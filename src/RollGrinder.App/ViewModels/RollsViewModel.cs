using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Records;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>台账列表的一行。</summary>
public sealed partial class RollRowViewModel : ObservableObject
{
    public RollRowViewModel(RollRecord roll, string kindText, string markText, string remainingText, bool lowLife)
    {
        Roll = roll;
        KindText = kindText;
        MarkText = markText;
        RemainingText = remainingText;
        IsLowLife = lowLife;
    }

    public RollRecord Roll { get; }

    public string RollId => Roll.RollId;

    public string KindText { get; }

    public string PurposeText => Roll.Purpose ?? "--";

    public string CurrentText => Roll.StartDiameterMm.ToString("F2", CultureInfo.CurrentCulture);

    /// <summary>剩余可磨量（mm）；没登记报废直径为 "--"。</summary>
    public string RemainingText { get; }

    /// <summary>标记：中断 / 返磨 / 临报废 / 作废 / 推断。</summary>
    public string MarkText { get; }

    public bool IsLowLife { get; }

    public bool IsRetired => Roll.Retired;

    /// <summary>多选时勾上了。</summary>
    [ObservableProperty]
    private bool isChecked;
}

/// <summary>磨削履历的一行。</summary>
public sealed record RollHistoryRowViewModel(string DateText, string ProfileText, string RemovedText, string ResultText, string MarkText, string JobId);

/// <summary>改计划逐支核对的一行。</summary>
public sealed record PlanCheckRowViewModel(string RollId, string PurposeText, string ResultText, bool Changes);

/// <summary>导入预览的一行。</summary>
public sealed record ImportRowViewModel(int LineNumber, string KindText, string RollId, string BodyText, string ProfileText, string ProblemText, bool IsError);

/// <summary>
/// 轧辊区（界面修订稿 v3 6.4）：以轧辊为中心——台账带计划（目标辊形 + 磨削程序）、用途、寿命、履历。
///
/// 根画面：左边在用的辊（中断 / 返磨 / 临报废标记），右边选中那支的卡片与磨削履历。
/// 子视图：新登记 / 编辑（计划必填，"选辊形… / 选程序…"打开与作业同一个选择子视图）、多选（取代"批量改计划"页）、
/// 改计划（逐支或多支同一条路，逐支核对、各记改动）、导入预览（先预览、有错的行不导入）。
/// </summary>
public sealed partial class RollsViewModel : PageViewModelBase
{
    public const string FormSubView = "SubView_RollForm";
    public const string MultiSubView = "SubView_MultiSelect";
    public const string ChangePlanSubView = "SubView_ChangePlan";
    public const string ImportSubView = "SubView_ImportPreview";

    private const int HistoryLimit = 50;

    private readonly IRollRepository rolls;
    private readonly IRollLedgerService ledger;
    private readonly IRollPlanningService planning;
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IJobRepository jobs;
    private readonly IUserSession session;
    private readonly JobDraft draft;
    private readonly FunctionKeyViewModel ledgerKey;
    private readonly FunctionKeyViewModel multiKey;
    private readonly FunctionKeyViewModel showRetiredKey;
    private readonly FunctionKeyViewModel retireKey;
    private readonly FunctionKeyViewModel onlyErrorsKey;
    private readonly FunctionKeyViewModel cancelKey;
    private readonly FunctionKeyViewModel saveKey;
    private readonly FunctionKeyViewModel applyPlanKey;
    private readonly FunctionKeyViewModel importKey;
    private readonly FunctionKeyViewModel pickKey;
    private IReadOnlyList<LedgerImportRow> importRows = Array.Empty<LedgerImportRow>();
    private RollRecord? editing;
    private PickTarget pickTarget;
    private bool registeringForJob;

    private enum PickTarget
    {
        FormProfile,
        FormProgram,
        PlanProfile,
        PlanProgram,
    }

