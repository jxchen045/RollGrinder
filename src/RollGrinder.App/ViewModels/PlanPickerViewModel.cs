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

namespace RollGrinder.App.ViewModels;

/// <summary>选的是辊形还是程序。</summary>
public enum PlanPickKind
{
    Profile = 0,
    Program = 1,
}

/// <summary>选择列表里的一条。</summary>
/// <param name="Id">标识。</param>
/// <param name="Name">名字。</param>
/// <param name="VersionText">版本。</param>
/// <param name="Detail">概况（设计长度 / 适用类型）。</param>
/// <param name="Fits">与这支辊对得上（长度按 2% 规则、类型）；"显示全部"时对不上的也列出来但灰显。</param>
public sealed record PlanPickItemViewModel(string Id, string Name, string VersionText, string Detail, bool Fits);

/// <summary>
/// 选辊形 / 选程序子视图（界面修订稿 v3 4.2"选择子视图共用"）：登记、台账改计划、多选改计划、作业里更换，
/// 四处用同一个——列表已按这支辊过滤（辊形长度按 2% 规则、程序适用类型、停用的不列），右边把选中的与现计划叠图对照。
/// </summary>
public sealed partial class PlanPickerViewModel : ObservableObject
{
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
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
        RollProfileTypeRegistry profileTypes,
        MachineDescription machine,
        HmiSettings settings,
        IStringLocalizer localizer)
    {
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public ObservableCollection<PlanPickItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfile), nameof(ListTitle))]
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

    /// <summary>取字用（视图画轴名）。</summary>
    public IStringLocalizer Localizer => this.localizer;

    public string ListTitle => this.localizer.Format(
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
        double tolerance = this.machine.Threshold(MachineDescription.LengthTolerancePercentKey) ?? BodyLengthFit.DefaultTolerancePercent;
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
                    fits = BodyLengthFit.Fit(definition.Profile, definition.BodyLengthMm, this.bodyLengthMm, tolerance).Kind != BodyFitKind.TooDifferent;
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

    /// <summary>"显示全部长度 / 类型"：对不上的也列出来（灰显，选了核对页会拦）。</summary>
    public Task ToggleShowAllAsync(CancellationToken cancellationToken)
    {
        ShowAll = !ShowAll;
        return LoadAsync(Kind, this.bodyLengthMm, this.rollKind, this.currentId, cancellationToken);
    }

    partial void OnSelectedItemChanged(PlanPickItemViewModel? value) => _ = RefreshPreviewAsync(CancellationToken.None);

    /// <summary>预览：辊形按 2% 规则套到辊身上的曲线；程序列要点。</summary>
    public async Task RefreshPreviewAsync(CancellationToken cancellationToken)
    {
        SelectedCurve = Array.Empty<(double, double)>();
        ProgramRows.Clear();
        if (SelectedItem is { } item)
        {
            if (IsProfile)
            {
                SelectedCurve = await CurveAsync(item.Id, cancellationToken).ConfigureAwait(true);
            }
            else if (await this.programs.GetAsync(item.Id, cancellationToken).ConfigureAwait(true) is { } program)
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

        double tolerance = this.machine.Threshold(MachineDescription.LengthTolerancePercentKey) ?? BodyLengthFit.DefaultTolerancePercent;
        BodyFitResult fit = BodyLengthFit.Fit(definition.Profile, definition.BodyLengthMm, this.bodyLengthMm, tolerance);
        CompositeRollProfile shape = fit.Profile ?? definition.Profile;
        double length = fit.Profile is null ? definition.BodyLengthMm : this.bodyLengthMm;
        return shape.Compose(RollGeometry.FromDiameter(length, definition.NominalDiameterMm ?? 600.0), this.profileTypes, this.settings.ProfileSampleCount)
            .Points.Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();
    }
}
