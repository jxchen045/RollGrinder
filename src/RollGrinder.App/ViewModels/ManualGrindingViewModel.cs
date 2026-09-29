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
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Manual;
using RollGrinder.Services.Monitoring;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 手动磨削（界面最终稿 5.1）：机床区 JOG 方式的基本画面，取代 MDI。
///
/// 主角是位置窗（X、X1、Z、U，L1 54 px，手持盒选中的轴青底"◀"）；旁边是测量、机构状态；
/// 下面是砂轮、头架、拖板三个驱动单元（实际值 L2，给定与倍率可改）和沿辊身的位置图。
///
/// 最高原则：这里发的全是"命令位 + 参数"的请求，PLC 按上升沿触发并自复位；
/// 屏幕上没有"按住才动"的键；停止类不问、也都有按钮板入口。上位机被强制结束时，
/// 已经在转的砂轮、在走的拖板保持原状，由在场的操作者用按钮板停下。
/// </summary>
public sealed partial class ManualGrindingViewModel : PageViewModelBase
{
    /// <summary>喂给定位循环的速度找不到配置时用的数（mm/min）。</summary>
    private const double DefaultPositioningFeed = 2000.0;

    private readonly IMachineMonitor monitor;
    private readonly IManualGrindingService grinding;
    private readonly MachineDescription machine;
    private readonly IGrindingRecordRepository records;
    private readonly IJobRepository jobs;
    private readonly ManualActionKeys actionKeys;
    private readonly FunctionKeyViewModel carriageKey;
    private readonly FunctionKeyViewModel assistKey;
    private readonly FunctionKeyViewModel positionKey;
    private readonly List<(FunctionKeyViewModel Key, string[] Tags)> tagGatedKeys = new();
    private double? lastDiameter;
    private double? previousDiameter;

