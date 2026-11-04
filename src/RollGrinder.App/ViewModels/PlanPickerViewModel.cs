using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Jobs;

namespace RollGrinder.App.ViewModels;

/// <summary>选的是辊形还是程序。</summary>
public enum PlanPickKind
{
    Profile = 0,
    Program = 1,

    /// <summary>这支辊的磨削方案（作业核对"换方案"，设计稿 v4 5.2.3）。</summary>
    Plan = 2,

    /// <summary>复制到本辊的来源（设计稿 v4 5.5.5）：本辊方案、其他辊的方案、模板。</summary>
    CopySource = 3,
}

/// <summary>复制来源列在哪一堆（设计稿 v4 I5：本辊 / 其他辊 ▸ / 模板 ▸）。</summary>
public enum CopyRange
{
    ThisRoll = 0,
    OtherRolls = 1,
    Templates = 2,
}

/// <summary>选择列表里的一条。</summary>
/// <param name="Id">标识。</param>
/// <param name="Name">名字。</param>
/// <param name="VersionText">版本。</param>
/// <param name="Detail">概况（设计长度 / 适用类型）。</param>
/// <param name="Fits">与这支辊对得上（长度按 2% 规则、类型）；"显示全部"时对不上的也列出来但灰显。</param>
public sealed record PlanPickItemViewModel(string Id, string Name, string VersionText, string Detail, bool Fits);

/// <summary>
/// 选模板 / 选方案子视图（界面修订稿 v3 4.2"选择子视图共用"、设计稿 v4 5.2.3）：登记、台账改计划、多选改计划选模板，
/// 作业核对"换方案"选这支辊的方案——列表已按这支辊过滤（模板长度按中心对齐能对上、程序适用类型、停用的不列），
/// 右边把选中的与现在用的叠图对照。
/// </summary>
public sealed partial class PlanPickerViewModel : ObservableObject
{
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IGrindingPlanService plans;
    private readonly IRollRepository rolls;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;
    private readonly IStringLocalizer localizer;
    private double bodyLengthMm = 1.0;
    private RollKind rollKind;
    private IReadOnlyList<(double, double)> referenceCurve = Array.Empty<(double, double)>();
    private string? currentId;

