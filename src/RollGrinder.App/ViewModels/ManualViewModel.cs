using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Units;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Measurement;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.App.ViewModels;

/// <summary>按钮矩阵里的一个动作。</summary>
public sealed class MachineActionViewModel
{
    public MachineActionViewModel(string labelResourceKey, IStringLocalizer localizer, System.Windows.Input.ICommand command)
    {
        Label = localizer[labelResourceKey];
        Command = command;
    }

    public string Label { get; }

    public System.Windows.Input.ICommand Command { get; }
}

/// <summary>两端比对表里的一行。</summary>
public sealed record EndComparisonRow(string PositionText, string ProbeAText, string ProbeBText, string DeviationText);

/// <summary>
/// 手动与辅助操作。版面见 docs/design/B-Manual-手动与辅助操作.html。
/// 左侧是只读的实时数据与对中判断，右侧是动作按钮矩阵。
/// </summary>
public sealed partial class ManualViewModel : PageViewModelBase
{
    private readonly IMachineMonitor monitor;
    private readonly IMeasurementService measurementService;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;

    public ManualViewModel(
        IMachineMonitor monitor,
        IMeasurementService measurementService,
        MachineDescription machine,
        HmiSettings settings,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.measurementService = measurementService ?? throw new ArgumentNullException(nameof(measurementService));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

        AxisValues = new ObservableCollection<LiveValueViewModel>(
            machine.Axes.Where(axis => axis.IsPresent)
                .Select(axis => new LiveValueViewModel("AxisRole_" + axis.Role, localizer)));
        this.axisNames = machine.Axes.Where(axis => axis.IsPresent).Select(axis => axis.Name).ToArray();

        SpindleValues = new ObservableCollection<LiveValueViewModel>
        {
            new("Live_WheelSpeed", localizer),
            new("Live_WheelDiameter", localizer),
            new("Live_GrindingCurrent", localizer),
            new("Live_WorkpieceSpeed", localizer),
        };

        MeasuringArmActions = BuildActions(
            "Action_ProbeALower", "Action_ProbeARaise", "Action_ProbeBLower", "Action_ProbeBRaise",
            "Action_ProbesToRoll", "Action_ProbesHome", "Action_CalibrateProbes", "Action_SampleMeasurement");

        TailstockActions = BuildActions(
            "Action_QuillExtend", "Action_QuillRetract", "Action_TailstockForward", "Action_TailstockBackward",
            "Action_TailstockClamp", "Action_TailstockRelease");

        OtherActions = BuildActions(
            "Action_HeadstockStart", "Action_HeadstockUp", "Action_HeadstockDown", "Action_DriverExtend",
            "Action_DriverRetract", "Action_U1AxisZero", "Action_SoftLandingUp", "Action_SoftLandingDown",
            "Action_Coolant", "Action_WheelStart", "Action_MeasureWheelDiameter", "Action_AllAxesHome");

        SetFunctionKeys(new[]
        {
            FunctionKeyViewModel.Placeholder("Fn_ManualGrinding", localizer, () => NotImplementedYet("Fn_ManualGrinding"), FunctionKeyKind.Primary),
            FunctionKeyViewModel.Placeholder("Fn_CalibrateDatum", localizer, () => NotImplementedYet("Fn_CalibrateDatum")),
            FunctionKeyViewModel.Placeholder("Fn_WheelDress", localizer, () => NotImplementedYet("Fn_WheelDress")),
            FunctionKeyViewModel.Placeholder("Fn_RollAlign", localizer, () => NotImplementedYet("Fn_RollAlign")),
            FunctionKeyViewModel.Placeholder("Fn_ReferencePoint", localizer, () => NotImplementedYet("Fn_ReferencePoint")),
            FunctionKeyViewModel.Placeholder(
                "Fn_Diagnostics", localizer, () => Navigator.StartTask(PageKey.Diagnostics, PageKey.Manual)),
            FunctionKeyViewModel.Placeholder("Fn_HmiReset", localizer, () => NotImplementedYet("Fn_HmiReset"), FunctionKeyKind.Danger),
        });
    }

    private readonly IReadOnlyList<string> axisNames;

    public override PageKey Key => PageKey.Manual;

    public override string TitleResourceKey => "Page_Manual";

    public override string MenuHintResourceKey => "Menu_ManualHint";

    public ObservableCollection<LiveValueViewModel> AxisValues { get; }

    public ObservableCollection<LiveValueViewModel> SpindleValues { get; }

    public ObservableCollection<MachineActionViewModel> MeasuringArmActions { get; }

    public ObservableCollection<MachineActionViewModel> TailstockActions { get; }

    public ObservableCollection<MachineActionViewModel> OtherActions { get; }

    /// <summary>手动采下的测点。</summary>
    public ObservableCollection<MeasurementRowViewModel> Points { get; } = new();

    /// <summary>两端比对（用于中心架找正）。</summary>
    public ObservableCollection<EndComparisonRow> EndComparison { get; } = new();

    [ObservableProperty]
    private string probeAText = "--";

    [ObservableProperty]
    private string probeBText = "--";

    [ObservableProperty]
    private string measuredDiameterText = "--";

    [ObservableProperty]
    private string mountingDeviationText = "--";

    [ObservableProperty]
    private string alignmentHintText = string.Empty;

    [ObservableProperty]
    private bool hasAlignmentProblem;

