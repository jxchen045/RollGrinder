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
using RollGrinder.Services.Manual;
using RollGrinder.Services.Measurement;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 按钮矩阵里的一个动作。
///
/// 按钮有四种样子：可按、压暗（tagmap 没登记）、禁用（自动循环挂着程序）、
/// 待确认（危险动作按第一下之后）。保持型动作亮着表示正开着。
/// </summary>
public sealed partial class MachineActionViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;

    public MachineActionViewModel(
        ManualCommandDescriptor descriptor,
        IStringLocalizer localizer,
        System.Windows.Input.ICommand command)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Command = command ?? throw new ArgumentNullException(nameof(command));
    }

    public ManualCommandDescriptor Descriptor { get; }

    public System.Windows.Input.ICommand Command { get; }

    /// <summary>按钮上的字。等确认时换成"再按一次"，让人知道第一下没白按。</summary>
    public string Label => IsAwaitingConfirmation
        ? this.localizer["Manual_ConfirmAgain"]
        : this.localizer[Descriptor.ResourceKey];

    /// <summary>原本的动作名，报警与提示里用。</summary>
    public string ActionName => this.localizer[Descriptor.ResourceKey];

    /// <summary>tagmap 里登记了这个动作没有。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool isMapped = true;

    /// <summary>现在能不能按（连接、通道状态）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool isAllowed = true;

    /// <summary>保持型动作当前是不是开着。</summary>
    [ObservableProperty]
    private bool isActive;

    /// <summary>危险动作按了第一下，正等第二下。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private bool isAwaitingConfirmation;

    /// <summary>按钮可不可按。没登记的动作压暗但留在原位——键位不跳动。</summary>
    public bool IsEnabled => IsMapped && IsAllowed;

    /// <summary>不能按时的说明，做成 ToolTip：让人知道是缺映射还是机床在忙。</summary>
    [ObservableProperty]
    private string? disabledHint;

    /// <summary>第二下的截止时刻；过了就自动撤销，免得一小时后误触当成确认。</summary>
    internal DateTimeOffset ConfirmDeadlineUtc { get; set; }
}

/// <summary>两端比对表里的一行。</summary>
public sealed record EndComparisonRow(string PositionText, string ProbeAText, string ProbeBText, string DeviationText);

/// <summary>
/// 手动与辅助操作。版面见 docs/design/B-Manual-手动与辅助操作.html。
/// 左侧是只读的实时数据与对中判断，右侧是动作按钮矩阵。
/// </summary>
public sealed partial class ManualViewModel : PageViewModelBase
{
    /// <summary>危险动作第二下的等待窗口。太短来不及按，太长就成了误触的机会。</summary>
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(4.0);

    /// <summary>"已发出"提示在界面上停留多久。</summary>
    private static readonly TimeSpan FeedbackWindow = TimeSpan.FromSeconds(3.0);

    private readonly IMachineMonitor monitor;
    private readonly IMeasurementService measurementService;
    private readonly IManualCommandService commands;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;

    private DateTimeOffset feedbackExpiryUtc;