    public ManualGrindingViewModel(
        IMachineMonitor monitor,
        IManualGrindingService grinding,
        IManualCommandService commands,
        MachineDescription machine,
        IGrindingRecordRepository records,
        IJobRepository jobs,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.grinding = grinding ?? throw new ArgumentNullException(nameof(grinding));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.actionKeys = new ManualActionKeys(
            commands ?? throw new ArgumentNullException(nameof(commands)), interaction, localizer, alarms, _ => Task.CompletedTask);

        Axes = AxisReadoutViewModel.For(
            machine, localizer,
            MachineAxisRoles.InfeedRadius, MachineAxisRoles.MeasuringCarriage, MachineAxisRoles.Carriage, MachineAxisRoles.RollProfile);
        Mechanism = MachineStatusCatalog.All
            .Where(indicator => indicator != MachineStatusCatalog.WheelRunning
                && indicator != MachineStatusCatalog.HeadstockForward
                && indicator != MachineStatusCatalog.HeadstockReverse)
            .Select(indicator => new StatusLampViewModel(indicator, localizer))
            .ToArray();
        WheelLamp = new StatusLampViewModel(MachineStatusCatalog.WheelRunning, localizer);
        HeadstockForwardLamp = new StatusLampViewModel(MachineStatusCatalog.HeadstockForward, localizer);
        HeadstockReverseLamp = new StatusLampViewModel(MachineStatusCatalog.HeadstockReverse, localizer);

        double wheelMin = Threshold("minWheelSurfaceSpeedMPerSec") ?? 10.0;
        double wheelMax = Threshold("maxWheelSurfaceSpeedMPerSec") ?? 45.0;
        double headstockMax = Axis(MachineAxisRoles.WorkpieceSpindle)?.MaxSpeedRpm ?? 120.0;
        double carriageMax = Axis(MachineAxisRoles.Carriage)?.MaxFeedMmPerMin ?? 6000.0;
        double carriageMin = Axis(MachineAxisRoles.Carriage)?.MinPositionMm ?? 0.0;
        double carriageTravel = Axis(MachineAxisRoles.Carriage)?.MaxPositionMm ?? 6000.0;

        WheelSetpoint = new SetpointViewModel("MG_WheelSetpoint", "Unit_MeterPerSecond", wheelMin, wheelMax, 1, 0, localizer, OnMachineValueChanged);
        WheelOverride = new SetpointViewModel("MG_WheelOverride", "Unit_Percent", 50, 100, 0, 5, localizer, OnMachineValueChanged);
        HeadstockSetpoint = new SetpointViewModel("MG_HeadstockSetpoint", "Unit_RevolutionsPerMinute", 0, headstockMax, 0, 0, localizer, OnMachineValueChanged);
        HeadstockOverride = new SetpointViewModel("MG_HeadstockOverride", "Unit_Percent", 50, 100, 0, 5, localizer, OnMachineValueChanged);
        TraverseSpeed = new SetpointViewModel("MG_TraverseSpeed", "Unit_MillimeterPerMinute", 1, carriageMax, 0, 0, localizer, OnLocalValueChanged);
        FeedOverride = new SetpointViewModel("MG_FeedOverride", "Unit_Percent", 20, 100, 0, 5, localizer, OnMachineValueChanged);
        StrokeStart = new SetpointViewModel("MG_StrokeStart", "Unit_Millimeter", carriageMin, carriageTravel, 1, 0, localizer, OnLocalValueChanged);
        StrokeEnd = new SetpointViewModel("MG_StrokeEnd", "Unit_Millimeter", carriageMin, carriageTravel, 1, 0, localizer, OnLocalValueChanged);
        PositionTarget = new SetpointViewModel("MG_PositionTarget", "Unit_Millimeter", carriageMin, carriageTravel, 1, 0, localizer, OnLocalValueChanged);
        TraverseSpeed.Accept(Math.Min(1200.0, carriageMax));

        SetFunctionKeys(MachineAreaKeys.Create(Navigator, localizer, MachineAreaKeys.Grinding));

        this.carriageKey = new FunctionKeyViewModel("MG_CarriageStart", new AsyncRelayCommand(PressCarriageAsync), localizer)
        {
            IsMachineCommand = true,
            RequiredPermission = Permission.RunMachine,
        };
        this.assistKey = new FunctionKeyViewModel("MG_HeadstockAssist", new AsyncRelayCommand(ToggleAssistAsync), localizer)
        {
            IsMachineCommand = true,
            RequiredPermission = Permission.RunMachine,
        };
        this.positionKey = new FunctionKeyViewModel("MG_Position", new RelayCommand(OpenPositionMenu), localizer)
        {
            IsMachineCommand = true,
            RequiredPermission = Permission.RunMachine,
        };
        Gate(this.carriageKey, MachineTagKeys.ManualCarriageStart, MachineTagKeys.ManualCarriageStop, MachineTagKeys.ManualCarriageSpeed,
            MachineTagKeys.ManualCarriageStrokeStart, MachineTagKeys.ManualCarriageStrokeEnd);
        Gate(this.assistKey, MachineTagKeys.ManualHeadstockAssist);
        Gate(this.positionKey, MachineTagKeys.ManualPositionAxis, MachineTagKeys.ManualPositionTarget,
            MachineTagKeys.ManualPositionSpeed, MachineTagKeys.ManualPositionStart);

        // 竖键（最终稿 5.1）：1 砂轮启动… ⇄ 砂轮停止；2 拖板往复… ⇄ 拖板停止；3 冷却液；4 带启动装置；
        // 5 定位 ▸；6 各轴归位…；7 空（待确认时"✕ 取消"）；8 ≡▸ 第二页：测砂轮直径…、校测量臂…。
        SetVerticalKeys(new FunctionKeyViewModel?[]
        {
            this.actionKeys.Create("wheel.run"),
            this.carriageKey,
            this.actionKeys.Create("coolant"),
            this.assistKey,
            this.positionKey,
            this.actionKeys.Create("axes.home"),
            null,
            this.actionKeys.Create("wheel.measureDiameter"),
            this.actionKeys.Create("arms.calibrate"),
        });
    }