    public PlanPickerViewModel(
        IRollProfileRepository profiles,
        IProgramRepository programs,
        IGrindingPlanService plans,
        IRollRepository rolls,
        RollProfileTypeRegistry profileTypes,
        MachineDescription machine,
        HmiSettings settings,
        IStringLocalizer localizer)
    {
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public ObservableCollection<PlanPickItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfile), nameof(ListTitle), nameof(ShowsCurve))]
    private PlanPickKind kind;

    [ObservableProperty]
    private PlanPickItemViewModel? selectedItem;

    /// <summary>对不上的也列出来（灰显）。</summary>
    [ObservableProperty]
    private bool showAll;

    /// <summary>"现计划"那一条的名字（对照用）。</summary>
    [ObservableProperty]
    private string currentName = "--";

    public bool IsProfile => Kind == PlanPickKind.Profile;

    /// <summary>右边画辊形（选辊形、选方案）。</summary>
    public bool ShowsCurve => Kind != PlanPickKind.Program;

    /// <summary>选方案时：这支辊。</summary>
    private string? planRollId;

    /// <summary>取字用（视图画轴名）。</summary>
    public IStringLocalizer Localizer => this.localizer;

    public string ListTitle => Kind == PlanPickKind.CopySource
        ? this.localizer.Format("Copy_ListTitleFormat", this.localizer["CopyRange_" + Range], this.copyTargetText, Items.Count)
        : Kind == PlanPickKind.Plan
        ? this.localizer.Format("Picker_PlansTitleFormat", this.planRollId ?? "--", Items.Count)
        : this.localizer.Format(
        IsProfile ? "Picker_ProfilesTitleFormat" : "Picker_ProgramsTitleFormat",
        this.bodyLengthMm.ToString("F0", CultureInfo.InvariantCulture),
        Items.Count(item => item.Fits),
        Items.Count);

    /// <summary>选中的辊形（直径量 µm，沿辊身）——按 2% 规则套到辊身上之后的样子。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> SelectedCurve { get; private set; } = Array.Empty<(double, double)>();

    /// <summary>现计划的辊形（虚线对照）。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> ReferenceCurve => this.referenceCurve;

    /// <summary>辊身长度（图的横轴）。</summary>
    public double BodyLengthMm => this.bodyLengthMm;

    /// <summary>选中程序的要点（工序、标准余量、适用类型）。</summary>
    public ObservableCollection<LabelValueViewModel> ProgramRows { get; } = new();

    /// <summary>预览变了（视图重画）。</summary>
    public event EventHandler? PreviewChanged;

    /// <summary>按这支辊（或这批辊的第一支）载入列表。</summary>
    public async Task LoadAsync(PlanPickKind pickKind, double bodyLength, RollKind kindOfRoll, string? currentId, CancellationToken cancellationToken)
    {
        Kind = pickKind;
        this.currentId = currentId;
        this.bodyLengthMm = Math.Max(bodyLength, 1.0);
        this.rollKind = kindOfRoll;
        Items.Clear();
        if (pickKind == PlanPickKind.Profile)
        {
            foreach (RollProfileSummary summary in await this.profiles.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(true))
            {
                if (summary.Disabled)
                {
                    continue;
                }

                bool fits = true;
                if (await this.profiles.GetAsync(summary.ProfileId, cancellationToken).ConfigureAwait(true) is { } definition)
                {
                    fits = GrindingPlanService.AlignForJob(
                        definition, this.bodyLengthMm, null, this.machine, this.profileTypes, this.settings.ProfileSampleCount).CanUse;
                }

                Items.Add(new PlanPickItemViewModel(
                    summary.ProfileId,
                    summary.Name,
                    this.localizer.Format("Lib_VersionFormat", summary.Version),
                    this.localizer.Format("Picker_ProfileDetailFormat", summary.BodyLengthMm.ToString("F0", CultureInfo.InvariantCulture)),
                    fits));
            }
        }
        else
        {
            foreach (ProgramSummary summary in await this.programs.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(true))
            {
                if (summary.Disabled)
                {
                    continue;
                }

                bool fits = summary.ApplicableRollKind == RollKind.Unspecified || this.rollKind == RollKind.Unspecified
                    || summary.ApplicableRollKind == this.rollKind;
                Items.Add(new PlanPickItemViewModel(
                    summary.ProgramId,
                    summary.Name,
                    this.localizer.Format("Lib_VersionFormat", summary.Version),
                    this.localizer["RollKindFilter_" + summary.ApplicableRollKind],
                    fits));
            }
        }

        // 对得上的在前；默认只列对得上的。
        PlanPickItemViewModel[] ordered = Items.OrderBy(item => item.Fits ? 0 : 1).ThenBy(item => item.Name, StringComparer.CurrentCulture).ToArray();
        Items.Clear();
        foreach (PlanPickItemViewModel item in ordered.Where(item => ShowAll || item.Fits))
        {
            Items.Add(item);
        }

        CurrentName = currentId is null ? "--" : ordered.FirstOrDefault(item => item.Id == currentId)?.Name ?? currentId;
        this.referenceCurve = pickKind == PlanPickKind.Profile && currentId is not null
            ? await CurveAsync(currentId, cancellationToken).ConfigureAwait(true)
            : Array.Empty<(double, double)>();
        SelectedItem = Items.FirstOrDefault(item => item.Id == currentId) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(ListTitle));
        await RefreshPreviewAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 选方案（设计稿 v4 5.2.3）：列这支辊的方案，★ 当前在前；右边把选中方案的辊形与"这一次原来用的"叠图，下面列工艺要点。
    /// </summary>
    public async Task LoadPlansAsync(string rollId, string? currentPlanId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);
        Kind = PlanPickKind.Plan;
        this.planRollId = rollId;
        this.currentId = currentPlanId;
        Items.Clear();
        IReadOnlyList<PlanOverview> list = await this.plans.ListAsync(rollId, cancellationToken).ConfigureAwait(true);
        foreach (PlanOverview row in list)
        {
            Items.Add(new PlanPickItemViewModel(
                row.Plan.PlanId,
                this.localizer.Format("Picker_PlanNameFormat", row.Plan.PlanNo, row.Plan.Name),
                (row.IsCurrent ? "★ " : string.Empty) + this.localizer.Format("Picker_PlanUsedFormat", row.Usage.UseCount),
                row.Plan.Purpose ?? "--",
                true));
        }

        PlanOverview? reference = list.FirstOrDefault(row => row.Plan.PlanId == currentPlanId);
        CurrentName = reference is null ? "--" : this.localizer.Format("Picker_PlanNameFormat", reference.Plan.PlanNo, reference.Plan.Name);
        this.referenceCurve = reference is null
            ? Array.Empty<(double, double)>()
            : await CurveAsync(reference.Plan.ProfileId, cancellationToken).ConfigureAwait(true);
        SelectedItem = Items.FirstOrDefault(item => item.Id == currentPlanId) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(ListTitle));
        await RefreshPreviewAsync(cancellationToken).ConfigureAwait(true);
    }

    // ───────────── 复制来源（设计稿 v4 5.5.5、5.4.4） ─────────────

    private string? copyTargetRollId;
    private double copyTargetLengthMm;
    private double? copyTargetMeasuredMm;
    private RollKind copyTargetKind;
    private string copyTargetText = "--";

    /// <summary>复制来源列在哪一堆。</summary>
    public CopyRange Range { get; private set; }

    /// <summary>复制范围（P10）：全部 / 只辊形 / 只工艺。</summary>
    public CopyScope Scope { get; private set; }

    /// <summary>选中来源的复制预览；没选或不在复制模式为 null。</summary>
    public CopyPreview? CopyResult { get; private set; }

    /// <summary>选中的来源（复制模式）。</summary>
    public CopySource? SelectedCopySource => SelectedItem is null ? null : ParseSource(SelectedItem.Id);

    /// <summary>能复制（选了来源、没被拒）。</summary>
    public bool CanCopy => CopyResult is { ProblemKey: null };

    /// <summary>
    /// 复制来源：本辊的方案、其他辊的方案（同类型、同用途的在前）、模板（辊形模板 + 工艺模板）。
    /// <paramref name="targetRollId"/> 为 null 时是新登记表：按 <paramref name="targetLengthMm"/> 等表里的数预览。
    /// </summary>
    public async Task LoadCopySourcesAsync(
        string? targetRollId, double targetLengthMm, double? measuredMm, RollKind kind, CopyRange range, CopyScope scope, CancellationToken cancellationToken)
    {
        Kind = PlanPickKind.CopySource;
        this.copyTargetRollId = targetRollId;
        this.copyTargetLengthMm = targetLengthMm;
        this.copyTargetMeasuredMm = measuredMm;
        this.copyTargetKind = kind;
        this.copyTargetText = targetRollId ?? this.localizer.Format("Copy_NewRollFormat", targetLengthMm);
        Range = range;
        Scope = scope;
        Items.Clear();
        RollRecord? target = targetRollId is null ? null : await this.rolls.GetAsync(targetRollId, cancellationToken).ConfigureAwait(true);
        switch (range)
        {
            case CopyRange.ThisRoll when targetRollId is not null:
                foreach (PlanOverview row in await this.plans.ListAsync(targetRollId, cancellationToken).ConfigureAwait(true))
                {
                    Items.Add(PlanItem(row, includeRoll: false));
                }

                break;

            case CopyRange.OtherRolls:
            case CopyRange.ThisRoll:
                IEnumerable<RollRecord> others = (await this.rolls.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(true))
                    .Where(roll => !roll.Retired && roll.RollId != targetRollId)
                    .OrderBy(roll => roll.Kind == kind ? 0 : 1)
                    .ThenBy(roll => string.Equals(roll.Purpose, target?.Purpose, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(roll => roll.RollId, StringComparer.Ordinal);
                foreach (RollRecord roll in others)
                {
                    foreach (PlanOverview row in await this.plans.ListAsync(roll.RollId, cancellationToken).ConfigureAwait(true))
                    {
                        Items.Add(PlanItem(row, includeRoll: true));
                    }
                }

                break;

            case CopyRange.Templates:
                foreach (RollProfileSummary summary in await this.profiles.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(true))
                {
                    if (!summary.Disabled)
                    {
                        Items.Add(new PlanPickItemViewModel(
                            "TP:" + summary.ProfileId, this.localizer.Format("Copy_ProfileTemplateFormat", summary.Name),
                            this.localizer.Format("Lib_VersionFormat", summary.Version),
                            summary.BodyLengthMm.ToString("F0", CultureInfo.CurrentCulture), true));
                    }
                }

                foreach (ProgramSummary summary in await this.programs.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(true))
                {
                    if (!summary.Disabled)
                    {
                        Items.Add(new PlanPickItemViewModel(
                            "TG:" + summary.ProgramId, this.localizer.Format("Copy_ProgramTemplateFormat", summary.Name),
                            this.localizer.Format("Lib_VersionFormat", summary.Version),
                            this.localizer["RollKindFilter_" + summary.ApplicableRollKind], true));
                    }
                }

                break;
        }

        CurrentName = "--";
        this.referenceCurve = Array.Empty<(double, double)>();
        SelectedItem = Items.FirstOrDefault();
        OnPropertyChanged(nameof(ListTitle));
        await RefreshPreviewAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>换复制范围（重算预览）。</summary>
    public Task SetScopeAsync(CopyScope scope, CancellationToken cancellationToken)
    {
        Scope = scope;
        return RefreshPreviewAsync(cancellationToken);
    }

    private PlanPickItemViewModel PlanItem(PlanOverview row, bool includeRoll) => new(
        "PL:" + row.Plan.PlanId,
        includeRoll
            ? this.localizer.Format("Copy_OtherPlanFormat", row.Plan.RollId, row.Plan.PlanNo, row.Plan.Name)
            : this.localizer.Format("Picker_PlanNameFormat", row.Plan.PlanNo, row.Plan.Name),
        (row.IsCurrent ? "★ " : string.Empty) + (row.Plan.Purpose ?? string.Empty),
        row.NominalLengthMm.ToString("F0", CultureInfo.CurrentCulture),
        true);

    private static CopySource? ParseSource(string id) => id.Length < 3 ? null : id[..3] switch
    {
        "PL:" => CopySource.FromPlan(id[3..]),
        "TP:" => CopySource.FromTemplates(id[3..], null),
        "TG:" => CopySource.FromTemplates(null, id[3..]),
        _ => null,
    };

    /// <summary>选中来源的复制预览：长度对齐几行、叠图（来源点线、复制后实线）。</summary>
    private async Task RefreshCopyPreviewAsync(PlanPickItemViewModel item, CancellationToken cancellationToken)
    {
        CopyResult = null;
        if (ParseSource(item.Id) is not { } source)
        {
            return;
        }

        CopyPreview preview = this.copyTargetRollId is { } rollId
            ? await this.plans.PreviewCopyAsync(rollId, source, Scope, cancellationToken).ConfigureAwait(true)
            : await this.plans.PreviewCopyForNewRollAsync(this.copyTargetLengthMm, this.copyTargetMeasuredMm, this.copyTargetKind, source, cancellationToken)
                .ConfigureAwait(true);
        CopyResult = preview;
        foreach ((string label, string value) in DescribeCopy(preview, this.localizer))
        {
            ProgramRows.Add(new LabelValueViewModel(label, value, this.localizer));
        }

        // 叠图：来源按它自己的名义长度画（点线），复制后按本辊名义长度画（实线）；视图以中心对齐。
        RollProfileDefinition? from = source.PlanId is { } planId
            ? (await this.plans.GetContentAsync(planId, cancellationToken).ConfigureAwait(true))?.Profile
            : source.ProfileTemplateId is { } templateId ? await this.profiles.GetAsync(templateId, cancellationToken).ConfigureAwait(true) : null;
        if (from is not null && Scope != CopyScope.ProgramOnly)
        {
            this.referenceCurve = Compose(from.Profile, from.BodyLengthMm);
            CurrentName = preview.SourceText;
        }
        else
        {
            this.referenceCurve = Array.Empty<(double, double)>();
        }

        SelectedCurve = preview.Alignment?.Profile is { } aligned ? Compose(aligned, preview.Alignment.TargetLengthMm) : Array.Empty<(double, double)>();
        OnPropertyChanged(nameof(CanCopy));
    }

    private IReadOnlyList<(double, double)> Compose(CompositeRollProfile profile, double lengthMm) =>
        profile.Compose(RollGeometry.FromDiameter(lengthMm, RollGeometry.PreviewDiameterMm), this.profileTypes, this.settings.ProfileSampleCount)
            .Points.Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();

    /// <summary>
    /// 复制对齐的几行（设计稿 v4 P9）：名义长度 来源 → 本辊、方式、段表、判定，有实测时加"下发时"一行。
    /// 返回（标签资源键，值）。台账的新登记表、多支建方案也用它。
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> DescribeCopy(CopyPreview preview, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(localizer);
        var rows = new List<(string, string)>
        {
            ("Copy_Source", preview.SourceText),
            ("Copy_Result", localizer.Format("Copy_ResultFormat", preview.ProfileName, preview.ProgramName)),
        };
        if (preview.Alignment is { } alignment)
        {
            rows.Add(("Copy_Lengths", localizer.Format("Copy_LengthsFormat", alignment.SourceLengthMm, alignment.TargetLengthMm, alignment.DifferenceMm)));
            rows.Add(("Copy_Method", MethodText(alignment, localizer)));
            if (alignment.SegmentsAfter.Count > 0 && !alignment.SegmentsBefore.SequenceEqual(alignment.SegmentsAfter))
            {
                rows.Add(("Copy_Segments", localizer.Format("Copy_SegmentsFormat", Join(alignment.SegmentsBefore), Join(alignment.SegmentsAfter))));
            }
        }

        rows.Add(("Copy_Verdict", VerdictText(preview, localizer)));
        if (preview.Download is { } download)
        {
            rows.Add(("Copy_Download", download.Kind switch
            {
                LengthAlignmentKind.Exact => localizer["Copy_DownloadExact"],
                LengthAlignmentKind.Trimmed => localizer.Format("Copy_DownloadTrimFormat", download.BodyLengthMm, -download.PerEndMm),
                LengthAlignmentKind.Extended => localizer.Format("Copy_DownloadExtendFormat", download.BodyLengthMm, download.PerEndMm),
                _ => localizer.Format("Copy_DownloadBlockFormat", download.BodyLengthMm),
            }));
        }

        return rows;
    }

    /// <summary>一行写完的结论（多支建方案的逐支列）。</summary>
    public static string ShortCopyText(CopyPreview preview, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(localizer);
        string text = preview.Alignment is { } alignment ? MethodText(alignment, localizer) : localizer["Copy_NoProfile"];
        if (preview.ProblemKey is { } problem)
        {
            return localizer[problem];
        }

        if (preview.NeedsConfirm)
        {
            text += " · " + localizer["Copy_NeedsConfirmShort"];
        }

        if (preview.Download is { Kind: not LengthAlignmentKind.Exact } download)
        {
            text += " · " + (download.CanUse
                ? localizer.Format("Copy_DownloadShortFormat", download.PerEndMm)
                : localizer["Copy_DownloadBlockShort"]);
        }

        return text;
    }

    private static string MethodText(CopyAlignment alignment, IStringLocalizer localizer) => alignment.Kind switch
    {
        CopyAlignmentKind.Same => localizer["Copy_MethodSame"],
        CopyAlignmentKind.MiddleFilled => localizer["Copy_MethodMiddle"],
        CopyAlignmentKind.Trimmed => localizer.Format("Copy_MethodTrimFormat", -alignment.PerEndMm),
        CopyAlignmentKind.Extended => localizer.Format("Copy_MethodExtendFormat", alignment.PerEndMm),
        _ => localizer["Copy_MethodRefused"],
    };

    private static string VerdictText(CopyPreview preview, IStringLocalizer localizer) =>
        preview.ProblemKey is { } problem ? localizer.Format("Copy_VerdictRefusedFormat", localizer[problem])
        : preview.NeedsConfirm ? localizer["Copy_VerdictConfirm"]
        : localizer["Copy_VerdictOk"];

    private static string Join(IReadOnlyList<double> lengths) =>
        string.Join(" · ", lengths.Select(length => length.ToString("0.#", CultureInfo.CurrentCulture)));

    /// <summary>"显示全部长度 / 类型"：对不上的也列出来（灰显，选了核对页会拦）。</summary>
    public Task ToggleShowAllAsync(CancellationToken cancellationToken)
    {
        ShowAll = !ShowAll;
        return Kind == PlanPickKind.CopySource
            ? LoadCopySourcesAsync(this.copyTargetRollId, this.copyTargetLengthMm, this.copyTargetMeasuredMm, this.copyTargetKind, Range, Scope, cancellationToken)
            : Kind == PlanPickKind.Plan
            ? LoadPlansAsync(this.planRollId!, this.currentId, cancellationToken)
            : LoadAsync(Kind, this.bodyLengthMm, this.rollKind, this.currentId, cancellationToken);
    }

    partial void OnSelectedItemChanged(PlanPickItemViewModel? value) => _ = RefreshPreviewAsync(CancellationToken.None);

    /// <summary>预览：辊形按 2% 规则套到辊身上的曲线；程序列要点。</summary>
    public async Task RefreshPreviewAsync(CancellationToken cancellationToken)
    {
        SelectedCurve = Array.Empty<(double, double)>();
        ProgramRows.Clear();
        if (Kind == PlanPickKind.CopySource)
        {
            CopyResult = null;
            if (SelectedItem is { } source)
            {
                await RefreshCopyPreviewAsync(source, cancellationToken).ConfigureAwait(true);
            }

            OnPropertyChanged(nameof(CanCopy));
            PreviewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (SelectedItem is { } item)
        {
            string? programId = item.Id;
            if (Kind == PlanPickKind.Plan)
            {
                PlanContent? content = await this.plans.GetContentAsync(item.Id, cancellationToken).ConfigureAwait(true);
                SelectedCurve = content is null ? Array.Empty<(double, double)>() : await CurveAsync(content.Profile.ProfileId, cancellationToken).ConfigureAwait(true);
                programId = content?.Program.ProgramId;
                if (content is not null)
                {
                    ProgramRows.Add(new LabelValueViewModel("Picker_PlanProfile", content.Profile.Name, this.localizer));
                    ProgramRows.Add(new LabelValueViewModel("Picker_PlanProgram", content.Program.Name, this.localizer));
                }
            }

            if (IsProfile)
            {
                SelectedCurve = await CurveAsync(item.Id, cancellationToken).ConfigureAwait(true);
            }
            else if (programId is not null && await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(true) is { } program)
            {
                ProgramRows.Add(new LabelValueViewModel("Lib_PreviewApplicable",
                    this.localizer["RollKindFilter_" + program.ApplicableRollKind]
                    + (string.IsNullOrWhiteSpace(program.ApplicableMaterial) ? string.Empty : " · " + program.ApplicableMaterial), this.localizer));
                ProgramRows.Add(new LabelValueViewModel("Lib_PreviewStandardStock",
                    program.StandardStockMicrometer is double stock ? (stock / 1000.0).ToString("F3", CultureInfo.CurrentCulture) + " mm" : "--", this.localizer));
                ProgramRows.Add(new LabelValueViewModel("Lib_PreviewSteps",
                    string.Join(" · ", program.Steps.Select(step => this.localizer["StepType_" + step.StepTypeKey])), this.localizer));
            }
        }

        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<IReadOnlyList<(double, double)>> CurveAsync(string profileId, CancellationToken cancellationToken)
    {
        if (await this.profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(true) is not { } definition)
        {
            return Array.Empty<(double, double)>();
        }

        // 选方案：画方案自己的名义长度；选模板：按中心对到这支辊上（截取 / 延伸，不伸缩），对不上就画模板原样。
        LengthAlignment? fit = Kind == PlanPickKind.Plan
            ? null
            : GrindingPlanService.AlignForJob(definition, this.bodyLengthMm, null, this.machine, this.profileTypes, this.settings.ProfileSampleCount);
        CompositeRollProfile shape = fit?.Profile ?? definition.Profile;
        double length = fit?.Profile is null ? definition.BodyLengthMm : this.bodyLengthMm;
        return shape.Compose(RollGeometry.FromDiameter(length, RollGeometry.PreviewDiameterMm), this.profileTypes, this.settings.ProfileSampleCount)
            .Points.Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();
    }
}
