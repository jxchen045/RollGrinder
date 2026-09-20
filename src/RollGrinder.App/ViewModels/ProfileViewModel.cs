using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using RollGrinder.Services.Alarms;

namespace RollGrinder.App.ViewModels;

/// <summary>曲线段列表里的一行。</summary>
public sealed partial class SegmentRowViewModel : ObservableObject
{
    public SegmentRowViewModel(RollProfileSegment segment, string displayName, string purposeText)
    {
        Segment = segment;
        DisplayName = displayName;
        PurposeText = purposeText;
    }

    public RollProfileSegment Segment { get; }

    public int Order => Segment.Order;

    public string OrderText => Segment.Order.ToString(CultureInfo.InvariantCulture);

    /// <summary>曲线类型名，例如 Polynom / Taper。</summary>
    public string DisplayName { get; }

    /// <summary>区间与用途，例如 "0 – 150 mm · 头架端锥"。</summary>
    public string PurposeText { get; }

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>
/// 辊形编辑。版面见 docs/design/B-Profile-辊形编辑.html：
/// 左边是可叠加的曲线段，中上是预览，中下是该段参数（由 Schema 自动生成，新增类型不改界面）。
/// </summary>
public sealed partial class ProfileViewModel : PageViewModelBase
{
    /// <summary>中高轴（补偿执行轴）在 machine.json 里的 role。</summary>
    public const string CrownAxisRole = "CrownAdjust";

    private readonly RollProfileTypeRegistry profileTypes;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;

    private readonly RollGeometry geometry;

    private CompositeRollProfile composite;

    /// <summary>上一次"干净"的辊形，供"放弃修改"回退。</summary>
    private CompositeRollProfile committedComposite;

    public ProfileViewModel(
        RollProfileTypeRegistry profileTypes,
        MachineDescription machine,
        HmiSettings settings,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

        this.geometry = RollGeometry.FromDiameter(
            machine.Workpiece.MinBodyLengthMm, machine.Workpiece.MinDiameterMm);
        this.composite = CompositeRollProfile.Single(
            ProfileTypeKeys.Cylindrical, this.geometry, ParameterSet.Empty);
        this.committedComposite = this.composite;

        AvailableTypes = new ObservableCollection<string>(profileTypes.All.Select(type => type.Key));
        this.selectedTypeForInsert = AvailableTypes.FirstOrDefault() ?? ProfileTypeKeys.Cylindrical;

        SetFunctionKeys(new[]
        {
            FunctionKeyViewModel.Placeholder("Fn_Save", localizer, () => NotImplementedYet("Fn_Save"), FunctionKeyKind.Primary, requiresEditable: true),
            FunctionKeyViewModel.Placeholder("Fn_SaveAs", localizer, () => NotImplementedYet("Fn_SaveAs"), requiresEditable: true),
            new FunctionKeyViewModel("Fn_NewSegment", InsertSegmentCommand, localizer, requiresEditable: true),
            FunctionKeyViewModel.Placeholder("Fn_ImportPoints", localizer, () => NotImplementedYet("Fn_ImportPoints"), requiresEditable: true),
            FunctionKeyViewModel.Placeholder("Fn_GeneratePoints", localizer, () => NotImplementedYet("Fn_GeneratePoints"), requiresEditable: true),
            new FunctionKeyViewModel("Fn_Validate", ValidateCommand, localizer),
            FunctionKeyViewModel.Placeholder("Fn_ProfileLibrary", localizer, () => NotImplementedYet("Fn_ProfileLibrary")),
        });

        Rebuild();
    }

    public override PageKey Key => PageKey.Profile;

    public override string TitleResourceKey => "Page_Profile";

    public override string MenuHintResourceKey => "Menu_ProfileHint";

    /// <summary>编辑页：自动循环挂着程序时落只读锁，免得改了辊形以为机床会跟着变。</summary>
    public override bool LocksDuringRun => true;

    public ObservableCollection<SegmentRowViewModel> Segments { get; } = new();

    public ObservableCollection<ParameterRowViewModel> SegmentParameters { get; } = new();

    public ObservableCollection<string> AvailableTypes { get; }

    /// <summary>合成辊形（辊身坐标 mm，直径量 mm）。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMm)> ComposedPoints { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>仅主辊形（第 1 段），作为对照线。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMm)> MainPoints { get; private set; } =
        Array.Empty<(double, double)>();

    public event EventHandler? PreviewChanged;

    [ObservableProperty]
    private SegmentRowViewModel? selectedSegment;

    [ObservableProperty]
    private string selectedTypeForInsert;

    [ObservableProperty]
    private string segmentParametersTitle = string.Empty;

    [ObservableProperty]
    private string maxChordErrorText = "--";

    [ObservableProperty]
    private string segmentCountText = "--";

    [ObservableProperty]
    private string taperAlignmentText = "--";

    [ObservableProperty]
    private string compensationAxisText = "--";

    partial void OnSelectedSegmentChanged(SegmentRowViewModel? value)
    {
        foreach (SegmentRowViewModel row in Segments)
        {
            row.IsSelected = ReferenceEquals(row, value);
        }

        ShowSegmentParameters(value);
    }