    public RollsViewModel(
        IRollRepository rolls,
        IRollLedgerService ledger,
        IRollPlanningService planning,
        IRollProfileRepository profiles,
        IProgramRepository programs,
        IJobRepository jobs,
        IUserSession session,
        PlanPickerViewModel picker,
        JobDraft draft,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.planning = planning ?? throw new ArgumentNullException(nameof(planning));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        Picker = picker ?? throw new ArgumentNullException(nameof(picker));
        this.draft = draft ?? throw new ArgumentNullException(nameof(draft));

        this.ledgerKey = FunctionKeyViewModel.ForAction("Fn_Ledger", localizer, () => Navigator.CloseSubView());
        SetFunctionKeys(new FunctionKeyViewModel?[]
        {
            this.ledgerKey,
            new FunctionKeyViewModel("Fn_ImportLedger", new AsyncRelayCommand(PickImportFileAsync), localizer) { RequiredPermission = Permission.EditRollPlans },
            new FunctionKeyViewModel("Fn_ExportLedger", new AsyncRelayCommand(() => ExportAsync(template: false)), localizer),
        });
        MarkActiveFunctionKey(this.ledgerKey);

        this.multiKey = FunctionKeyViewModel.ForAction("Vk_MultiSelect", localizer, ToggleMulti);
        this.showRetiredKey = FunctionKeyViewModel.ForAction("Vk_ShowRetired", localizer, () => _ = ToggleRetiredAsync());
        this.retireKey = new FunctionKeyViewModel("Vk_Retire", new RelayCommand(AskRetire, HasSelection), localizer)
        {
            RequiredPermission = Permission.EditRollPlans,
            PreconditionResourceKey = "Rolls_NothingSelected",
        };
        this.onlyErrorsKey = FunctionKeyViewModel.ForAction("Vk_OnlyErrors", localizer, ToggleOnlyErrors);
        this.cancelKey = new FunctionKeyViewModel("Vk_Cancel", new RelayCommand(Navigator.CloseSubView), localizer, FunctionKeyKind.Cancel);
        this.saveKey = new FunctionKeyViewModel("Vk_Save", new AsyncRelayCommand(SaveFormAsync), localizer, FunctionKeyKind.Confirm) { RequiredPermission = Permission.EditJobs };
        this.applyPlanKey = new FunctionKeyViewModel("Vk_ApplyPlanFormat", new AsyncRelayCommand(ApplyPlanAsync), localizer, FunctionKeyKind.Confirm)
        {
            RequiredPermission = Permission.EditRollPlans,
        };
        this.importKey = new FunctionKeyViewModel("Vk_ImportRowsFormat", new AsyncRelayCommand(ImportAsync), localizer, FunctionKeyKind.Confirm)
        {
            RequiredPermission = Permission.EditRollPlans,
        };
        this.pickKey = new FunctionKeyViewModel("Vk_PickThis", new RelayCommand(ConfirmPick), localizer, FunctionKeyKind.Confirm);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveSubViewKey))
            {
                OnSubViewChanged();
            }
        };
        ApplyKeys();
    }

    public override PageKey Key => PageKey.Rolls;

    public override string TitleResourceKey => "Page_Rolls";

    public override bool WorksOffline => true;

    /// <summary>登记、改尺寸：操作者即可（关系设计第 8 节）；改计划、导入、作废另点名管理员。</summary>
    public override Permission? EditPermission => Permission.EditJobs;

    /// <summary>选辊形 / 选程序子视图（与作业共用）。</summary>
    public PlanPickerViewModel Picker { get; }

    // ───────────── 台账 ─────────────

    public ObservableCollection<RollRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private RollRowViewModel? selectedRow;

    /// <summary>按用途筛选（null = 全部）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListTitle))]
    private string? purposeFilter;

    /// <summary>显示作废的辊（表头开关，与库的"显示停用"一致）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListTitle))]
    private bool showRetired;

    public string ListTitle => Localizer.Format(
        "Rolls_ListTitleFormat",
        PurposeFilter ?? Localizer["Rolls_AllPurposes"],
        Rows.Count(row => !row.IsRetired),
        Localizer[ShowRetired ? "Rolls_RetiredShown" : "Rolls_RetiredHidden"]);

    /// <summary>选中那支辊的卡片（尺寸、计划、材质、重量、次数、累计去除）。</summary>
    public ObservableCollection<LabelValueViewModel> CardRows { get; } = new();

    [ObservableProperty]
    private string cardTitle = string.Empty;

    /// <summary>剩余可磨的比例（0–1），寿命条用。</summary>
    [ObservableProperty]
    private double remainingFraction;

    [ObservableProperty]
    private string remainingText = "--";

    public ObservableCollection<RollHistoryRowViewModel> History { get; } = new();

    /// <summary>已勾选几支（多选）。</summary>
    public int CheckedCount => Rows.Count(row => row.IsChecked);

    public bool IsMulti => ActiveSubViewKey is MultiSubView or ChangePlanSubView;

    public bool IsForm => ActiveSubViewKey == FormSubView;

    public bool IsChangePlan => ActiveSubViewKey == ChangePlanSubView;

    public bool IsImport => ActiveSubViewKey == ImportSubView;

    public bool IsLedger => !IsForm && !IsChangePlan && !IsImport;

    /// <summary>选辊形 / 选程序子视图开着（盖住登记表或改计划的左半边）。</summary>
    [ObservableProperty]
    private bool isPicking;

    public override bool HasModalPrompt => IsPicking;

    public override bool TryDismissPrompt()
    {
        if (!IsPicking)
        {
            return false;
        }

        IsPicking = false;
        ApplyKeys();
        return true;
    }

    /// <summary>最近一次刷新的任务（自检等它）。</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public override void OnActivated()
    {
        if (this.draft.RegisterNewRollRequested)
        {
            // 作业派来登记：直接开一张新表；存好后"« 返回"回作业就选上它。
            this.draft.RegisterNewRollRequested = false;
            this.registeringForJob = true;
            string? prefill = this.draft.RegisterRollIdHint;
            this.draft.RegisterRollIdHint = null;
            Loading = RunGuardedAsync(
                async token =>
                {
                    await ReloadAsync(null, token).ConfigureAwait(true);
                    OpenForm(null, copyFrom: null, prefill);
                },
                CancellationToken.None);
            return;
        }

        Loading = RunGuardedAsync(token => ReloadAsync(SelectedRow?.RollId, token), CancellationToken.None);
    }

    partial void OnSelectedRowChanged(RollRowViewModel? value)
    {
        (this.retireKey.Command as IRelayCommand)?.NotifyCanExecuteChanged();
        this.retireKey.LabelResourceKey = value?.IsRetired == true ? "Vk_Restore" : "Vk_Retire";
        if (ActiveSubViewKey is null && !IsPicking)
        {
            // 根画面的键按选中的那支辊重排（下作业、编辑、复制登记……能不能按跟着变）。
            ApplyKeys();
        }

        if (value is not null)
        {
            _ = RunGuardedAsync(token => ShowCardAsync(value.Roll, token), CancellationToken.None);
        }
    }

    private bool HasSelection() => SelectedRow is not null;

    private string UserName => this.session.CurrentUser?.UserName ?? string.Empty;

    private async Task ReloadAsync(string? keep, CancellationToken cancellationToken)
    {
        IReadOnlyList<QueueEntry> queue = await this.planning.LoadQueueAsync(int.MaxValue, cancellationToken).ConfigureAwait(true);
        Dictionary<string, QueueEntry> byId = queue.ToDictionary(entry => entry.Roll.RollId, StringComparer.Ordinal);
        IEnumerable<RollRecord> all = (await this.rolls.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(true))
            .Where(roll => ShowRetired || !roll.Retired)
            .Where(roll => PurposeFilter is null || string.Equals(roll.Purpose?.Trim(), PurposeFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(roll => roll.Retired)
            .ThenBy(roll => roll.RollId, StringComparer.Ordinal);

        var checkedIds = Rows.Where(row => row.IsChecked).Select(row => row.RollId).ToHashSet(StringComparer.Ordinal);
        this.perGrindCache.Clear();
        Rows.Clear();
        foreach (RollRecord roll in all)
        {
            byId.TryGetValue(roll.RollId, out QueueEntry? entry);
            double? remaining = roll.ScrapDiameterMm is double scrap ? roll.StartDiameterMm - scrap : null;
            bool low = remaining is double r && await IsLowLifeAsync(roll, r, cancellationToken).ConfigureAwait(true);
            string mark = roll.Retired ? Localizer["Mark_Retired"]
                : entry?.Mark == QueueMark.Interrupted ? Localizer["Mark_Interrupted"]
                : entry?.Mark == QueueMark.Regrind ? Localizer["Mark_Regrind"]
                : low ? Localizer["Mark_LowLife"]
                : roll.PlanInferred ? Localizer["Mark_Inferred"]
                : string.Empty;
            Rows.Add(new RollRowViewModel(
                roll,
                Localizer["RollKind_" + roll.Kind],
                mark,
                remaining is double value ? value.ToString("F2", CultureInfo.CurrentCulture) : "--",
                low)
            {
                IsChecked = checkedIds.Contains(roll.RollId),
            });
        }

        foreach (RollRowViewModel row in Rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RollRowViewModel.IsChecked))
                {
                    OnPropertyChanged(nameof(CheckedCount));
                    ApplyKeys();
                }
            };
        }

        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(CheckedCount));
        SelectedRow = Rows.FirstOrDefault(row => row.RollId == keep) ?? Rows.FirstOrDefault();
        if (SelectedRow is null)
        {
            CardRows.Clear();
            History.Clear();
            CardTitle = string.Empty;
        }
    }

    private readonly Dictionary<string, double> perGrindCache = new(StringComparer.Ordinal);

    /// <summary>剩余可磨量不到两次标准余量（关系设计 5.6"临近报废"）。每支程序的标准余量一次刷新只读一遍。</summary>
    private async Task<bool> IsLowLifeAsync(RollRecord roll, double remainingMm, CancellationToken cancellationToken)
    {
        if (roll.ProgramId is not { } programId)
        {
            return false;
        }

        if (!this.perGrindCache.TryGetValue(programId, out double perGrind))
        {
            perGrind = await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(true) is { } program
                ? (program.StandardStockMicrometer ?? StockAdjustment.Apply(program.Steps, 1.0).ProgramStockMicrometer) / 1000.0
                : 0.0;
            this.perGrindCache[programId] = perGrind;
        }

        return perGrind > 0.0 && remainingMm < JobChecklist.LowLifeFactor * perGrind;
    }

    private async Task ShowCardAsync(RollRecord roll, CancellationToken cancellationToken)
    {
        CardTitle = Localizer.Format("Rolls_CardTitleFormat", roll.RollId, Localizer["RollKind_" + roll.Kind], roll.Purpose ?? "--");
        CardRows.Clear();
        History.Clear();

        string profileText = "--";
        if (roll.TargetProfileId is { } profileId && await this.profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(true) is { } profile)
        {
            profileText = Localizer.Format("Lib_NameVersionFormat", profile.Name, profile.Version) + (profile.Disabled ? " " + Localizer["Mark_Disabled"] : string.Empty);
        }

        string programText = "--";
        if (roll.ProgramId is { } programId && await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(true) is { } program)
        {
            programText = Localizer.Format("Lib_NameVersionFormat", program.Name, program.Version) + (program.Disabled ? " " + Localizer["Mark_Disabled"] : string.Empty);
        }

        IReadOnlyList<GrindingRecord> records = await this.ledger.HistoryAsync(roll.RollId, HistoryLimit, cancellationToken).ConfigureAwait(true);
        double removedTotal = 0.0;
        foreach (GrindingRecord record in records)
        {
            (GrindingJob Job, JobState State)? stored = await this.jobs.GetAsync(record.JobId, cancellationToken).ConfigureAwait(true);
            GrindingJob? job = stored?.Job;
            double? removed = job?.StockMicrometer / 1000.0;
            if (record.State == JobState.Completed && removed is double done)
            {
                removedTotal += done;
            }

            History.Add(new RollHistoryRowViewModel(
                record.StartedAtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture),
                job is null ? "--" : (job.ProfileName ?? "--") + (job.ProfileVersion is int v ? " v" + v.ToString(CultureInfo.InvariantCulture) : string.Empty),
                removed is double mm ? mm.ToString("F3", CultureInfo.CurrentCulture) : "--",
                ResultText(record),
                MarkFor(job, record.State),
                record.JobId));
        }

        CardRows.Add(new LabelValueViewModel("Rolls_BodyNominal", Localizer.Format("Rolls_PairFormat",
            roll.Geometry.BodyLengthMm.ToString("F0", CultureInfo.CurrentCulture), roll.Geometry.NominalDiameterMm.ToString("F1", CultureInfo.CurrentCulture)), Localizer));
        CardRows.Add(new LabelValueViewModel("Rolls_CurrentScrap", Localizer.Format("Rolls_PairFormat",
            roll.StartDiameterMm.ToString("F2", CultureInfo.CurrentCulture), roll.ScrapDiameterMm?.ToString("F1", CultureInfo.CurrentCulture) ?? "--"), Localizer));
        CardRows.Add(new LabelValueViewModel("Rolls_PlanProfile", profileText, Localizer));
        CardRows.Add(new LabelValueViewModel("Rolls_PlanProgram", programText, Localizer));
        CardRows.Add(new LabelValueViewModel("Rolls_GrindCount", records.Count.ToString(CultureInfo.CurrentCulture), Localizer));
        CardRows.Add(new LabelValueViewModel("Rolls_RemovedTotal", removedTotal.ToString("F3", CultureInfo.CurrentCulture) + " mm", Localizer));
        CardRows.Add(new LabelValueViewModel("Ledger_Material", roll.Material ?? "--", Localizer));
        CardRows.Add(new LabelValueViewModel("RollData_NetWeight", roll.Data.NetWeightKg?.ToString("F0", CultureInfo.CurrentCulture) ?? "--", Localizer));
        if (roll.PlanInferred)
        {
            CardRows.Add(new LabelValueViewModel("Rolls_PlanInferredLabel", Localizer["Rolls_PlanInferred"], Localizer));
        }

        if (roll.ScrapDiameterMm is double scrap && roll.Geometry.NominalDiameterMm > scrap)
        {
            double remaining = Math.Max(roll.StartDiameterMm - scrap, 0.0);
            RemainingFraction = Math.Clamp(remaining / (roll.Geometry.NominalDiameterMm - scrap), 0.0, 1.0);
            RemainingText = Localizer.Format("Rolls_RemainingFormat", remaining);
        }
        else
        {
            RemainingFraction = 0.0;
            RemainingText = Localizer["Rolls_RemainingUnknown"];
        }
    }

    private string ResultText(GrindingRecord record) => record.State switch
    {
        JobState.Interrupted => Localizer["Result_Interrupted"],
        JobState.Handed => Localizer["Result_Running"],
        _ => record.Passed switch
        {
            true => Localizer["Result_Pass"],
            false => Localizer["Result_Fail"],
            _ => "--",
        },
    };

    private string MarkFor(GrindingJob? job, JobState state)
    {
        if (job is null)
        {
            return string.Empty;
        }

        var marks = new List<string>();
        if (job.RegrindOfJobId is not null)
        {
            marks.Add(Localizer["Mark_Regrind"]);
        }

        if (job.Deviation == JobDeviation.ThisTimeOnly)
        {
            marks.Add(Localizer["Mark_ThisTime"] + (job.DeviationReason is { } reason ? "·" + ReasonText(reason) : string.Empty));
        }
        else if (job.Deviation == JobDeviation.PlanChanged)
        {
            marks.Add(Localizer["Mark_PlanChanged"]);
        }

        if (state == JobState.Interrupted)
        {
            marks.Add(Localizer["Mark_Interrupted"]);
        }

        return string.Join(" ", marks);
    }

    private string ReasonText(string reason) => reason.StartsWith("Reason_", StringComparison.Ordinal) ? Localizer[reason] : reason;

    // ───────────── 竖键编排 ─────────────

    private void OnSubViewChanged()
    {
        if (ActiveSubViewKey is null)
        {
            IsPicking = false;
            this.editing = null;
            if (this.registeringForJob)
            {
                this.registeringForJob = false;
            }

            foreach (RollRowViewModel row in Rows)
            {
                row.IsChecked = false;
            }

            _ = RunGuardedAsync(token => ReloadAsync(SelectedRow?.RollId, token), CancellationToken.None);
        }

        OnPropertyChanged(nameof(IsMulti));
        OnPropertyChanged(nameof(IsForm));
        OnPropertyChanged(nameof(IsChangePlan));
        OnPropertyChanged(nameof(IsImport));
        OnPropertyChanged(nameof(IsLedger));
        OnPropertyChanged(nameof(LowerTitle));
        ApplyKeys();
    }

    private void ApplyKeys()
    {
        if (IsPicking)
        {
            SetVerticalKeys(new FunctionKeyViewModel?[]
            {
                FunctionKeyViewModel.ForAction(Picker.ShowAll ? "Vk_ShowFitting" : "Vk_ShowAll", Localizer,
                    () => _ = RunGuardedAsync(Picker.ToggleShowAllAsync, CancellationToken.None)),
            });
            SetCommitPair(new FunctionKeyViewModel("Vk_Cancel", new RelayCommand(() => TryDismissPrompt()), Localizer, FunctionKeyKind.Cancel), this.pickKey);
            return;
        }

        switch (ActiveSubViewKey)
        {
            case FormSubView:
                SetVerticalKeys(new FunctionKeyViewModel?[]
                {
                    FunctionKeyViewModel.ForAction("Vk_PickKind", Localizer, OpenKindMenu),
                    FunctionKeyViewModel.ForAction("Vk_PickPurpose", Localizer, () => _ = OpenPurposeMenuAsync(forFilter: false)),
                    FunctionKeyViewModel.ForAction("Vk_PickProfile", Localizer, () => _ = OpenPickerAsync(PickTarget.FormProfile)),
                    FunctionKeyViewModel.ForAction("Vk_PickProgram", Localizer, () => _ = OpenPickerAsync(PickTarget.FormProgram)),
                });
                SetCommitPair(this.cancelKey, this.saveKey);
                break;

            case MultiSubView:
                SetVerticalKeys(new FunctionKeyViewModel?[]
                {
                    FunctionKeyViewModel.ForAction("Vk_CheckAll", Localizer, () => SetAllChecked(true)),
                    FunctionKeyViewModel.ForAction("Vk_CheckNone", Localizer, () => SetAllChecked(false)),
                    FunctionKeyViewModel.ForAction("Vk_FilterPurpose", Localizer, () => _ = OpenPurposeMenuAsync(forFilter: true)),
                    new FunctionKeyViewModel("Vk_ChangePlan", new RelayCommand(OpenChangePlan, () => CheckedCount > 0), Localizer)
                    {
                        RequiredPermission = Permission.EditRollPlans,
                        PreconditionResourceKey = "Rolls_NoneChecked",
                    },
                    this.multiKey,
                    null,
                    null,
                    new FunctionKeyViewModel("Vk_Back", new RelayCommand(Navigator.CloseSubView), Localizer, FunctionKeyKind.Navigation),
                });
                this.multiKey.IsActive = true;
                SetCommitPair(null, null);
                break;

            case ChangePlanSubView:
                SetVerticalKeys(new FunctionKeyViewModel?[]
                {
                    FunctionKeyViewModel.ForAction("Vk_PickProfile", Localizer, () => _ = OpenPickerAsync(PickTarget.PlanProfile)),
                    FunctionKeyViewModel.ForAction("Vk_PickProgram", Localizer, () => _ = OpenPickerAsync(PickTarget.PlanProgram)),
                    FunctionKeyViewModel.ForAction("Vk_Reason", Localizer, OpenReasonMenu),
                });
                this.applyPlanKey.LabelArgument = PlanChecks.Count(row => row.Changes).ToString(CultureInfo.InvariantCulture);
                SetCommitPair(this.cancelKey, PlanChecks.Any(row => row.Changes) ? this.applyPlanKey : null);
                break;

            case ImportSubView:
                SetVerticalKeys(new FunctionKeyViewModel?[]
                {
                    this.onlyErrorsKey,
                    FunctionKeyViewModel.ForAction("Vk_ExportTemplate", Localizer, () => _ = ExportAsync(template: true)),
                });
                int good = this.importRows.Count(row => row.Kind != LedgerImportKind.Error);
                this.importKey.LabelArgument = good.ToString(CultureInfo.InvariantCulture);
                SetCommitPair(this.cancelKey, good > 0 ? this.importKey : null);
                break;

            default:
                SetVerticalKeys(new FunctionKeyViewModel?[]
                {
                    new FunctionKeyViewModel("Vk_ToJob", new RelayCommand(ToJob, () => SelectedRow is { IsRetired: false }), Localizer)
                    {
                        PreconditionResourceKey = "Rolls_NothingSelected",
                    },
                    new FunctionKeyViewModel("Vk_ChangePlan", new RelayCommand(OpenChangePlanForSelected, HasSelection), Localizer)
                    {
                        RequiredPermission = Permission.EditRollPlans,
                        PreconditionResourceKey = "Rolls_NothingSelected",
                    },
                    new FunctionKeyViewModel("Vk_RegisterRoll", new RelayCommand(() => OpenForm(null, null, null)), Localizer) { RequiredPermission = Permission.EditJobs },
                    new FunctionKeyViewModel("Vk_CopyRegister", new RelayCommand(() => OpenForm(null, SelectedRow?.Roll, null), HasSelection), Localizer)
                    {
                        RequiredPermission = Permission.EditJobs,
                        PreconditionResourceKey = "Rolls_NothingSelected",
                    },
                    this.multiKey,
                    new FunctionKeyViewModel("Vk_EditRoll", new RelayCommand(() => OpenForm(SelectedRow?.Roll, null, null), HasSelection), Localizer)
                    {
                        RequiredPermission = Permission.EditJobs,
                        PreconditionResourceKey = "Rolls_NothingSelected",
                    },
                    this.retireKey,
                    this.showRetiredKey,
                });
                this.multiKey.IsActive = false;
                this.showRetiredKey.IsActive = ShowRetired;
                SetCommitPair(null, null);
                break;
        }
    }

    private void ToJob()
    {
        if (SelectedRow is { IsRetired: false } row)
        {
            this.draft.RegisteredRollId = row.RollId;
            Navigator.GoTo(PageKey.Job);
        }
    }

    private void ToggleMulti()
    {
        if (ActiveSubViewKey == MultiSubView)
        {
            Navigator.CloseSubView();
        }
        else
        {
            Navigator.OpenSubView(MultiSubView);
        }
    }

    private async Task ToggleRetiredAsync()
    {
        ShowRetired = !ShowRetired;
        await RunGuardedAsync(token => ReloadAsync(SelectedRow?.RollId, token), CancellationToken.None).ConfigureAwait(true);
        ApplyKeys();
    }

    private void SetAllChecked(bool value)
    {
        foreach (RollRowViewModel row in Rows.Where(row => !row.IsRetired))
        {
            row.IsChecked = value;
        }
    }

    /// <summary>用途从已有值里选（避免同一用途两种写法），可"新用途…"。</summary>
    private async Task OpenPurposeMenuAsync(bool forFilter)
    {
        IReadOnlyList<string> purposes = await this.planning.PurposesAsync(CancellationToken.None).ConfigureAwait(true);
        var items = new List<FunctionKeyViewModel?>();
        if (forFilter)
        {
            items.Add(MenuChoice("Rolls_AllPurposes", () => _ = SetPurposeFilterAsync(null), requiresEditable: false));
        }

        foreach (string purpose in purposes.Take(forFilter ? 6 : 6))
        {
            string chosen = purpose;
            items.Add(MenuChoice(
                "Rolls_PurposeItemFormat",
                () =>
                {
                    if (forFilter)
                    {
                        _ = SetPurposeFilterAsync(chosen);
                    }
                    else
                    {
                        FormPurpose = chosen;
                    }
                },
                requiresEditable: false,
                labelArgument: chosen));
        }

        if (!forFilter)
        {
            items.Add(MenuChoice("Vk_NewPurpose", () => FormPurposeEditable = true, requiresEditable: false));
        }

        OpenVerticalMenu(forFilter ? "Vk_FilterPurpose" : "Vk_PickPurpose", items);
    }

    private async Task SetPurposeFilterAsync(string? purpose)
    {
        PurposeFilter = purpose;
        await RunGuardedAsync(token => ReloadAsync(SelectedRow?.RollId, token), CancellationToken.None).ConfigureAwait(true);
    }

    // ───────────── 登记 / 编辑 ─────────────

    [ObservableProperty]
    private bool isNewRoll;

    [ObservableProperty]
    private string formRollId = string.Empty;

    [ObservableProperty]
    private RollKind formKind = RollKind.WorkRoll;

    [ObservableProperty]
    private string formBodyLengthText = string.Empty;

    [ObservableProperty]
    private string formNominalText = string.Empty;

    [ObservableProperty]
    private string formCurrentText = string.Empty;

    [ObservableProperty]
    private string formScrapText = string.Empty;

    [ObservableProperty]
    private string formMaterial = string.Empty;

    [ObservableProperty]
    private string formNetWeightText = string.Empty;

    [ObservableProperty]
    private string formPurpose = string.Empty;

    /// <summary>"新用途…"之后用途格可直接输入。</summary>
    [ObservableProperty]
    private bool formPurposeEditable;

    [ObservableProperty]
    private string? formProfileId;

    [ObservableProperty]
    private string formProfileText = "--";

    [ObservableProperty]
    private string? formProgramId;

    [ObservableProperty]
    private string formProgramText = "--";

    public string FormKindText => Localizer["RollKind_" + FormKind];

    public string FormTitle => Localizer[IsNewRoll ? "Rolls_FormTitleNew" : "Rolls_FormTitleEdit"];

    /// <summary>右下卡片：平时是磨削履历，登记表开着时是"没存进去的原因"。</summary>
    public string LowerTitle => Localizer[IsForm ? "Rolls_FormProblems" : "Rolls_History"];

    partial void OnIsNewRollChanged(bool value) => OnPropertyChanged(nameof(FormTitle));

    partial void OnFormKindChanged(RollKind value) => OnPropertyChanged(nameof(FormKindText));

    /// <summary>上一次"保存"没存进去的原因，逐条。</summary>
    public ObservableCollection<string> FormProblems { get; } = new();

    /// <summary>登记表：新登记（可带作业页输入的辊号）、照同类复制（带计划与用途）、编辑已有的（辊号不能改）。</summary>
    private void OpenForm(RollRecord? existing, RollRecord? copyFrom, string? prefillRollId)
    {
        this.editing = existing;
        IsNewRoll = existing is null;
        RollRecord? source = existing ?? copyFrom;
        FormRollId = existing?.RollId ?? prefillRollId ?? string.Empty;
        FormKind = source?.Kind is RollKind kind and not RollKind.Unspecified ? kind : RollKind.WorkRoll;
        FormBodyLengthText = Text(source?.Geometry.BodyLengthMm, "F0");
        FormNominalText = Text(source?.Geometry.NominalDiameterMm, "F1");
        FormCurrentText = existing is null ? string.Empty : Text(existing.CurrentDiameterMm, "F2");
        FormScrapText = Text(source?.ScrapDiameterMm, "F1");
        FormMaterial = source?.Material ?? string.Empty;
        FormNetWeightText = Text(source?.Data.NetWeightKg, "F0");
        FormPurpose = source?.Purpose ?? string.Empty;
        FormPurposeEditable = false;
        FormProfileId = source?.TargetProfileId;
        FormProgramId = source?.ProgramId;
        FormProblems.Clear();
        _ = RunGuardedAsync(RefreshFormPlanTextAsync, CancellationToken.None);
        Navigator.OpenSubView(FormSubView);
    }

    private async Task RefreshFormPlanTextAsync(CancellationToken cancellationToken)
    {
        FormProfileText = FormProfileId is { } p && await this.profiles.GetAsync(p, cancellationToken).ConfigureAwait(true) is { } profile
            ? Localizer.Format("Lib_NameVersionFormat", profile.Name, profile.Version)
            : "--";
        FormProgramText = FormProgramId is { } g && await this.programs.GetAsync(g, cancellationToken).ConfigureAwait(true) is { } program
            ? Localizer.Format("Lib_NameVersionFormat", program.Name, program.Version)
            : "--";
    }

    private void OpenKindMenu() => OpenVerticalMenu("Vk_PickKind", new FunctionKeyViewModel?[]
    {
        MenuChoice("RollKind_WorkRoll", () => FormKind = RollKind.WorkRoll, requiresEditable: false),
        MenuChoice("RollKind_BackupRoll", () => FormKind = RollKind.BackupRoll, requiresEditable: false),
    });

    private async Task SaveFormAsync()
    {
        await RunGuardedAsync(
            async token =>
            {
                FormProblems.Clear();
                RollRecord? roll = ReadForm();
                if (roll is null)
                {
                    FormProblems.Add(Localizer["Ledger_Problem_NotANumber"]);
                    Interaction.Fail(Localizer["Ledger_Problem_NotANumber"]);
                    return;
                }

                RollLedgerSaveResult result = await this.ledger.SaveAsync(roll, IsNewRoll, token).ConfigureAwait(true);
                if (!result.Saved)
                {
                    foreach (RollLedgerProblem problem in result.Problems)
                    {
                        FormProblems.Add(Localizer["Ledger_Problem_" + problem]);
                    }

                    Interaction.Fail(string.Join(" · ", FormProblems));
                    return;
                }

                // 计划改了（编辑已有的辊）：记一条改动。
                if (!IsNewRoll && this.editing is not null
                    && (this.editing.TargetProfileId != roll.TargetProfileId || this.editing.ProgramId != roll.ProgramId))
                {
                    await this.planning.ChangePlanAsync(new[] { roll.RollId }, roll.TargetProfileId, roll.ProgramId, null, UserName, token).ConfigureAwait(true);
                }

                Say("Ledger_Saved");
                string rollId = roll.RollId.Trim();
                bool backToJob = this.registeringForJob;
                SelectedRow = null;
                Navigator.CloseSubView();
                if (backToJob)
                {
                    // 作业派来登记的：存好直接回作业页，作业页打开这支辊的核对。
                    this.draft.RegisteredRollId = rollId;
                    Navigator.CompleteTask();
                    return;
                }

                await ReloadAsync(rollId, token).ConfigureAwait(true);
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>表里的内容读成一支辊。长度与公称直径必填；有格子不是数返回 null。</summary>
    private RollRecord? ReadForm()
    {
        if (!TryNumber(FormBodyLengthText, out double length) || !TryNumber(FormNominalText, out double nominal) || length <= 0.0 || nominal <= 0.0
            || !TryOptional(FormCurrentText, out double? current) || !TryOptional(FormScrapText, out double? scrap)
            || !TryOptional(FormNetWeightText, out double? weight))
        {
            return null;
        }

        string rollId = (FormRollId ?? string.Empty).Trim();
        RollRecord basis = this.editing ?? new RollRecord(rollId, rollId, RollGeometry.FromDiameter(length, nominal), null, DateTimeOffset.UtcNow);
        return basis with
        {
            Geometry = RollGeometry.FromDiameter(length, nominal),
            Kind = FormKind,
            CurrentDiameterMm = current,
            ScrapDiameterMm = scrap,
            Material = string.IsNullOrWhiteSpace(FormMaterial) ? null : FormMaterial.Trim(),
            Purpose = string.IsNullOrWhiteSpace(FormPurpose) ? null : FormPurpose.Trim(),
            TargetProfileId = FormProfileId,
            ProgramId = FormProgramId,
            PlanInferred = this.editing is not null
                && this.editing.PlanInferred && this.editing.TargetProfileId == FormProfileId && this.editing.ProgramId == FormProgramId,
            Data = basis.Data with { NetWeightKg = weight },
        };
    }

    // ───────────── 选辊形 / 选程序 ─────────────

    private async Task OpenPickerAsync(PickTarget target)
    {
        this.pickTarget = target;
        double body;
        RollKind kind;
        string? current;
        if (target is PickTarget.FormProfile or PickTarget.FormProgram)
        {
            body = TryNumber(FormBodyLengthText, out double length) && length > 0.0 ? length : 1.0;
            kind = FormKind;
            current = target == PickTarget.FormProfile ? FormProfileId : FormProgramId;
        }
        else
        {
            RollRecord first = PlanTargets().FirstOrDefault()?.Roll ?? SelectedRow!.Roll;
            body = first.Geometry.BodyLengthMm;
            kind = first.Kind;
            current = target == PickTarget.PlanProfile ? PlanProfileId ?? first.TargetProfileId : PlanProgramId ?? first.ProgramId;
        }

        await RunGuardedAsync(
            token => Picker.LoadAsync(target is PickTarget.FormProfile or PickTarget.PlanProfile ? PlanPickKind.Profile : PlanPickKind.Program, body, kind, current, token),
            CancellationToken.None).ConfigureAwait(true);
        IsPicking = true;
        ApplyKeys();
    }

    private void ConfirmPick()
    {
        if (Picker.SelectedItem is not { } item)
        {
            return;
        }

        switch (this.pickTarget)
        {
            case PickTarget.FormProfile:
                FormProfileId = item.Id;
                break;
            case PickTarget.FormProgram:
                FormProgramId = item.Id;
                break;
            case PickTarget.PlanProfile:
                PlanProfileId = item.Id;
                break;
            case PickTarget.PlanProgram:
                PlanProgramId = item.Id;
                break;
        }

        IsPicking = false;
        _ = RunGuardedAsync(
            async token =>
            {
                await RefreshFormPlanTextAsync(token).ConfigureAwait(true);
                if (IsChangePlan)
                {
                    await RefreshPlanChecksAsync(token).ConfigureAwait(true);
                }
            },
            CancellationToken.None);
        ApplyKeys();
    }

    // ───────────── 改计划（逐支 = 选一支；多支 = 多选） ─────────────

    public ObservableCollection<PlanCheckRowViewModel> PlanChecks { get; } = new();

    [ObservableProperty]
    private string? planProfileId;

    [ObservableProperty]
    private string? planProgramId;

    [ObservableProperty]
    private string planTitle = string.Empty;

    [ObservableProperty]
    private string planReason = string.Empty;

    private IReadOnlyList<RollRowViewModel> PlanTargets() =>
        Rows.Any(row => row.IsChecked) ? Rows.Where(row => row.IsChecked).ToArray()
        : SelectedRow is { } row ? new[] { row } : Array.Empty<RollRowViewModel>();

    private void OpenChangePlanForSelected()
    {
        foreach (RollRowViewModel row in Rows)
        {
            row.IsChecked = ReferenceEquals(row, SelectedRow);
        }

        OpenChangePlan();
    }

    private void OpenChangePlan()
    {
        PlanProfileId = null;
        PlanProgramId = null;
        PlanReason = string.Empty;
        PlanChecks.Clear();
        Navigator.OpenSubView(ChangePlanSubView);
        _ = OpenPickerAsync(PickTarget.PlanProfile);
    }

    private async Task RefreshPlanChecksAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RollRowViewModel> targets = PlanTargets();
        IReadOnlyList<PlanChangeOutcome> outcomes = await this.planning
            .PreviewPlanChangeAsync(targets.Select(row => row.RollId).ToArray(), PlanProfileId, PlanProgramId, cancellationToken).ConfigureAwait(true);
        PlanChecks.Clear();
        foreach (PlanChangeOutcome outcome in outcomes)
        {
            RollRowViewModel? row = targets.FirstOrDefault(r => r.RollId == outcome.RollId);
            PlanChecks.Add(new PlanCheckRowViewModel(
                outcome.RollId,
                row?.PurposeText ?? "--",
                outcome.ProblemKey is null ? Localizer["PlanCheck_Ok"] : Localizer[outcome.ProblemKey],
                outcome.Changed));
        }

        string? profileName = PlanProfileId is { } p ? (await this.profiles.GetAsync(p, cancellationToken).ConfigureAwait(true))?.Name : null;
        string? programName = PlanProgramId is { } g ? (await this.programs.GetAsync(g, cancellationToken).ConfigureAwait(true))?.Name : null;
        PlanTitle = Localizer.Format("Rolls_PlanTitleFormat", targets.Count, profileName ?? Localizer["Plan_Unchanged"], programName ?? Localizer["Plan_Unchanged"]);
        ApplyKeys();
    }

    /// <summary>原因：常用项竖键（关系设计 O2）；改计划的原因不强制。</summary>
    private void OpenReasonMenu() => OpenVerticalMenu("Vk_Reason", new FunctionKeyViewModel?[]
    {
        MenuChoice("Reason_ScheduleChange", () => PlanReason = Localizer["Reason_ScheduleChange"], requiresEditable: false),
        MenuChoice("Reason_Trial", () => PlanReason = Localizer["Reason_Trial"], requiresEditable: false),
        MenuChoice("Reason_Equipment", () => PlanReason = Localizer["Reason_Equipment"], requiresEditable: false),
        MenuChoice("Reason_Quality", () => PlanReason = Localizer["Reason_Quality"], requiresEditable: false),
    });

    private async Task ApplyPlanAsync()
    {
        int count = PlanChecks.Count(row => row.Changes);
        Interaction.Choose(
            Localizer.Format("Rolls_AskApplyPlanFormat", count, PlanTitle),
            "Vk_ApplyPlanFormatShort",
            async () => await RunGuardedAsync(
                async token =>
                {
                    IReadOnlyList<PlanChangeOutcome> done = await this.planning.ChangePlanAsync(
                        PlanTargets().Select(row => row.RollId).ToArray(),
                        PlanProfileId,
                        PlanProgramId,
                        string.IsNullOrWhiteSpace(PlanReason) ? null : PlanReason,
                        UserName,
                        token).ConfigureAwait(true);
                    Say("Rolls_PlanChangedFormat", done.Count(o => o.Changed));
                    Navigator.CloseSubView();
                },
                CancellationToken.None).ConfigureAwait(true));
        await Task.CompletedTask.ConfigureAwait(true);
    }

    // ───────────── 作废 ─────────────

    private void AskRetire()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        bool retire = !row.IsRetired;
        Ask(
            retire ? "Rolls_AskRetire" : "Rolls_AskRestore",
            async () => await RunGuardedAsync(
                async token =>
                {
                    await this.planning.SetRetiredAsync(row.RollId, retire, UserName, token).ConfigureAwait(true);
                    Say(retire ? "Rolls_Retired" : "Rolls_Restored", row.RollId);
                    await ReloadAsync(row.RollId, token).ConfigureAwait(true);
                },
                CancellationToken.None).ConfigureAwait(true),
            row.RollId);
    }

    // ───────────── 导入 / 导出 ─────────────

    public ObservableCollection<ImportRowViewModel> ImportRows { get; } = new();

    [ObservableProperty]
    private bool onlyErrors;

    [ObservableProperty]
    private string importTitle = string.Empty;

    private async Task PickImportFileAsync()
    {
        string? path = InteractionScope.FileDialogs.PickOpenPath(".csv", Localizer["Rolls_CsvFilter"]);
        if (path is null)
        {
            return;
        }

        await RunGuardedAsync(
            async token =>
            {
                string text = await File.ReadAllTextAsync(path, token).ConfigureAwait(true);
                this.importRows = await this.planning.PreviewImportAsync(text, token).ConfigureAwait(true);
                ImportTitle = Localizer.Format(
                    "Rolls_ImportTitleFormat",
                    Path.GetFileName(path),
                    this.importRows.Count(row => row.Kind == LedgerImportKind.Add),
                    this.importRows.Count(row => row.Kind == LedgerImportKind.Update),
                    this.importRows.Count(row => row.Kind == LedgerImportKind.Error));
                OnlyErrors = false;
                FillImportRows();
                Navigator.OpenSubView(ImportSubView);
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    private void ToggleOnlyErrors()
    {
        OnlyErrors = !OnlyErrors;
        this.onlyErrorsKey.IsActive = OnlyErrors;
        FillImportRows();
    }

    private void FillImportRows()
    {
        ImportRows.Clear();
        foreach (LedgerImportRow row in this.importRows.Where(row => !OnlyErrors || row.Kind == LedgerImportKind.Error))
        {
            ImportRows.Add(new ImportRowViewModel(
                row.LineNumber,
                Localizer["ImportKind_" + row.Kind],
                row.Roll?.RollId ?? "--",
                row.Roll is { } roll ? roll.Geometry.BodyLengthMm.ToString("F0", CultureInfo.CurrentCulture) : "--",
                row.Roll?.TargetProfileId ?? "--",
                string.Join(" · ", row.ProblemKeys.Select(key => Localizer[key])),
                row.Kind == LedgerImportKind.Error));
        }
    }

    private async Task ImportAsync()
    {
        await RunGuardedAsync(
            async token =>
            {
                int count = await this.planning.ImportAsync(this.importRows, UserName, token).ConfigureAwait(true);
                Say("Rolls_ImportedFormat", count);
                Navigator.CloseSubView();
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>导出台账（CSV，Excel 能直接打开）；模板就是表头 + 现有的辊。</summary>
    private async Task ExportAsync(bool template)
    {
        string? path = InteractionScope.FileDialogs.PickSavePath(
            (template ? "RGX-ledger-template-" : "RGX-ledger-") + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv",
            ".csv",
            Localizer["Rolls_CsvFilter"]);
        if (path is null)
        {
            return;
        }

        await RunGuardedAsync(
            async token =>
            {
                string csv = await this.planning.ExportCsvAsync(includeRetired: !template, token).ConfigureAwait(true);
                await File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), token).ConfigureAwait(true);
                Say("Rolls_ExportedFormat", path);
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    private static string Text(double? value, string format) =>
        value is double number ? number.ToString(format, CultureInfo.CurrentCulture) : string.Empty;

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryOptional(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (TryNumber(text, out double parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