    public override PageKey Key => PageKey.ManualGrinding;

    public override string TitleResourceKey => "Page_ManualGrinding";

    public override string? HelpTopicKey => "Help_ManualGrinding";

    /// <summary>位置窗：X、X1、Z、U（机床上装了的）。</summary>
    public IReadOnlyList<AxisReadoutViewModel> Axes { get; }

    /// <summary>机构窗：全部到位灯（取代原"状态总览"页，最终稿 F3）。</summary>
    public IReadOnlyList<StatusLampViewModel> Mechanism { get; }

    public StatusLampViewModel WheelLamp { get; }

    public StatusLampViewModel HeadstockForwardLamp { get; }

    public StatusLampViewModel HeadstockReverseLamp { get; }

    public SetpointViewModel WheelSetpoint { get; }

    public SetpointViewModel WheelOverride { get; }

    public SetpointViewModel HeadstockSetpoint { get; }

    public SetpointViewModel HeadstockOverride { get; }

    public SetpointViewModel TraverseSpeed { get; }

    public SetpointViewModel FeedOverride { get; }

    public SetpointViewModel StrokeStart { get; }

    public SetpointViewModel StrokeEnd { get; }

    /// <summary>"定位 ▸ 拖板到 Z…"的目标；点位置图上一处就填上。</summary>
    public SetpointViewModel PositionTarget { get; }

    /// <summary>Z1 − Z2 同步差。</summary>
    [ObservableProperty]
    private string syncDiffText = "--";

    [ObservableProperty]
    private string measuredDiameterText = "--";

    [ObservableProperty]
    private string probeAText = "--";

    [ObservableProperty]
    private string probeBText = "--";

    /// <summary>与上一点差（直径，mm）。</summary>
    [ObservableProperty]
    private string diameterStepText = "--";

    /// <summary>安装误差 (A − B) / 2。</summary>
    [ObservableProperty]
    private string mountingErrorText = "--";

    /// <summary>测量窗标题上写的测量臂状态（外 A 放下 · 内 B 放下）。</summary>
    [ObservableProperty]
    private string armStateText = string.Empty;

    /// <summary>砂轮实际转速 S2（r/min）。</summary>
    [ObservableProperty]
    private string wheelSpeedText = "--";

    /// <summary>砂轮线速度（m/s，由转速与直径算）与直径。</summary>
    [ObservableProperty]
    private string wheelSurfaceText = "--";

    [ObservableProperty]
    private string grindingCurrentText = "--";

    /// <summary>磨削电流占上限的比例（电流条，0–1）。</summary>
    [ObservableProperty]
    private double grindingCurrentFraction;

    /// <summary>电流超过设定上限（条变红）。</summary>
    [ObservableProperty]
    private bool isCurrentHigh;

    [ObservableProperty]
    private string headstockSpeedText = "--";

    /// <summary>拖板在往复。</summary>
    [ObservableProperty]
    private bool isCarriageRunning;

    [ObservableProperty]
    private string carriageStateText = string.Empty;

    /// <summary>辊身长度（位置图用）；没有作业时为 0，图只画机床行程。</summary>
    [ObservableProperty]
    private double bodyLengthMm;

    /// <summary>拖板当前 Z（位置图用）。</summary>
    [ObservableProperty]
    private double? carriageZ;

    /// <summary>拖板行程上限（位置图横轴）。</summary>
    public double CarriageTravelMm => StrokeStart.Maximum;