    [RelayCommand]
    private void InsertSegment()
    {
        // 新段默认落在辊身后四分之一，现场再按需要改区间。
        double fromMm = this.geometry.BodyLengthMm * 0.75;
        IRollProfileType profileType = this.profileTypes.Get(SelectedTypeForInsert);

        this.composite = this.composite.Add(RollProfileSegment.Create(
            this.composite.Segments.Count + 1,
            profileType.Key,
            fromMm,
            this.geometry.BodyLengthMm,
            profileType.Schema.CreateDefaults()));

        MarkDirty();
        Rebuild();
    }

    [RelayCommand]
    private void RemoveSegment()
    {
        if (SelectedSegment is null || this.composite.Segments.Count <= 1)
        {
            return;
        }

        this.composite = this.composite.RemoveAt(SelectedSegment.Order);
        MarkDirty();
        Rebuild();
    }

    [RelayCommand]
    private void MoveSegmentUp()
    {
        if (SelectedSegment is null)
        {
            return;
        }

        this.composite = this.composite.MoveUp(SelectedSegment.Order);
        MarkDirty();
        Rebuild();
    }

    [RelayCommand]
    private void Validate() => Rebuild();

    /// <summary>放弃修改：回到上次进入本页时的辊形，而不是清空。</summary>
    public override void DiscardChanges()
    {
        this.composite = this.committedComposite;
        base.DiscardChanges();
        Rebuild();
    }

    /// <summary>切到本页时记住当前状态，"放弃修改"才有东西可回。</summary>
    public override void OnActivated() => this.committedComposite = this.composite;

    private void ShowSegmentParameters(SegmentRowViewModel? row)
    {
        SegmentParameters.Clear();
        if (row is null)
        {
            SegmentParametersTitle = string.Empty;
            return;
        }

        IRollProfileType profileType = this.profileTypes.Get(row.Segment.ProfileTypeKey);
        SegmentParametersTitle = profileType.Key;

        ParameterSet values = profileType.Schema.ApplyDefaults(row.Segment.Parameters);
        foreach (ParameterDescriptor descriptor in profileType.Schema.Descriptors)
        {
            SegmentParameters.Add(new ParameterRowViewModel(descriptor, values.Get(descriptor.Key), Localizer));
        }
    }

    private void Rebuild()
    {
        Segments.Clear();
        foreach (RollProfileSegment segment in this.composite.Segments)
        {
            Segments.Add(new SegmentRowViewModel(
                segment,
                segment.ProfileTypeKey,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"{segment.FromMm:F0} – {segment.ToMm:F0} mm")));
        }

        SelectedSegment = Segments.FirstOrDefault();
        RefreshPreview();
        RefreshValidation();
    }

    private void RefreshPreview()
    {
        try
        {
            RollProfile composed = this.composite.Compose(
                this.geometry, this.profileTypes, this.settings.ProfileSampleCount);
            ComposedPoints = composed.Points
                .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMm(point.RadiusOffsetMm)))
                .ToArray();

            var mainOnly = new CompositeRollProfile(new[] { this.composite.Segments[0] });
            RollProfile main = mainOnly.Compose(this.geometry, this.profileTypes, this.settings.ProfileSampleCount);
            MainPoints = main.Points
                .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMm(point.RadiusOffsetMm)))
                .ToArray();
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
            ComposedPoints = Array.Empty<(double, double)>();
            MainPoints = Array.Empty<(double, double)>();
        }

        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshValidation()
    {
        int sampleCount = this.settings.ProfileSampleCount;
        SegmentCountText = string.Create(
            CultureInfo.CurrentCulture,
            $"{sampleCount - 1} / {this.geometry.BodyLengthMm / (sampleCount - 1):F1} mm");

        MaxChordErrorText = ComposedPoints.Count >= 3
            ? string.Create(CultureInfo.CurrentCulture, $"{EstimateChordErrorMicrometer():F2} µm")
            : "--";

        bool aligned = this.composite.Segments
            .Skip(1)
            .All(segment => segment.FromMm >= 0.0 && segment.ToMm <= this.geometry.BodyLengthMm);
        TaperAlignmentText = Localizer[aligned ? "Profile_Aligned" : "Profile_NotAligned"];

        AxisDescription? crownAxis = this.machine.Axes.FirstOrDefault(axis =>
            axis.IsPresent && string.Equals(axis.Role, CrownAxisRole, StringComparison.Ordinal));
        CompensationAxisText = crownAxis?.Name ?? Localizer["Common_NotConfigured"];
    }

    /// <summary>
    /// 弦高误差：把折线与更细一级采样的曲线比，取最大偏差。
    /// 点数不够时这个值会变大，提醒把采样点数调上去。
    /// </summary>
    private double EstimateChordErrorMicrometer()
    {
        RollProfile fine = this.composite.Compose(
            this.geometry, this.profileTypes, (this.settings.ProfileSampleCount * 2) - 1);
        RollProfile coarse = this.composite.Compose(
            this.geometry, this.profileTypes, this.settings.ProfileSampleCount);

        double worst = 0.0;
        foreach (ProfilePoint point in fine.Points)
        {
            double difference = Math.Abs(point.RadiusOffsetMm - coarse.RadiusOffsetAtMm(point.BodyPositionMm));
            worst = Math.Max(worst, difference);
        }

        return UnitConversion.RadiusMmToDiameterMicrometer(worst);
    }
}
