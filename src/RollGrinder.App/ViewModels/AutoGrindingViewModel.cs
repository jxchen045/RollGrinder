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
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Calibration;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.App.ViewModels;

/// <summary>工序序列里一行的状态。</summary>
public enum StepRowState
{
    /// <summary>还没轮到。</summary>
    Pending = 0,

    /// <summary>正在执行。</summary>
    Current = 1,

    /// <summary>下一道。</summary>
    Next = 2,

    /// <summary>已完成。</summary>
    Done = 3,
}

/// <summary>工序序列里的一行。</summary>
public sealed partial class SequenceRowViewModel : ObservableObject
{
    public SequenceRowViewModel(int order, string displayName, string durationText)
    {
        OrderText = order.ToString("00", CultureInfo.InvariantCulture);
        Order = order;
        DisplayName = displayName;
        DurationText = durationText;
    }

    public int Order { get; }

    public string OrderText { get; }

    public string DisplayName { get; }

    /// <summary>预计时长，例如"约 211 min"。</summary>
    public string DurationText { get; }

    [ObservableProperty]
    private StepRowState state;

    [ObservableProperty]
    private string passText = string.Empty;
}

/// <summary>右侧实时数据里的一行。</summary>
public sealed partial class LiveValueViewModel : ObservableObject
{
    public LiveValueViewModel(string labelResourceKey, IStringLocalizer localizer, bool highlight = false)
    {
        Label = localizer[labelResourceKey];
        Highlight = highlight;
    }

    public string Label { get; }

    public bool Highlight { get; }

    [ObservableProperty]
    private string valueText = "--";
}

/// <summary>主界面可切换的曲线。</summary>
public enum CurveKind
{
    Error = 0,
    Reference = 1,
    Roundness = 2,
    Eccentricity = 3,
    GrindingCurrent = 4,
}

/// <summary>
/// 自动磨削（主界面）。版面见 docs/design/B-Light-自动磨削.html。
/// 只读取监视服务发布的快照与数据库里的作业，不直接碰网关。
/// </summary>
public sealed partial class AutoGrindingViewModel : PageViewModelBase
{
    private readonly IMachineMonitor monitor;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;
    private readonly IGrindingRecordRepository records;
    private readonly IJobRepository jobs;
    private readonly IMeasurementRepository measurements;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly ICalibrationService calibration;

    private readonly LiveValueViewModel probeA;
    private readonly LiveValueViewModel probeB;
    private readonly LiveValueViewModel centringDeviation;
    private readonly LiveValueViewModel xPosition;
    private readonly LiveValueViewModel zPosition;
    private readonly LiveValueViewModel wheelDiameter;
    private readonly LiveValueViewModel grindingCurrent;
    private readonly LiveValueViewModel currentPass;

    private GrindingJob? activeJob;
    private IReadOnlyList<GrindingStepPlan> activePlans = Array.Empty<GrindingStepPlan>();