    [ObservableProperty]
    private string jobId = string.Empty;

    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : Localizer[StatusResourceKey];

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    public override void OnTick(DateTimeOffset nowUtc)
    {
        MachineStateSnapshot snapshot = this.monitor.Current;

        for (int i = 0; i < AxisValues.Count; i++)
        {
            AxisValues[i].ValueText = Format(
                snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(this.axisNames[i])), "F4");
        }

        SpindleValues[0].ValueText = Format(snapshot.GetNumberOrNull(MachineTagKeys.WheelSpeedRpm), "F1");
        SpindleValues[1].ValueText = Format(snapshot.GetNumberOrNull(MachineTagKeys.WheelDiameterMm), "F2");
        SpindleValues[2].ValueText = Format(snapshot.GetNumberOrNull(MachineTagKeys.GrindingCurrentA), "F1");
        SpindleValues[3].ValueText = Format(WorkpieceSpeed(snapshot), "F1");

        double? probeA = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeAMm);
        double? probeB = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeBMm);
        ProbeAText = Format(probeA, "F4", showSign: true);
        ProbeBText = Format(probeB, "F4", showSign: true);
        MeasuredDiameterText = Format(snapshot.GetNumberOrNull(MachineTagKeys.MeasuredDiameterMm), "F3");
        MountingDeviationText = probeA is null || probeB is null
            ? "--"
            : Format((probeA.Value - probeB.Value) / 2.0, "F4", showSign: true);
    }

    [RelayCommand]
    private Task CapturePointAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            MeasurementPoint point = await this.measurementService.CapturePointAsync(token).ConfigureAwait(true);
            Points.Add(new MeasurementRowViewModel(point));
            StatusResourceKey = "Measurement_PointCaptured";
            RefreshEndComparison();
        }, cancellationToken);

    [RelayCommand]
    private void ClearPoints()
    {
        Points.Clear();
        EndComparison.Clear();
        AlignmentHintText = string.Empty;
        HasAlignmentProblem = false;
        StatusResourceKey = string.Empty;
    }

    [RelayCommand]
    private Task SaveMeasurementAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(JobId) || Points.Count < 2)
            {
                StatusResourceKey = "Measurement_NotEnoughPoints";
                return;
            }

            await this.measurementService.SaveAsync(
                JobId, Points.Select(row => row.Point).ToArray(), "manual", token).ConfigureAwait(true);
            StatusResourceKey = "Measurement_Saved";
        }, cancellationToken);

    /// <summary>
    /// 两端比对：取最靠头架侧与最靠尾座侧的两个测点，算出两端差与折合锥度。
    /// 超出公差时给出往哪边调中心架的提示。
    /// </summary>
    private void RefreshEndComparison()
    {
        EndComparison.Clear();
        AlignmentHintText = string.Empty;
        HasAlignmentProblem = false;

        if (Points.Count < 2)
        {
            return;
        }

        MeasurementRowViewModel head = Points.OrderBy(row => row.Point.BodyPositionMm).First();
        MeasurementRowViewModel tail = Points.OrderBy(row => row.Point.BodyPositionMm).Last();

        EndComparison.Add(Describe("Manual_HeadSide", head));
        EndComparison.Add(Describe("Manual_TailSide", tail));

        double differenceMm = UnitConversion.RadiusMmToDiameterMm(
            head.Point.MeasuredRadiusMm - tail.Point.MeasuredRadiusMm);
        double toleranceMm = UnitConversion.MicrometerToMm(this.settings.ProfileToleranceDiameterMicrometer);

        if (Math.Abs(differenceMm) <= toleranceMm)
        {
            return;
        }

        HasAlignmentProblem = true;
        AlignmentHintText = Localizer.Format(
            differenceMm > 0.0 ? "Manual_AlignHeadInward" : "Manual_AlignTailInward",
            Math.Abs(differenceMm));
    }

    private EndComparisonRow Describe(string positionResourceKey, MeasurementRowViewModel row) => new(
        Localizer[positionResourceKey],
        row.DiameterText,
        "--",
        Format(row.Point.MeasuredRadiusMm, "F4", showSign: true));

    private double? WorkpieceSpeed(MachineStateSnapshot snapshot)
    {
        AxisDescription? spindle = this.machine.Axes.FirstOrDefault(axis =>
            axis.IsPresent && string.Equals(axis.Role, MachineAxisRoles.WorkpieceSpindle, StringComparison.Ordinal));

        return spindle is null ? null : snapshot.GetNumberOrNull(MachineTagKeys.AxisActualSpeedRpm(spindle.Name));
    }

    private ObservableCollection<MachineActionViewModel> BuildActions(params string[] labelResourceKeys) =>
        new(labelResourceKeys.Select(key => new MachineActionViewModel(
            key,
            Localizer,
            new RelayCommand(() => NotWiredToPlc(key)))));

    /// <summary>
    /// 这些动作要往 PLC 写命令位，tagmap 里还没有登记对应变量。
    /// 与其装作按下去有效，不如当场说清楚缺什么。
    /// </summary>
    private void NotWiredToPlc(string labelResourceKey) =>
        Alarms.Raise(AlarmSeverity.Information, "Alarm_ActionNeedsTagMapping", Localizer[labelResourceKey]);

    private static string Format(double? value, string format, bool showSign = false)
    {
        if (value is null)
        {
            return "--";
        }

        string text = value.Value.ToString(format, CultureInfo.CurrentCulture);
        return showSign && value.Value >= 0.0 ? "+" + text : text;
    }
}
