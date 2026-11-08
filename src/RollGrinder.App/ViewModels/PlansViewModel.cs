using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Library;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>方案列表的一行（设计稿 v4 5.5.1）。</summary>
public sealed record PlanRowViewModel(
    PlanOverview Overview,
    string NoText,
    string NameText,
    string PurposeText,
    string SourceText,
    string NominalText,
    string UsedText,
    string LastText,
    string CreatedText)
{
    /// <summary>与来源（模板）：＝ 一致 / ≠ 已改动；不是从模板来的为"—"。</summary>
    public string ConsistencyText { get; init; } = "—";

    public string PlanId => Overview.Plan.PlanId;

    public bool IsCurrent => Overview.IsCurrent;

    public bool IsUsed => Overview.IsUsed;
}

/// <summary>
/// 方案区入口：一支辊的磨削方案（设计稿 v4 5.5.1，设计说明第 4 节）。
/// 只列"这支辊"的方案：★ 当前、用了几次（0 = 没用过，可改可删）、来源、名义长度。
/// 竖键：选轧辊 · 打开 ▸（辊形 / 工艺）· 新建方案…（复制选中的）· 复制来… · 对比 · 命名… · 设为当前… · 删除…（只对没用过的）。
/// 方案用过即冻结：在辊形 / 工艺页改它，保存时自动另存为新方案。
/// </summary>
public sealed partial class PlansViewModel : PageViewModelBase
{
    private readonly IGrindingPlanService plans;
    private readonly IRollRepository rolls;
    private readonly IRollProfileRepository profileTemplates;
    private readonly IProgramRepository programTemplates;
    private readonly IUserSession session;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly HmiSettings settings;
    private readonly JobDraft draft;
    private readonly FunctionKeyViewModel listKey;
    private readonly FunctionKeyViewModel compareKey;
    private readonly List<FunctionKeyViewModel> selectionKeys = new();
    private readonly FunctionKeyViewModel?[] rootKeys;
    private readonly FunctionKeyViewModel copyKey;
    private int loadVersion;