    public override void OnActivated()
    {
        _ = RunGuardedAsync(LoadRollAsync, CancellationToken.None);

        // 最终稿 M7：进手动磨削请求 JOG。只是请求——PLC 决定切不切；机床在跑就不请求。
        MachineStateSnapshot snapshot = this.monitor.Current;
        if (snapshot.GetNumberOrNull(MachineTagKeys.OperatingMode) is { } mode && (int)mode != (int)MachineMode.Jog
            && snapshot.GetNumberOrNull(MachineTagKeys.ChannelState) is 0.0)
        {
            _ = RunGuardedAsync(
                async token => await this.grinding.RequestModeAsync(MachineModeRequest.Jog, token).ConfigureAwait(true),
                CancellationToken.None);
        }
    }

    public override void OnTick(DateTimeOffset nowUtc)
    {
        MachineStateSnapshot snapshot = this.monitor.Current;

        foreach (AxisReadoutViewModel axis in Axes)
        {
            axis.Update(snapshot);
        }

        foreach (StatusLampViewModel lamp in Mechanism.Append(WheelLamp).Append(HeadstockForwardLamp).Append(HeadstockReverseLamp))
        {
            lamp.Update(snapshot);
        }

        SyncDiffText = Format(snapshot.GetNumberOrNull(MachineTagKeys.CarriageSyncDiffMm), "F3");
        UpdateMeasurement(snapshot);
        UpdateDrives(snapshot);

        this.actionKeys.Refresh(Block);
        foreach ((FunctionKeyViewModel key, string[] tags) in this.tagGatedKeys)
        {
            string? missing = this.grinding.FirstUnmapped(tags);
            Block(key, missing is null ? null : Localizer.Format("Key_MissingTagFormat", missing));
        }
    }

    /// <summary>位置图上点了一处：填进"拖板到 Z"的目标（不写机床，不问）。</summary>
    public void PickPositionTarget(double z)
    {
        PositionTarget.Accept(Math.Round(Math.Clamp(z, PositionTarget.Minimum, PositionTarget.Maximum), 1));
        Say("MG_TargetPickedFormat", PositionTarget.Text);
    }