    public ManualViewModel(
        IMachineMonitor monitor,
        IMeasurementService measurementService,
        IManualCommandService commands,
        MachineDescription machine,
        HmiSettings settings,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.measurementService = measurementService ?? throw new ArgumentNullException(nameof(measurementService));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
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

        MeasuringArmActions = BuildActions(ManualCommandCatalog.MeasuringArm);
        TailstockActions = BuildActions(ManualCommandCatalog.Tailstock);
        OtherActions = BuildActions(ManualCommandCatalog.Other);
        RefreshActionAvailability();

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

    /// <summary>三组按钮的合集，刷新状态时遍历它。</summary>
    private IEnumerable<MachineActionViewModel> AllActions =>
        MeasuringArmActions.Concat(TailstockActions).Concat(OtherActions);

    /// <summary>最近一个动作的"已发出"提示，停留几秒后自己消失。</summary>
    [ObservableProperty]
    private string lastActionText = string.Empty;

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

        RefreshActionAvailability();
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

    private ObservableCollection<MachineActionViewModel> BuildActions(
        IReadOnlyList<ManualCommandDescriptor> descriptors)
    {
        var actions = new ObservableCollection<MachineActionViewModel>();
        foreach (ManualCommandDescriptor descriptor in descriptors)
        {
            MachineActionViewModel action = null!;
            action = new MachineActionViewModel(
                descriptor,
                Localizer,
                new AsyncRelayCommand(() => PressAsync(action, CancellationToken.None)));
            actions.Add(action);
        }

        return actions;
    }

    /// <summary>
    /// 按下一个动作按钮。
    ///
    /// 顺序是：先问能不能按（缺映射 / 没连上 / 机床在忙），不能按就说清楚原因；
    /// 危险动作第一下只是"预备"，第二下才真发；本地动作（测量采样）不写机床。
    /// </summary>
    private async Task PressAsync(MachineActionViewModel action, CancellationToken cancellationToken)
    {
        ManualCommandDescriptor descriptor = action.Descriptor;

        ManualCommandResult permission = this.commands.CanExecute(descriptor);
        if (!permission.Succeeded)
        {
            CancelConfirmation(action);
            Alarms.Raise(AlarmSeverity.Warning, permission.ReasonResourceKey!, action.ActionName);
            return;
        }

        if (descriptor.RequiresConfirmation && !action.IsAwaitingConfirmation)
        {
            BeginConfirmation(action);
            return;
        }

        CancelConfirmation(action);

        if (descriptor.Kind == ManualCommandKind.Local)
        {
            // 测量采样不写机床：走测量服务把当前读数存成一个测点。
            await CapturePointAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        await RunGuardedAsync(async token =>
        {
            ManualCommandResult result = await this.commands
                .ExecuteAsync(descriptor, desiredState: null, token).ConfigureAwait(true);

            if (result.Succeeded)
            {
                ShowFeedback(action.ActionName);
                return;
            }

            Alarms.Raise(AlarmSeverity.Warning, result.ReasonResourceKey!, action.ActionName);
        }, cancellationToken).ConfigureAwait(true);
    }

    private void BeginConfirmation(MachineActionViewModel action)
    {
        // 同一时刻只留一个待确认的按钮，免得两个红按钮并排让人按错。
        foreach (MachineActionViewModel other in AllActions)
        {
            CancelConfirmation(other);
        }

        action.IsAwaitingConfirmation = true;
        action.ConfirmDeadlineUtc = DateTimeOffset.UtcNow + ConfirmationWindow;
    }

    private static void CancelConfirmation(MachineActionViewModel action)
    {
        action.IsAwaitingConfirmation = false;
        action.ConfirmDeadlineUtc = default;
    }

    private void ShowFeedback(string actionName)
    {
        LastActionText = Localizer.Format("Manual_CommandSentFormat", actionName);
        this.feedbackExpiryUtc = DateTimeOffset.UtcNow + FeedbackWindow;
    }

    /// <summary>
    /// 每一拍刷新按钮状态：缺映射的压暗、机床在忙的禁用、保持型的点亮、
    /// 待确认的到点自动撤销。全部按机床的当前快照算，不缓存判断。
    /// </summary>
    private void RefreshActionAvailability()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        foreach (MachineActionViewModel action in AllActions)
        {
            ManualCommandResult permission = this.commands.CanExecute(action.Descriptor);

            action.IsMapped = permission.Outcome != ManualCommandOutcome.NotMapped;
            action.IsAllowed = permission.Succeeded;
            action.DisabledHint = permission.Succeeded ? null : Localizer[permission.ReasonResourceKey!];
            action.IsActive = this.commands.ReadState(action.Descriptor) ?? false;

            if (action.IsAwaitingConfirmation
                && (nowUtc > action.ConfirmDeadlineUtc || !permission.Succeeded))
            {
                CancelConfirmation(action);
            }
        }

        if (LastActionText.Length > 0 && nowUtc > this.feedbackExpiryUtc)
        {
            LastActionText = string.Empty;
        }
    }

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