    public PlansViewModel(
        IGrindingPlanService plans,
        IRollRepository rolls,
        IRollProfileRepository profileTemplates,
        IProgramRepository programTemplates,
        IUserSession session,
        RollProfileTypeRegistry profileTypes,
        HmiSettings settings,
        JobDraft draft,
        PlanPickerViewModel picker,
        CompareViewModel compare,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.profileTemplates = profileTemplates ?? throw new ArgumentNullException(nameof(profileTemplates));
        this.programTemplates = programTemplates ?? throw new ArgumentNullException(nameof(programTemplates));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.draft = draft ?? throw new ArgumentNullException(nameof(draft));
        Picker = picker ?? throw new ArgumentNullException(nameof(picker));
        Compare = compare ?? throw new ArgumentNullException(nameof(compare));
        Picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlanPickerViewModel.CanCopy) && IsPicking)
            {
                (this.copyKey?.Command as IRelayCommand)?.NotifyCanExecuteChanged();
                Interaction.Hint(CopyHint());
            }
        };
        this.copyKey = new FunctionKeyViewModel("Vk_CopyToRoll", new AsyncRelayCommand(CopySelectedSourceAsync, () => Picker.CanCopy), localizer,
            FunctionKeyKind.Confirm, requiresEditable: true)
        {
            PreconditionResourceKey = "Copy_CannotCopy",
        };
        NamePrompt = new NamePromptViewModel(localizer);
        AttachPrompt(NamePrompt);

        // 横键（设计稿 v4 方案区）：方案列表 · 辊形 · 工艺 · 对比。
        this.listKey = FunctionKeyViewModel.ForAction("Fn_PlanList", localizer, () => Navigator.CloseSubView());
        this.compareKey = new FunctionKeyViewModel("Fn_Compare", new AsyncRelayCommand(OpenCompareAsync, HasSelection), localizer)
        {
            PreconditionResourceKey = "Plan_NothingSelected",
        };
        FunctionKeyViewModel?[] horizontal =
        {
            this.listKey,
            new FunctionKeyViewModel("Fn_PlanProfile", new RelayCommand(() => OpenEditor(PageKey.Profile), HasSelection), localizer)
            {
                PreconditionResourceKey = "Plan_NothingSelected",
            },
            new FunctionKeyViewModel("Fn_PlanProgram", new RelayCommand(() => OpenEditor(PageKey.Steps), HasSelection), localizer)
            {
                PreconditionResourceKey = "Plan_NothingSelected",
            },
            this.compareKey,
        };
        SetFunctionKeys(horizontal);
        MarkActiveFunctionKey(this.listKey);

        FunctionKeyViewModel?[] vertical =
        {
            FunctionKeyViewModel.ForAction("Vk_PickRoll", localizer, () => Navigator.GoTo(PageKey.Rolls)),
            new FunctionKeyViewModel("Vk_OpenPlan", new RelayCommand(OpenEditorMenu, HasSelection), localizer)
            {
                PreconditionResourceKey = "Plan_NothingSelected",
            },
            new FunctionKeyViewModel("Vk_NewPlan", new RelayCommand(AskNewPlan, HasSelection), localizer, requiresEditable: true)
            {
                PreconditionResourceKey = "Plan_NothingSelected",
            },
            new FunctionKeyViewModel("Vk_CopyFrom", new RelayCommand(() => _ = OpenCopyAsync(CopyRange.OtherRolls), () => RollId is not null), localizer,
                requiresEditable: true)
            {
                PreconditionResourceKey = "Plan_PickRollHint",
            },
            new FunctionKeyViewModel("Vk_Compare", new AsyncRelayCommand(OpenCompareAsync, HasSelection), localizer)
            {
                PreconditionResourceKey = "Plan_NothingSelected",
            },
            new FunctionKeyViewModel("Vk_RenamePlan", new RelayCommand(AskRename, HasSelection), localizer, requiresEditable: true)
            {
                PreconditionResourceKey = "Plan_NothingSelected",
            },
            new FunctionKeyViewModel("Vk_SetCurrentPlan", new RelayCommand(AskSetCurrent, () => SelectedRow is { IsCurrent: false }), localizer)
            {
                RequiredPermission = Permission.EditRollPlans,
                PreconditionResourceKey = "Plan_AlreadyCurrent",
            },
            new FunctionKeyViewModel("Vk_DeletePlan", new RelayCommand(AskDelete, () => SelectedRow is { IsUsed: false, IsCurrent: false }), localizer, requiresEditable: true)
            {
                PreconditionResourceKey = "Plan_DeleteOnlyUnused",
            },
        };
        this.rootKeys = vertical;
        SetVerticalKeys(vertical);
        this.selectionKeys.AddRange(vertical.Skip(1).Concat(horizontal.Skip(1)).OfType<FunctionKeyViewModel>());

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveSubViewKey))
            {
                OnPropertyChanged(nameof(IsCompareOpen));
                ApplySubViewKeys();
            }
        };
    }

    // ───────────── 对比（设计稿 v4 5.5.1 竖键"对比"） ─────────────

    /// <summary>对比子视图：选中的方案与当前方案（选中的就是当前时，与最近用过 / 建的另一个）逐项比、辊形叠图。</summary>
    public const string CompareSubView = "SubView_Compare";

    public bool IsCompareOpen => ActiveSubViewKey == CompareSubView;

    public CompareViewModel Compare { get; }

    /// <summary>打开 ▸：辊形 / 工艺。</summary>
    private void OpenEditorMenu() => OpenVerticalMenu("Vk_OpenPlan", new FunctionKeyViewModel?[]
    {
        MenuChoice("Vk_OpenPlanProfile", () => OpenEditor(PageKey.Profile), requiresEditable: false, canChoose: HasSelection),
        MenuChoice("Vk_OpenPlanProgram", () => OpenEditor(PageKey.Steps), requiresEditable: false, canChoose: HasSelection),
    });

    /// <summary>选中的方案和谁比：不是当前的就和当前比；是当前的（或这支辊没有当前）就和最近用过 / 建的另一个比。</summary>
    public PlanRowViewModel? CompareBaseline(PlanRowViewModel selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return (selected.IsCurrent ? null : Rows.FirstOrDefault(row => row.IsCurrent))
            ?? Rows.Where(row => row.PlanId != selected.PlanId)
                .OrderByDescending(row => row.Overview.Usage.LastUsedUtc ?? row.Overview.Plan.CreatedAtUtc)
                .FirstOrDefault();
    }

    private async Task OpenCompareAsync()
    {
        if (SelectedRow is not { } selected)
        {
            Interaction.Refuse(Localizer["Plan_NothingSelected"]);
            return;
        }

        if (CompareBaseline(selected) is not { } baseline)
        {
            Interaction.Refuse(Localizer["Compare_NeedTwoPlans"]);
            return;
        }

        await RunGuardedAsync(
            async token =>
            {
                CompareSide? a = await SideAsync(baseline, token).ConfigureAwait(true);
                CompareSide? b = await SideAsync(selected, token).ConfigureAwait(true);
                if (a is null || b is null)
                {
                    Interaction.Refuse(Localizer["PlanProblem_Missing"]);
                    return;
                }

                Compare.Load(a, b);
                Navigator.OpenSubView(CompareSubView);
                Interaction.Hint(Compare.Summary);
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    private async Task<CompareSide?> SideAsync(PlanRowViewModel row, CancellationToken token)
    {
        if (await this.plans.GetContentAsync(row.PlanId, token).ConfigureAwait(true) is not { } content)
        {
            return null;
        }

        IReadOnlyList<PlanParamRange> ranges = await this.plans.GetRangesAsync(row.PlanId, token).ConfigureAwait(true);
        return new CompareSide(
            Localizer.Format("Compare_PlanTitleFormat", row.NoText, content.Plan.Name),
            content.Profile,
            content.Program,
            ranges,
            content.Plan.EndZoneMm);
    }

    /// <summary>对比开着：1 只看不同 · 2 全部 · 8 返回；收起换回根层。</summary>
    private void ApplySubViewKeys()
    {
        if (!IsCompareOpen)
        {
            SetVerticalKeys(this.rootKeys);
            RefreshKeyStates();
            MarkActiveFunctionKey(this.listKey);
            return;
        }

        SetVerticalKeys(CompareKeys(Compare, Localizer, Navigator.CloseSubView));
        MarkActiveFunctionKey(this.compareKey);
    }

    /// <summary>对比子视图的竖键（方案区、模板区共用）：1 只看不同 · 2 全部 · 8 返回。</summary>
    internal static FunctionKeyViewModel?[] CompareKeys(CompareViewModel compare, IStringLocalizer localizer, Action back)
    {
        FunctionKeyViewModel? differences = null;
        FunctionKeyViewModel? all = null;
        void Show(bool only)
        {
            compare.OnlyDifferences = only;
            differences!.IsActive = only;
            all!.IsActive = !only;
        }

        differences = FunctionKeyViewModel.ForAction("Vk_CompareDifferences", localizer, () => Show(true));
        all = FunctionKeyViewModel.ForAction("Vk_CompareAll", localizer, () => Show(false));
        differences.IsActive = compare.OnlyDifferences;
        all.IsActive = !compare.OnlyDifferences;
        return new[]
        {
            differences, all, null, null, null, null, null,
            new FunctionKeyViewModel("Vk_Back", new RelayCommand(back), localizer, FunctionKeyKind.Navigation),
        };
    }

    public override PageKey Key => PageKey.Plans;

    public override string TitleResourceKey => "Page_Plans";

    /// <summary>离线可用：只和数据库打交道。</summary>
    public override bool WorksOffline => true;

    /// <summary>方案归工艺员（管理员）维护；操作者只看。</summary>
    public override Permission? EditPermission => Permission.EditProfiles;

    /// <summary>新方案名、改名的命名框（3 种弹出框之一）。</summary>
    public NamePromptViewModel NamePrompt { get; }

    public override bool HasModalPrompt => NamePrompt.IsOpen || IsPicking;

    public override bool TryDismissPrompt()
    {
        if (IsPicking)
        {
            CloseCopy();
            return true;
        }

        if (!NamePrompt.IsOpen)
        {
            return false;
        }

        NamePrompt.CancelCommand.Execute(null);
        return true;
    }

    /// <summary>复制来源子视图（设计稿 v4 5.5.5，与作业换方案同一个选方案子视图）。</summary>
    public PlanPickerViewModel Picker { get; }

    /// <summary>复制来源子视图开着。</summary>
    [ObservableProperty]
    private bool isPicking;

    // ───────────── 复制来（设计稿 v4 5.5.5） ─────────────

    /// <summary>打开复制来源：本辊 / 其他辊 / 模板，按这支辊的名义长度预览对齐。</summary>
    private async Task OpenCopyAsync(CopyRange range)
    {
        if (RollId is not { } id || await this.rolls.GetAsync(id, CancellationToken.None).ConfigureAwait(true) is not { } roll)
        {
            return;
        }

        CopyScope scope = IsPicking ? Picker.Scope : CopyScope.All;
        await RunGuardedAsync(
            token => Picker.LoadCopySourcesAsync(id, roll.Geometry.BodyLengthMm, roll.MeasuredBodyLengthMm, roll.Kind, range, scope, token),
            CancellationToken.None).ConfigureAwait(true);
        IsPicking = true;
        ApplyCopyKeys();
    }

    private void ApplyCopyKeys()
    {
        FunctionKeyViewModel Range(string key, CopyRange range) =>
            new(key, new RelayCommand(() => _ = OpenCopyAsync(range)), Localizer) { IsActive = Picker.Range == range };
        SetVerticalKeys(new FunctionKeyViewModel?[]
        {
            Range("Vk_CopyRangeThisRoll", CopyRange.ThisRoll),
            Range("Vk_CopyRangeOtherRolls", CopyRange.OtherRolls),
            Range("Vk_CopyRangeTemplates", CopyRange.Templates),
            null,
            FunctionKeyViewModel.ForAction("Vk_CopyScope", Localizer, OpenScopeMenu),
        });
        SetCommitPair(new FunctionKeyViewModel("Vk_Cancel", new RelayCommand(CloseCopy), Localizer, FunctionKeyKind.Cancel), this.copyKey);
        (this.copyKey.Command as IRelayCommand)?.NotifyCanExecuteChanged();
        Interaction.Hint(CopyHint());
    }

    /// <summary>复制范围 ▸（P10）：全部 / 只辊形 / 只工艺。</summary>
    private void OpenScopeMenu() => OpenVerticalMenu("Vk_CopyScope", new FunctionKeyViewModel?[]
    {
        MenuChoice("CopyScope_All", () => _ = Picker.SetScopeAsync(CopyScope.All, CancellationToken.None), requiresEditable: false),
        MenuChoice("CopyScope_ProfileOnly", () => _ = Picker.SetScopeAsync(CopyScope.ProfileOnly, CancellationToken.None), requiresEditable: false),
        MenuChoice("CopyScope_ProgramOnly", () => _ = Picker.SetScopeAsync(CopyScope.ProgramOnly, CancellationToken.None), requiresEditable: false),
    });

    private string CopyHint() => Picker.CopyResult switch
    {
        null => Localizer["Copy_HintPick"],
        { ProblemKey: { } problem } => Localizer.Format("Copy_HintRefusedFormat", Localizer[problem]),
        { NeedsConfirm: true } => Localizer["Copy_HintConfirm"],
        _ => Localizer["Copy_HintOk"],
    };

    private void CloseCopy()
    {
        IsPicking = false;
        SetCommitPair(null, null);
        SetVerticalKeys(this.rootKeys);
        RefreshKeyStates();
        Interaction.Hint(Localizer["Plan_FrozenHint"]);
    }

    /// <summary>✓ 复制到本辊：看过叠图就是确认；复制出的方案不设为当前（I17）。</summary>
    private async Task CopySelectedSourceAsync()
    {
        if (RollId is not { } id || Picker.SelectedCopySource is not { } source || !Picker.CanCopy)
        {
            return;
        }

        CopyScope scope = Picker.Scope;
        await RunGuardedAsync(
            async token =>
            {
                PlanCreateOutcome outcome = await this.plans
                    .CopyToRollAsync(id, source, scope, confirmed: true, setCurrent: false, UserName, token).ConfigureAwait(true);
                if (outcome.Plan is not { } created)
                {
                    Interaction.Refuse(Localizer[outcome.ProblemKey ?? "PlanProblem_Missing"]);
                    return;
                }

                CloseCopy();
                await ReloadAsync(created.PlanId, token).ConfigureAwait(true);
                Say("Copy_DoneFormat", created.PlanNo, Picker.CopyResult?.SourceText ?? "--");
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>列的是哪支辊；还没选为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoll))]
    private string? rollId;

    public bool HasRoll => RollId is not null;

    /// <summary>表头："R-2026-031 的方案 · 共 4 个 · 当前 方案 3"。</summary>
    [ObservableProperty]
    private string listTitle = string.Empty;

    public ObservableCollection<PlanRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private PlanRowViewModel? selectedRow;

    /// <summary>选中方案的摘要。</summary>
    public ObservableCollection<LabelValueViewModel> DetailRows { get; } = new();

    [ObservableProperty]
    private string detailTitle = string.Empty;

    /// <summary>选中方案的辊形（横轴以辊身中心为 0，直径量 µm）。</summary>
    public IReadOnlyList<(double CenteredZMm, double DiameterMicrometer)> PreviewCurve { get; private set; } = Array.Empty<(double, double)>();

    /// <summary>预览曲线的名义长度。</summary>
    public double PreviewLengthMm { get; private set; } = 1.0;

    /// <summary>预览变了（视图重画）。</summary>
    public event EventHandler? PreviewChanged;

    /// <summary>最近一次刷新的任务（自检等它）。</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    private string UserName => this.session.CurrentUser?.UserName ?? string.Empty;

    public override void OnActivated()
    {
        if (this.draft.PlanRollId is { } requested)
        {
            this.draft.PlanRollId = null;
            RollId = requested;
        }

        Interaction.Hint(RollId is null ? Localizer["Plan_PickRollHint"] : Localizer["Plan_FrozenHint"]);
        Loading = RunGuardedAsync(token => ReloadAsync(SelectedRow?.PlanId, token), CancellationToken.None);
    }

    partial void OnSelectedRowChanged(PlanRowViewModel? value)
    {
        RefreshKeyStates();
        _ = RunRefreshAsync(token => ShowDetailAsync(value, token), CancellationToken.None);
    }

    private bool HasSelection() => SelectedRow is not null;

    /// <summary>重读这支辊的方案；<paramref name="keep"/> 选回原来那一行。</summary>
    public async Task ReloadAsync(string? keep, CancellationToken cancellationToken)
    {
        int version = ++this.loadVersion;
        Rows.Clear();
        if (RollId is not { } id)
        {
            ListTitle = Localizer["Plan_NoRoll"];
            SelectedRow = null;
            return;
        }

        RollRecord? roll = await this.rolls.GetAsync(id, cancellationToken).ConfigureAwait(true);
        IReadOnlyList<PlanOverview> list = await this.plans.ListAsync(id, cancellationToken).ConfigureAwait(true);
        if (version != this.loadVersion)
        {
            return;
        }

        foreach (PlanOverview overview in list)
        {
            Rows.Add(await RowAsync(overview, cancellationToken).ConfigureAwait(true));
        }

        PlanOverview? current = list.FirstOrDefault(row => row.IsCurrent);
        ListTitle = Localizer.Format(
            "Plan_ListTitleFormat",
            id,
            roll is null ? "--" : Localizer["RollKind_" + roll.Kind],
            roll?.Purpose ?? "--",
            list.Count,
            current is null ? "--" : current.Plan.PlanNo.ToString(CultureInfo.CurrentCulture));
        SelectedRow = Rows.FirstOrDefault(row => row.PlanId == keep) ?? Rows.FirstOrDefault();
        if (SelectedRow is null)
        {
            await ShowDetailAsync(null, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>一行：模板来源的方案标"＝ 一致 / ≠ 已改动"，模板出了新版的辊形 · 工艺后附"↑ 模板已更新"。</summary>
    private async Task<PlanRowViewModel> RowAsync(PlanOverview overview, CancellationToken cancellationToken)
    {
        GrindingPlanRecord plan = overview.Plan;
        bool fromTemplate = plan.TemplateProfileId is not null || plan.TemplateProgramId is not null;
        bool diverged = plan.ProfileDiverged || plan.ProgramDiverged;
        bool updated =
            (plan.TemplateProfileId is { } p && await this.profileTemplates.GetAsync(p, cancellationToken).ConfigureAwait(true) is { } profile
                && profile.Version > (plan.TemplateProfileVersion ?? profile.Version))
            || (plan.TemplateProgramId is { } g && await this.programTemplates.GetAsync(g, cancellationToken).ConfigureAwait(true) is { } program
                && program.Version > (plan.TemplateProgramVersion ?? program.Version));
        return new PlanRowViewModel(
            overview,
            (overview.IsCurrent ? "★ " : string.Empty) + plan.PlanNo.ToString(CultureInfo.CurrentCulture),
            overview.ProfileName + " · " + overview.ProgramName + (updated ? "  " + Localizer["Plan_TemplateUpdated"] : string.Empty),
            plan.Purpose ?? "--",
            SourceText(plan),
            overview.NominalLengthMm.ToString("F0", CultureInfo.CurrentCulture),
            overview.Usage.UseCount.ToString(CultureInfo.CurrentCulture),
            overview.Usage.LastUsedUtc?.ToLocalTime().ToString("MM-dd", CultureInfo.CurrentCulture) ?? "--",
            plan.CreatedAtUtc.ToLocalTime().ToString("MM-dd", CultureInfo.CurrentCulture) + " " + plan.CreatedBy)
        {
            ConsistencyText = !fromTemplate ? "—" : Localizer[diverged ? "Plan_Diverged" : "Plan_Consistent"],
        };
    }

    /// <summary>来源一句话：空白新建、复制自本辊方案 n、模板、作业 J 的调整、旧计划。</summary>
    public string SourceText(GrindingPlanRecord plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Localizer.Format("PlanSource_" + plan.SourceKind, plan.SourceRef ?? "--");
    }

    private async Task ShowDetailAsync(PlanRowViewModel? row, CancellationToken cancellationToken)
    {
        DetailRows.Clear();
        PreviewCurve = Array.Empty<(double, double)>();
        DetailTitle = row is null ? string.Empty : Localizer.Format("Plan_DetailTitleFormat", row.Overview.Plan.PlanNo, row.Overview.Plan.Name);
        if (row is not null && await this.plans.GetContentAsync(row.PlanId, cancellationToken).ConfigureAwait(true) is { } content
            && SelectedRow?.PlanId == row.PlanId)
        {
            (double head, double tail) = content.Plan.EndZoneMm is double zone
                ? (zone, zone)
                : ProfileLengthAlignment.DefaultEndZones(content.Profile.Profile);
            DetailRows.Add(new LabelValueViewModel("Plan_DetailProfile", content.Profile.Name
                + (content.Profile.ToleranceMicrometer is double tol ? Localizer.Format("Plan_ToleranceFormat", tol) : string.Empty), Localizer));
            DetailRows.Add(new LabelValueViewModel("Plan_DetailProgram", content.Program.Name, Localizer));
            DetailRows.Add(new LabelValueViewModel("Plan_DetailNominal", content.Profile.BodyLengthMm.ToString("F1", CultureInfo.CurrentCulture), Localizer));
            DetailRows.Add(new LabelValueViewModel("Plan_DetailEndZones", Localizer.Format("Plan_EndZonesFormat", head, tail), Localizer));
            DetailRows.Add(new LabelValueViewModel("Plan_DetailSource", SourceText(content.Plan), Localizer));
            DetailRows.Add(new LabelValueViewModel("Plan_DetailUsed", row.IsUsed
                ? Localizer.Format("Plan_UsedFormat", row.Overview.Usage.UseCount, row.LastText)
                : Localizer["Plan_NotUsed"], Localizer));
            DetailRows.Add(new LabelValueViewModel("Plan_DetailCreated", row.CreatedText, Localizer));

            double length = content.Profile.BodyLengthMm;
            PreviewLengthMm = length;
            PreviewCurve = content.Profile.Profile
                .Compose(RollGeometry.FromDiameter(length, RollGeometry.PreviewDiameterMm), this.profileTypes, this.settings.ProfileSampleCount)
                .Points
                .Select(point => (point.BodyPositionMm - (length / 2.0), UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
                .ToArray();
        }

        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshKeyStates()
    {
        foreach (FunctionKeyViewModel key in this.selectionKeys)
        {
            (key.Command as IRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    // ───────────── 动作 ─────────────

    /// <summary>到辊形 / 工艺页打开选中的方案。</summary>
    private void OpenEditor(PageKey page)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        this.draft.PlanToEdit = row.PlanId;
        Navigator.GoTo(page);
    }

    /// <summary>新建方案：复制选中的方案（新编号，来源记为它），先起名字；不设为当前。</summary>
    private void AskNewPlan()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string planId = row.PlanId;
        NamePrompt.Open(
            Localizer["Plan_NewTitle"],
            Localizer.Format("Library_CopyNameFormat", row.Overview.Plan.Name),
            async (name, _, token) =>
            {
                if (await this.plans.NameTakenAsync(RollId!, name, null, token).ConfigureAwait(true))
                {
                    return NamePromptOutcome.Refused(Localizer.Format("Plan_NameTakenFormat", name));
                }

                GrindingPlanRecord created = await this.plans.CopyAsync(planId, name, setCurrent: false, UserName, token).ConfigureAwait(true);
                await ReloadAsync(created.PlanId, token).ConfigureAwait(true);
                Say("Plan_CreatedFormat", created.PlanNo, row.Overview.Plan.PlanNo);
                return NamePromptOutcome.Done;
            });
    }

    /// <summary>命名：名字在同一支辊内不能重复（5.5.6）。</summary>
    private void AskRename()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        string planId = row.PlanId;
        string? purpose = row.Overview.Plan.Purpose;
        NamePrompt.Open(
            Localizer["Plan_RenameTitle"],
            row.Overview.Plan.Name,
            async (name, _, token) =>
            {
                if (!await this.plans.RenameAsync(planId, name, purpose, UserName, token).ConfigureAwait(true))
                {
                    return NamePromptOutcome.Refused(Localizer.Format("Plan_NameTakenFormat", name));
                }

                await ReloadAsync(planId, token).ConfigureAwait(true);
                return NamePromptOutcome.Done;
            });
    }

    /// <summary>设为当前：问一句（不自动取消），记一条改动。</summary>
    private void AskSetCurrent()
    {
        if (SelectedRow is not { IsCurrent: false } row)
        {
            return;
        }

        string planId = row.PlanId;
        Interaction.Choose(
            Localizer.Format("Plan_AskSetCurrentFormat", row.Overview.Plan.PlanNo, row.Overview.Plan.Name, RollId ?? "--"),
            "Vk_SetCurrentPlanConfirm",
            () => RunGuardedAsync(
                async token =>
                {
                    await this.plans.SetCurrentAsync(planId, null, UserName, token).ConfigureAwait(true);
                    await ReloadAsync(planId, token).ConfigureAwait(true);
                    Say("Plan_SetCurrentDoneFormat", row.Overview.Plan.PlanNo);
                },
                CancellationToken.None));
    }

    /// <summary>删除：只对没用过、不是当前的方案。</summary>
    private void AskDelete()
    {
        if (SelectedRow is not { IsUsed: false, IsCurrent: false } row)
        {
            return;
        }

        string planId = row.PlanId;
        Ask("Plan_AskDeleteFormat",
            () => RunGuardedAsync(
                async token =>
                {
                    if (await this.plans.DeleteAsync(planId, UserName, token).ConfigureAwait(true) is { } problem)
                    {
                        Interaction.Refuse(Localizer[problem]);
                        return;
                    }

                    await ReloadAsync(null, token).ConfigureAwait(true);
                },
                CancellationToken.None),
            row.Overview.Plan.PlanNo,
            row.Overview.Plan.Name);
    }
}