    private void UpdateMeasurement(MachineStateSnapshot snapshot)
    {
        double? diameter = snapshot.GetNumberOrNull(MachineTagKeys.MeasuredDiameterMm);
        if (diameter is { } d && (this.lastDiameter is null || Math.Abs(d - this.lastDiameter.Value) > 1e-6))
        {
            this.previousDiameter = this.lastDiameter;
            this.lastDiameter = d;
        }

        MeasuredDiameterText = Format(diameter, "F3");
        DiameterStepText = this.lastDiameter is { } last && this.previousDiameter is { } previous
            ? Format(last - previous, "F3", showSign: true)
            : "--";

        double? probeA = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeAMm);
        double? probeB = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeBMm);
        ProbeAText = Format(probeA, "F4", showSign: true);
        ProbeBText = Format(probeB, "F4", showSign: true);
        MountingErrorText = probeA is null || probeB is null ? "--" : Format((probeA.Value - probeB.Value) / 2.0, "F4", showSign: true);

        ArmStateText = Localizer.Format(
            "MG_ArmStateFormat",
            ArmText(snapshot, MachineStatusCatalog.OuterArm),
            ArmText(snapshot, MachineStatusCatalog.InnerArm));
    }

    private string ArmText(MachineStateSnapshot snapshot, StatusIndicator indicator) => indicator.Read(snapshot) switch
    {
        IndicatorState.On => Localizer["MG_ArmLowered"],
        IndicatorState.Off => Localizer["MG_ArmRaised"],
        _ => "--",
    };

    private void UpdateDrives(MachineStateSnapshot snapshot)
    {
        double? wheelRpm = snapshot.GetNumberOrNull(MachineTagKeys.WheelSpeedRpm);
        double? wheelDiameter = snapshot.GetNumberOrNull(MachineTagKeys.WheelDiameterMm);
        WheelSpeedText = Format(wheelRpm, "F0");
        double? surface = wheelRpm is { } n && wheelDiameter is { } dia ? Math.PI * dia * n / 60000.0 : null;
        WheelSurfaceText = surface is null
            ? "--"
            : Localizer.Format("MG_WheelSurfaceFormat", Format(surface, "F1"), Format(wheelDiameter, "F1"));
        if (WheelSetpoint.Committed is null && surface is { } v && v >= WheelSetpoint.Minimum)
        {
            WheelSetpoint.Accept(Math.Round(v, 1));
        }

        double? current = snapshot.GetNumberOrNull(MachineTagKeys.GrindingCurrentA);
        double limit = Threshold("grindingCurrentLimitA") ?? 60.0;
        GrindingCurrentText = Format(current, "F1");
        GrindingCurrentFraction = current is { } a && limit > 0 ? Math.Clamp(a / limit, 0.0, 1.0) : 0.0;
        IsCurrentHigh = current is { } amps && amps > limit;

        AxisDescription? spindle = Axis(MachineAxisRoles.WorkpieceSpindle);
        double? headstockRpm = spindle is null ? null : snapshot.GetNumberOrNull(MachineTagKeys.AxisActualSpeedRpm(spindle.Name));
        HeadstockSpeedText = Format(headstockRpm, "F0");
        if (HeadstockSetpoint.Committed is null && headstockRpm is { } rpm)
        {
            HeadstockSetpoint.Accept(Math.Round(Math.Abs(rpm)));
        }

        WheelOverride.Observe(snapshot.GetNumberOrNull(MachineTagKeys.OverrideWheelPercent));
        HeadstockOverride.Observe(snapshot.GetNumberOrNull(MachineTagKeys.OverrideHeadstockPercent));
        FeedOverride.Observe(snapshot.GetNumberOrNull(MachineTagKeys.OverrideFeedPercent));

        IsCarriageRunning = snapshot.GetNumberOrNull(MachineTagKeys.ManualCarriageRunning) is { } running && running != 0;
        CarriageStateText = Localizer[IsCarriageRunning ? "MG_CarriageRunning" : "MG_CarriageStopped"];
        this.carriageKey.LabelResourceKey = IsCarriageRunning ? "MG_CarriageStop" : "MG_CarriageStart";
        this.carriageKey.IsActive = IsCarriageRunning;
        this.assistKey.IsActive = snapshot.GetNumberOrNull(MachineTagKeys.ManualHeadstockAssistState) is { } assist && assist != 0;

        AxisDescription? carriage = Axis(MachineAxisRoles.Carriage);
        CarriageZ = carriage is null ? null : snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(carriage.Name));
    }

    /// <summary>会马上影响机床的值改了：先标红（SetpointViewModel 已标），问一句，确认才写。</summary>
    private void OnMachineValueChanged(SetpointViewModel setpoint, double value)
    {
        string tag = setpoint == WheelSetpoint ? MachineTagKeys.ManualWheelSurfaceSpeedSetpoint
            : setpoint == HeadstockSetpoint ? MachineTagKeys.ManualHeadstockSpeedSetpoint
            : setpoint == WheelOverride ? MachineTagKeys.OverrideWheelPercent
            : setpoint == HeadstockOverride ? MachineTagKeys.OverrideHeadstockPercent
            : MachineTagKeys.OverrideFeedPercent;

        if (this.grinding.FirstUnmapped(tag) is { } missing)
        {
            setpoint.Revert();
            Interaction.Refuse(Localizer.Format("Key_MissingTagFormat", missing));
            return;
        }

        string from = setpoint.Committed is { } old ? setpoint.Format(old) : "--";
        Interaction.Ask(
            Localizer.Format("MG_AskValueFormat", setpoint.Label, from, setpoint.Format(value), setpoint.Unit),
            async () =>
            {
                ManualCommandResult result = await this.grinding.WriteValueAsync(tag, value, CancellationToken.None).ConfigureAwait(true);
                if (result.Succeeded)
                {
                    setpoint.Accept(value);
                    Say("MG_ValueWrittenFormat", setpoint.Label);
                }
                else
                {
                    setpoint.Revert();
                    Alarms.Raise(AlarmSeverity.Warning, result.ReasonResourceKey!, setpoint.Label);
                }
            },
            outcome =>
            {
                if (outcome != ConfirmationOutcome.Confirmed)
                {
                    setpoint.Revert();
                }
            });
    }

    /// <summary>不马上影响机床的值（往复速度、行程、定位目标）：改了就算，按"拖板往复…"时一起下发。</summary>
    private static void OnLocalValueChanged(SetpointViewModel setpoint, double value) => setpoint.Accept(value);

    /// <summary>拖板往复… ⇄ 拖板停止：启动要问，停止不问（最终稿 M3）。</summary>
    private async Task PressCarriageAsync()
    {
        if (IsCarriageRunning)
        {
            Report(await this.grinding.StopReciprocationAsync(CancellationToken.None).ConfigureAwait(true), "MG_CarriageStop");
            return;
        }

        if (TraverseSpeed.Committed is not { } speed || StrokeStart.Committed is not { } start || StrokeEnd.Committed is not { } end || end <= start)
        {
            Interaction.Refuse(Localizer["MG_StrokeInvalid"]);
            return;
        }

        Interaction.Ask(
            Localizer.Format("MG_AskCarriageFormat", StrokeStart.Format(start), StrokeEnd.Format(end), TraverseSpeed.Format(speed)),
            async () => Report(
                await this.grinding.StartReciprocationAsync(speed, start, end, CancellationToken.None).ConfigureAwait(true),
                "MG_CarriageStartName"));
    }

    /// <summary>带启动装置（最终稿 M5）：保持型开关，不问。</summary>
    private async Task ToggleAssistAsync()
    {
        bool on = !this.assistKey.IsActive;
        Report(await this.grinding.SetHeadstockAssistAsync(on, CancellationToken.None).ConfigureAwait(true), "MG_HeadstockAssist");
    }

    /// <summary>
    /// 定位 ▸（最终稿 5.1、M4）：拖板到 Z…、到辊身 ¼ / ½ / ¾…、测量架到轧辊…、测量架归位…、磨架退到安全位…。
    /// 每一项都要确认；目标是固定位置的（测量架、磨架）从 machine.json thresholds 取，没配就灰、写原因。
    /// </summary>
    private void OpenPositionMenu()
    {
        double body = BodyLengthMm;
        OpenVerticalMenu("MG_PositionTitle", new[]
        {
            PositionChoice("MG_ToZ", PositioningAxis.CarriageZ, PositionTarget.Committed, null),
            PositionChoice("MG_ToQuarter", PositioningAxis.CarriageZ, body > 0 ? BodyStart() + body * 0.25 : null, "MG_NeedsRoll"),
            PositionChoice("MG_ToHalf", PositioningAxis.CarriageZ, body > 0 ? BodyStart() + body * 0.5 : null, "MG_NeedsRoll"),
            PositionChoice("MG_ToThreeQuarter", PositioningAxis.CarriageZ, body > 0 ? BodyStart() + body * 0.75 : null, "MG_NeedsRoll"),
            PositionChoice("MG_ArmsToRoll", PositioningAxis.MeasuringCarriageX1, Threshold("positionX1AtRollMm"), "MG_NeedsConfigX1Roll"),
            PositionChoice("MG_ArmsHome", PositioningAxis.MeasuringCarriageX1, Threshold("positionX1HomeMm"), "MG_NeedsConfigX1Home"),
            PositionChoice("MG_InfeedSafe", PositioningAxis.InfeedX, Threshold("positionXSafeMm"), "MG_NeedsConfigXSafe"),
        });
    }

    private FunctionKeyViewModel PositionChoice(string labelKey, PositioningAxis axis, double? target, string? missingReasonKey)
    {
        var key = MenuChoice(
            labelKey,
            () =>
            {
                if (target is not { } z)
                {
                    return;
                }

                string where = z.ToString("F1", CultureInfo.InvariantCulture);
                Interaction.Ask(
                    Localizer.Format("MG_AskPositionFormat", Localizer[labelKey], where),
                    async () => Report(
                        await this.grinding.StartPositioningAsync(axis, z, PositioningFeed(axis), CancellationToken.None).ConfigureAwait(true),
                        labelKey));
            },
            requiresEditable: false);
        key.Blocker = target is null ? Localizer[missingReasonKey ?? "MG_NoTarget"] : null;
        return key;
    }

    /// <summary>辊身头架侧端面在机床 Z 上的位置：辊形与轨迹都以它为零点（最终稿 Q6），按惯例就是 Z 0。</summary>
    private static double BodyStart() => 0.0;

    private double PositioningFeed(PositioningAxis axis)
    {
        double configured = Threshold("positioningFeedMmPerMin") ?? DefaultPositioningFeed;
        AxisDescription? description = axis switch
        {
            PositioningAxis.CarriageZ => Axis(MachineAxisRoles.Carriage),
            PositioningAxis.MeasuringCarriageX1 => Axis(MachineAxisRoles.MeasuringCarriage),
            _ => Axis(MachineAxisRoles.InfeedRadius),
        };
        return description?.MaxFeedMmPerMin is { } max ? Math.Min(configured, max) : configured;
    }

    /// <summary>装着的辊（最近一份作业的辊身长度）：位置图、辊身 ¼ ½ ¾、往复默认行程都靠它。</summary>
    private async Task LoadRollAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GrindingRecord> recent = await this.records
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), 1, cancellationToken)
            .ConfigureAwait(true);
        GrindingJob? job = recent.Count == 0
            ? null
            : (await this.jobs.GetAsync(recent[0].JobId, cancellationToken).ConfigureAwait(true))?.Job;

        ContextItems.Clear();
        if (job is null)
        {
            BodyLengthMm = 0;
            ContextItems.Add(new ContextItem("MG_ContextNoRoll", string.Empty));
            return;
        }

        BodyLengthMm = job.Geometry.BodyLengthMm;
        ContextItems.Add(new ContextItem("Context_Roll", job.RollId, IsMonospaced: true));
        ContextItems.Add(new ContextItem("Context_BodyLength", job.Geometry.BodyLengthMm.ToString("F0", CultureInfo.InvariantCulture), IsMonospaced: true));

        // 往复默认行程：辊身两端各留 manualStrokeMarginMm（最终稿 M8，默认 60）。
        if (StrokeStart.Committed is null || StrokeEnd.Committed is null)
        {
            double margin = this.machine.ManualStrokeMarginMm ?? MachineDescription.DefaultManualStrokeMarginMm;
            StrokeStart.Accept(BodyStart() + margin);
            StrokeEnd.Accept(BodyStart() + Math.Max(margin, BodyLengthMm - margin));
        }
    }

    private void Report(ManualCommandResult result, string nameKey)
    {
        if (result.Succeeded)
        {
            Say("Manual_CommandSentFormat", Localizer[nameKey]);
        }
        else if (result.Outcome == ManualCommandOutcome.NotMapped)
        {
            Interaction.Refuse(Localizer[result.ReasonResourceKey!]);
        }
        else
        {
            Alarms.Raise(AlarmSeverity.Warning, result.ReasonResourceKey!, Localizer[nameKey]);
        }
    }

    private void Gate(FunctionKeyViewModel key, params string[] tags) => this.tagGatedKeys.Add((key, tags));

    private AxisDescription? Axis(string role) =>
        this.machine.Axes.FirstOrDefault(axis => axis.IsPresent && string.Equals(axis.Role, role, StringComparison.Ordinal));

    private double? Threshold(string key) => this.machine.Thresholds.TryGetValue(key, out double value) ? value : null;

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