    public AutoGrindingViewModel(
        IMachineMonitor monitor,
        MachineDescription machine,
        HmiSettings settings,
        IGrindingRecordRepository records,
        IJobRepository jobs,
        IMeasurementRepository measurements,
        GrindingStepTypeRegistry stepTypes,
        RollProfileTypeRegistry profileTypes,
        ICalibrationService calibration,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.measurements = measurements ?? throw new ArgumentNullException(nameof(measurements));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));

        this.probeA = new LiveValueViewModel("Live_ProbeA", localizer);
        this.probeB = new LiveValueViewModel("Live_ProbeB", localizer);
        this.centringDeviation = new LiveValueViewModel("Live_CentringDeviation", localizer, highlight: true);
        this.xPosition = new LiveValueViewModel("Live_XPosition", localizer);
        this.zPosition = new LiveValueViewModel("Live_ZPosition", localizer);
        this.wheelDiameter = new LiveValueViewModel("Live_WheelDiameter", localizer);
        this.grindingCurrent = new LiveValueViewModel("Live_GrindingCurrent", localizer);
        this.currentPass = new LiveValueViewModel("Live_CurrentPass", localizer);

        LiveValues = new ObservableCollection<LiveValueViewModel>
        {
            this.probeA, this.probeB, this.centringDeviation,
            this.xPosition, this.zPosition,
            this.wheelDiameter, this.grindingCurrent, this.currentPass,
        };

        RefreshTolerance();
        calibration.Changed += (_, _) => RefreshTolerance();

        SetFunctionKeys(new[]
        {
            FunctionKeyViewModel.Placeholder("Fn_Start", localizer, () => NotImplementedYet("Fn_Start"), FunctionKeyKind.Start),
            FunctionKeyViewModel.Placeholder("Fn_Pause", localizer, () => NotImplementedYet("Fn_Pause")),
            FunctionKeyViewModel.Placeholder("Fn_SkipStep", localizer, () => NotImplementedYet("Fn_SkipStep")),
            FunctionKeyViewModel.Placeholder("Fn_EndEarly", localizer, () => NotImplementedYet("Fn_EndEarly")),
            FunctionKeyViewModel.Placeholder("Fn_Coolant", localizer, () => NotImplementedYet("Fn_Coolant")),
            // 补偿设置住在工序编程页：派过去，导航槽会显示"返回 自动磨削"。
            FunctionKeyViewModel.Placeholder(
                "Fn_CompensationSettings", localizer, () => Navigator.StartTask(PageKey.Steps, PageKey.AutoGrinding)),
            FunctionKeyViewModel.Placeholder("Fn_Records", localizer, () => Navigator.GoToArea(PageKey.Records)),
        });
    }

    public override PageKey Key => PageKey.AutoGrinding;

    public override string TitleResourceKey => "Page_AutoGrinding";

    public override string MenuHintResourceKey => "Menu_AutoGrindingHint";

    /// <summary>左栏：工序序列。</summary>
    public ObservableCollection<SequenceRowViewModel> Sequence { get; } = new();

    /// <summary>右栏：实时数据。</summary>
    public ObservableCollection<LiveValueViewModel> LiveValues { get; }

    /// <summary>中下：当前工序的工艺参数（只读）。</summary>
    public ObservableCollection<ParameterRowViewModel> StepParameters { get; } = new();

    /// <summary>曲线数据（辊身坐标 mm，直径量 µm）。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> CurvePoints { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>曲线有更新。</summary>
    public event EventHandler? CurveChanged;

    [ObservableProperty]
    private CurveKind selectedCurve = CurveKind.Error;

    [ObservableProperty]
    private string measuredDiameterText = "--";

    [ObservableProperty]
    private string targetDiameterText = "--";

    [ObservableProperty]
    private string remainingStockText = "--";

    [ObservableProperty]
    private string toleranceText = string.Empty;

    [ObservableProperty]
    private string rmsText = "--";

    [ObservableProperty]
    private string progressText = "--";

    [ObservableProperty]
    private string remainingTimeText = "--";

    [ObservableProperty]
    private double progressFraction;

    [ObservableProperty]
    private string stepParametersTitle = string.Empty;

    [ObservableProperty]
    private string feedForwardText = "--";

    [ObservableProperty]
    private string strokeVersionText = "--";

    [ObservableProperty]
    private string realtimeOffsetText = "--";

    [ObservableProperty]
    private bool curveHasData;

    /// <summary>曲线没有数据时给出的原因（例如通道未配置）。</summary>
    [ObservableProperty]
    private string curveEmptyText = string.Empty;

    public override void OnActivated()
    {
        _ = RunGuardedAsync(LoadActiveJobAsync, CancellationToken.None);
    }

    public override void OnTick(DateTimeOffset nowUtc)
    {
        MachineStateSnapshot snapshot = this.monitor.Current;

        this.probeA.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeAMm), "F4", showSign: true);
        this.probeB.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeBMm), "F4", showSign: true);

        double? probeAValue = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeAMm);
        double? probeBValue = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeBMm);
        this.centringDeviation.ValueText = probeAValue is null || probeBValue is null
            ? "--"
            : FormatOrDash((probeAValue.Value - probeBValue.Value) / 2.0, "F4", showSign: true);

        this.xPosition.ValueText = FormatOrDash(AxisPosition(snapshot, MachineAxisRoles.InfeedRadius), "F3", showSign: true);
        this.zPosition.ValueText = FormatOrDash(AxisPosition(snapshot, MachineAxisRoles.Carriage), "F2");
        this.wheelDiameter.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.WheelDiameterMm), "F2");
        this.grindingCurrent.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.GrindingCurrentA), "F1");

        double? pass = snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentPass);
        double? totalPasses = snapshot.GetNumberOrNull(MachineTagKeys.JobTotalPasses);
        this.currentPass.ValueText = pass is null || totalPasses is null
            ? "--"
            : string.Create(CultureInfo.InvariantCulture, $"{(int)pass.Value} / {(int)totalPasses.Value}");

        UpdateDiameters(snapshot);
        UpdateSequence(snapshot);
        UpdateCompensation(snapshot);
    }

    [RelayCommand]
    private void SelectCurve(CurveKind kind)
    {
        SelectedCurve = kind;
        _ = RunGuardedAsync(RefreshCurveAsync, CancellationToken.None);
    }

    private void UpdateDiameters(MachineStateSnapshot snapshot)
    {
        double? measuredDiameterMm = snapshot.GetNumberOrNull(MachineTagKeys.MeasuredDiameterMm);
        MeasuredDiameterText = FormatOrDash(measuredDiameterMm, "F3");

        if (this.activeJob is null)
        {
            TargetDiameterText = "--";
            RemainingStockText = "--";
            return;
        }

        double targetDiameterMm = this.activeJob.Geometry.NominalDiameterMm;
        TargetDiameterText = targetDiameterMm.ToString("F3", CultureInfo.CurrentCulture);
        RemainingStockText = measuredDiameterMm is null
            ? "--"
            : (measuredDiameterMm.Value - targetDiameterMm).ToString("F3", CultureInfo.CurrentCulture);
    }

    private void UpdateSequence(MachineStateSnapshot snapshot)
    {
        if (Sequence.Count == 0)
        {
            return;
        }

        int currentOrder = (int)(snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentStepOrder) ?? 0);
        double? pass = snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentPass);
        double? totalPasses = snapshot.GetNumberOrNull(MachineTagKeys.JobTotalPasses);

        foreach (SequenceRowViewModel row in Sequence)
        {
            row.State = row.Order < currentOrder ? StepRowState.Done
                : row.Order == currentOrder ? StepRowState.Current
                : row.Order == currentOrder + 1 ? StepRowState.Next
                : StepRowState.Pending;

            row.PassText = row.State == StepRowState.Current && pass is not null && totalPasses is not null
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)pass.Value}/{(int)totalPasses.Value}")
                : string.Empty;
        }

        UpdateProgress(currentOrder, pass, totalPasses);
    }

    private void UpdateProgress(int currentOrder, double? pass, double? totalPasses)
    {
        if (Sequence.Count == 0 || currentOrder <= 0)
        {
            ProgressFraction = 0.0;
            ProgressText = "--";
            RemainingTimeText = "--";
            return;
        }

        double withinStep = pass is not null && totalPasses is > 0 ? pass.Value / totalPasses.Value : 0.0;
        double completed = Math.Min(currentOrder - 1 + withinStep, Sequence.Count);
        ProgressFraction = completed / Sequence.Count;
        ProgressText = (ProgressFraction * 100.0).ToString("F0", CultureInfo.CurrentCulture) + "%";

        if (this.activeJob is null || this.activePlans.Count == 0)
        {
            RemainingTimeText = "--";
            return;
        }

        TimeSpan remaining = TimeSpan.Zero;
        for (int i = currentOrder - 1; i < this.activePlans.Count; i++)
        {
            TimeSpan stepDuration = this.activePlans[i].EstimateDuration(this.activeJob.Geometry);
            remaining += i == currentOrder - 1 ? stepDuration * (1.0 - withinStep) : stepDuration;
        }

        RemainingTimeText = remaining.TotalHours >= 1.0
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}")
            : string.Create(CultureInfo.InvariantCulture, $"00:{(int)remaining.TotalMinutes:00}");
    }

    private void UpdateCompensation(MachineStateSnapshot snapshot)
    {
        double? feedForwardA = snapshot.GetNumberOrNull(MachineTagKeys.CompensationFeedForwardA);
        double? feedForwardB = snapshot.GetNumberOrNull(MachineTagKeys.CompensationFeedForwardB);
        FeedForwardText = feedForwardA is null || feedForwardB is null
            ? "--"
            : string.Create(CultureInfo.InvariantCulture, $"a {feedForwardA.Value:F3} / b {feedForwardB.Value:0.0e+0}");

        double? version = snapshot.GetNumberOrNull(MachineTagKeys.CompensationStrokeVersion);
        StrokeVersionText = version is null ? "--" : "v " + ((int)version.Value).ToString(CultureInfo.InvariantCulture);

        RealtimeOffsetText = FormatOrDash(
            snapshot.GetNumberOrNull(MachineTagKeys.CompensationRealtimeOffsetMm), "F4", showSign: true);
    }

    private double? AxisPosition(MachineStateSnapshot snapshot, string role)
    {
        AxisDescription? axis = this.machine.Axes.FirstOrDefault(candidate =>
            candidate.IsPresent && string.Equals(candidate.Role, role, StringComparison.Ordinal));

        return axis is null ? null : snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(axis.Name));
    }

    private async Task LoadActiveJobAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GrindingRecord> recent = await this.records
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), 1, cancellationToken)
            .ConfigureAwait(true);

        Sequence.Clear();
        StepParameters.Clear();
        this.activeJob = null;
        this.activePlans = Array.Empty<GrindingStepPlan>();

        if (recent.Count == 0)
        {
            StepParametersTitle = Localizer["Auto_NoActiveJob"];
            CurveHasData = false;
            CurveEmptyText = Localizer["Auto_NoActiveJob"];
            return;
        }

        (GrindingJob Job, JobState State)? stored = await this.jobs
            .GetAsync(recent[0].JobId, cancellationToken).ConfigureAwait(true);
        if (stored is null)
        {
            return;
        }

        this.activeJob = stored.Value.Job;
        this.activePlans = this.activeJob.Steps
            .Select(step => this.stepTypes.Get(step.StepTypeKey).CreatePlan(this.activeJob.Geometry, step.Parameters))
            .ToArray();

        for (int i = 0; i < this.activeJob.Steps.Count; i++)
        {
            GrindingJobStep step = this.activeJob.Steps[i];
            TimeSpan duration = this.activePlans[i].EstimateDuration(this.activeJob.Geometry);
            Sequence.Add(new SequenceRowViewModel(
                step.Order,
                Localizer["StepType_" + step.StepTypeKey],
                duration > TimeSpan.Zero
                    ? Localizer.Format("Auto_StepDurationFormat", (int)duration.TotalMinutes)
                    : string.Empty));
        }

        ShowStepParameters(this.activeJob.Steps[0]);
        await RefreshCurveAsync(cancellationToken).ConfigureAwait(true);
    }

    private void ShowStepParameters(GrindingJobStep step)
    {
        StepParameters.Clear();
        IGrindingStepType stepType = this.stepTypes.Get(step.StepTypeKey);
        StepParametersTitle = Localizer["StepType_" + step.StepTypeKey];

        foreach (Core.Parameters.ParameterDescriptor descriptor in stepType.Schema.Descriptors)
        {
            if (step.Parameters.TryGet(descriptor.Key, out Core.Parameters.ParameterValue? value) && value is not null)
            {
                StepParameters.Add(new ParameterRowViewModel(descriptor, value, Localizer));
            }
        }
    }

    private async Task RefreshCurveAsync(CancellationToken cancellationToken)
    {
        CurvePoints = Array.Empty<(double, double)>();
        CurveHasData = false;
        CurveEmptyText = string.Empty;
        RmsText = "--";

        if (this.activeJob is null)
        {
            CurveEmptyText = Localizer["Auto_NoActiveJob"];
            CurveChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (SelectedCurve)
        {
            case CurveKind.Reference:
                BuildReferenceCurve();
                break;

            case CurveKind.Error:
                await BuildErrorCurveAsync(cancellationToken).ConfigureAwait(true);
                break;

            default:
                // 圆度、偏心度、磨削电流曲线需要机床侧对应的测量通道，
                // machine.json 里没有描述就如实说明，不画一条编出来的线。
                CurveEmptyText = Localizer["Auto_CurveChannelNotConfigured"];
                break;
        }

        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 参考曲线 = 把作业里各段辊形合成之后的整条目标曲线。
    /// 端部锥度与倒角也画在里面——只画主辊形，两端就跟实测对不上。
    /// </summary>
    private RollProfile TargetProfile() => this.activeJob!.Profile.Compose(
        this.activeJob.Geometry, this.profileTypes, this.settings.ProfileSampleCount);

    /// <summary>公差是现场标定值，设置页上改完这里跟着变。</summary>
    private void RefreshTolerance() => ToleranceText = Localizer.Format(
        "Auto_ToleranceFormat", this.calibration.Current.ProfileToleranceMicrometer);

    private void BuildReferenceCurve()
    {
        RollProfile target = TargetProfile();

        CurvePoints = target.Points
            .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();
        CurveHasData = true;
    }

    private async Task BuildErrorCurveAsync(CancellationToken cancellationToken)
    {
        MeasurementRecord? measurement = await this.measurements
            .GetLatestByJobAsync(this.activeJob!.JobId, cancellationToken).ConfigureAwait(true);
        if (measurement is null)
        {
            CurveEmptyText = Localizer["Auto_NoMeasurementYet"];
            return;
        }

        RollProfile deviation = CompensationCalculator.ComputeDeviation(
            measurement.Profile, TargetProfile(), this.activeJob.Geometry);

        CurvePoints = deviation.Points
            .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();
        CurveHasData = true;

        double sumOfSquares = CurvePoints.Sum(point => point.DiameterMicrometer * point.DiameterMicrometer);
        double rms = Math.Sqrt(sumOfSquares / CurvePoints.Count);
        RmsText = Localizer.Format("Auto_RmsFormat", rms);
    }

    private static string FormatOrDash(double? value, string format, bool showSign = false)
    {
        if (value is null)
        {
            return "--";
        }

        string text = value.Value.ToString(format, CultureInfo.CurrentCulture);
        return showSign && value.Value >= 0.0 ? "+" + text : text;
    }
}
