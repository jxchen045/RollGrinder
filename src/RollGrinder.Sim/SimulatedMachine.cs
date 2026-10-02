using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Units;

namespace RollGrinder.Sim;

/// <summary>
/// 一台"会动"的假机床：按下发的参数模拟走刀、去除量与测量值。
/// 只用于无机床环境下的调试与自动测试，时间由调用方推进，结果可复现。
/// 这里的系数是仿真保真度参数，不是机床配置——机床差异一律来自 machine.json。
/// </summary>
public sealed class SimulatedMachine
{
    /// <summary>程序启动时假定的待磨余量（半径量 mm）。</summary>
    public const double InitialStockRadiusMm = RollSurfaceModel.InitialStockRadiusMm;

    /// <summary>磨削时的去除速度（半径量 mm/min）。</summary>
    public const double RemovalRateMmPerMinute = 0.06;

    /// <summary>测量值的模拟波动幅度（半径量 mm）。</summary>
    public const double MeasurementNoiseRadiusMm = 0.0005;

    /// <summary>没有下发作业时每道工序假定的走刀次数。</summary>
    public const int FallbackPassCount = 10;

    /// <summary>
    /// 拖板不走的工序（开始、结束、暂停……）在仿真里停留多久（仿真秒）。
    /// 真 NC 上这些工序也要花时间（换参数、等确认），不会是 0。
    /// </summary>
    public const double NonTraverseStepSeconds = 5.0;

    /// <summary>下发进来的每道工序拖板进给（mm/min），按工序下标（从 0 起）。</summary>
    private readonly Dictionary<int, double> stepFeeds = new();

    /// <summary>拖板不走的工序已经停了多久（仿真秒）。</summary>
    private double stepDwellSeconds;

    private readonly MachineDescription machine;
    private readonly Dictionary<string, TagValue> writtenValues = new(StringComparer.Ordinal);

    /// <summary>下发进来的每道工序走刀次数，按工序下标（从 0 起）。</summary>
    private readonly Dictionary<int, int> stepPassCounts = new();

    /// <summary>下发进来的每道工序光磨道数（不进刀、照样走拖板），按工序下标。</summary>
    private readonly Dictionary<int, int> stepSparkOutCounts = new();

    /// <summary>下发进来的每道工序有没有进给（每刀进给或连续进给大于 0）。测量、暂停这类工序没有，不去除材料。</summary>
    private readonly Dictionary<int, bool> stepCuts = new();

    /// <summary>
    /// 循环正常结束位（job.cycleComplete）。程序开始清 0，最后一道走完置 1；
    /// 半路被中止（参数失效）不置——上位机靠它区分"磨完了"与"被复位了"。
    /// </summary>
    private bool cycleComplete;

    /// <summary>下发进来的辊形点列，按下标收，收齐一对就同步进辊面模型。</summary>
    private readonly Dictionary<int, double> profilePositionsMm = new();
    private readonly Dictionary<int, double> profileOffsetsMm = new();
    private readonly string? carriageAxisName;
    private readonly string? infeedAxisName;
    private readonly string? workpieceSpindleName;
    private readonly string? wheelSpindleName;

    private double targetRadiusMm;
    private double currentRadiusMm;
    private double bodyLengthMm;
    private double feedMmPerMin;
    private double carriagePositionMm;
    private int carriageDirection = 1;
    private double elapsedSeconds;
    private int currentStepOrder = 1;
    private int currentPass;
    private int stepCount;
    private int strokeVersion;
    private readonly double wheelDiameterMm = 890.24;

    // ── 手动磨削（界面最终稿 5.1）：拖板往复、定位、方式请求。只在通道空闲时动，和真 PLC 的联锁一致。──

    /// <summary>拖板在手动往复。</summary>
    private bool manualCarriageRunning;

    /// <summary>定位循环状态：0 空闲 / 1 运行 / 2 完成 / 3 出错（与 manual.position.state 同义）。</summary>
    private int positionState;

    private int positionAxis;
    private double positionTargetMm;
    private double positionSpeedMmPerMin;

    /// <summary>测量架 X1 的位置（仿真里只有定位会动它）。</summary>
    private double measuringCarriageMm;

    /// <summary>方式选择：没有程序挂着时报这个（0 JOG / 2 AUTO）；上位机的方式请求改它。</summary>
    private int requestedMode;

    /// <summary>
    /// 辊面模型：测径仪读到什么、电流多大，都从它来。
    /// 这是让"无机床也能验收"说得过去的关键——辊面真的带着误差，磨削真的把它磨掉，
    /// 机床真的留下一份可重复的系统性偏差，补偿收敛才有东西可验。
    /// </summary>
    private RollSurfaceModel surface;

    public SimulatedMachine(MachineDescription machine)
    {
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));

        this.carriageAxisName = AxisName(MachineAxisRoles.Carriage);
        this.infeedAxisName = AxisName(MachineAxisRoles.InfeedRadius);
        this.workpieceSpindleName = AxisName(MachineAxisRoles.WorkpieceSpindle);
        this.wheelSpindleName = AxisName(MachineAxisRoles.WheelSpindle);

        this.targetRadiusMm = UnitConversion.DiameterMmToRadiusMm(machine.Workpiece.MinDiameterMm);
        this.currentRadiusMm = this.targetRadiusMm;
        this.bodyLengthMm = machine.Workpiece.MinBodyLengthMm;
        this.feedMmPerMin = 1000.0;
        this.surface = new RollSurfaceModel(this.bodyLengthMm, this.targetRadiusMm);
    }

    /// <summary>当前这支辊的辊面，测试里直接拿它断言。</summary>
    public RollSurfaceModel Surface => this.surface;

    /// <summary>当前通道状态。</summary>
    public NcChannelState ChannelState { get; private set; } = NcChannelState.Reset;

    /// <summary>当前辊件半径（mm）。</summary>
    public double CurrentRadiusMm => this.currentRadiusMm;

    /// <summary>还剩多少余量（半径量 mm）。</summary>
    public double RemainingStockRadiusMm => Math.Max(0.0, this.currentRadiusMm - this.targetRadiusMm);

    /// <summary>当前程序名。</summary>
    public string ProgramName { get; private set; } = string.Empty;

    /// <summary>状态回读变量的后缀，与 <see cref="MachineTagKeys.ManualCommandState"/> 保持一致。</summary>
    private const string ManualStateSuffix = ".state";

    /// <summary>
    /// 脉冲型手动动作落到哪个机构到位状态（Q7 的状态位）：仿真机床收到"套筒伸出"就把"套筒伸出到位"置上，
    /// 手动页与状态带的灯才有东西可读。一个动作可以动几个机构（"双臂到轧辊"）。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Status, bool Value)[]> PulseEffects =
        new Dictionary<string, (string, bool)[]>(StringComparer.Ordinal)
        {
            ["outerArm.lower"] = new[] { ("outerArm.lowered", true) },
            ["outerArm.raise"] = new[] { ("outerArm.lowered", false) },
            ["innerArm.lower"] = new[] { ("innerArm.lowered", true) },
            ["innerArm.raise"] = new[] { ("innerArm.lowered", false) },
            ["arms.toRoll"] = new[] { ("outerArm.lowered", true), ("innerArm.lowered", true) },
            ["arms.home"] = new[] { ("outerArm.lowered", false), ("innerArm.lowered", false) },
            ["quill.extend"] = new[] { ("quill.extended", true) },
            ["quill.retract"] = new[] { ("quill.extended", false) },
            ["tailstock.forward"] = new[] { ("tailstock.forward", true) },
            ["tailstock.backward"] = new[] { ("tailstock.forward", false) },
            ["driver.extend"] = new[] { ("driver.extended", true) },
            ["driver.retract"] = new[] { ("driver.extended", false) },
            ["softLanding.headstock.up"] = new[] { ("softLanding.headstock.raised", true) },
            ["softLanding.headstock.down"] = new[] { ("softLanding.headstock.raised", false) },
            ["softLanding.tailstock.up"] = new[] { ("softLanding.tailstock.raised", true) },
            ["softLanding.tailstock.down"] = new[] { ("softLanding.tailstock.raised", false) },
        };

    /// <summary>
    /// 机构到位状态。开机时辊子已装好：套筒伸出、尾架前进、拨盘伸出，测量臂收起、托瓦落下；
    /// 机床已上电、已回参考点、没有急停（界面最终稿标题行 / 通道行读这几位）。
    /// </summary>
    private readonly Dictionary<string, bool> statuses = new(StringComparer.Ordinal)
    {
        ["emergencyStop"] = false,
        ["machineOn"] = true,
        ["referenced"] = true,
        ["outerArm.lowered"] = false,
        ["innerArm.lowered"] = false,
        ["quill.extended"] = true,
        ["tailstock.forward"] = true,
        ["driver.extended"] = true,
        ["softLanding.headstock.raised"] = false,
        ["softLanding.tailstock.raised"] = false,
    };

    /// <summary>接受一次写入。参数有效标志置真即开始模拟磨削。</summary>
    public void Write(string logicalName, TagValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentNullException.ThrowIfNull(value);

        this.writtenValues[logicalName] = value;

        if (HandleManualGrindingWrite(logicalName, value))
        {
            return;
        }

        // 手动动作：把命令位原样回显到状态位，界面上的"冷却水开着"这类指示才有东西可读。
        // 仿真机床就是这台"机床"，所以这是真回读，不是假数据。
        if (logicalName.StartsWith(MachineTagKeys.ManualCommandPrefix, StringComparison.Ordinal)
            && !logicalName.EndsWith(ManualStateSuffix, StringComparison.Ordinal)
            && value.Raw is bool)
        {
            string stateName = logicalName + ManualStateSuffix;
            this.writtenValues[stateName] = value with { Key = stateName };
            if (value.Raw is true
                && PulseEffects.TryGetValue(logicalName[MachineTagKeys.ManualCommandPrefix.Length..], out (string Status, bool Value)[]? effects))
            {
                foreach ((string status, bool on) in effects)
                {
                    this.statuses[status] = on;
                }
            }

            return;
        }

        // 辊形点列也是数组变量：记下来，磨出来的辊面才是"照着指令辊形磨的"，
        // 而不是一根光溜溜的圆柱。补偿叠进来的那一份也在这里面。
        if (TagKeySyntax.TrySplit(logicalName, out string profileKey, out int profileIndex))
        {
            if (string.Equals(profileKey, MachineTagKeys.JobProfileBodyPositionMm, StringComparison.Ordinal))
            {
                this.profilePositionsMm[profileIndex] = ToDouble(value.Raw) ?? 0.0;
                SyncProfilePoint(profileIndex);
                return;
            }

            if (string.Equals(profileKey, MachineTagKeys.JobProfileRadiusOffsetMm, StringComparison.Ordinal))
            {
                this.profileOffsetsMm[profileIndex] = ToDouble(value.Raw) ?? 0.0;
                SyncProfilePoint(profileIndex);
                return;
            }
        }

        // 工序走刀次数是数组变量，下发时一条一条写进来：记下来，仿真才知道每道磨几刀。
        // 测量这类工序下发的是 0 刀：按扫一个来回算，而不是退回默认刀数。
        if (TagKeySyntax.TrySplit(logicalName, out string baseKey, out int index))
        {
            if (string.Equals(baseKey, MachineTagKeys.JobStepPassCount, StringComparison.Ordinal))
            {
                if ((int?)ToDouble(value.Raw) is int passCount)
                {
                    this.stepPassCounts[index] = Math.Max(1, passCount);
                }

                return;
            }

            if (string.Equals(baseKey, MachineTagKeys.JobStepSparkOutPassCount, StringComparison.Ordinal))
            {
                this.stepSparkOutCounts[index] = Math.Max(0, (int?)ToDouble(value.Raw) ?? 0);
                return;
            }

            if (string.Equals(baseKey, MachineTagKeys.JobStepFeedMmPerMin, StringComparison.Ordinal))
            {
                this.stepFeeds[index] = ToDouble(value.Raw) ?? 0.0;
                return;
            }

            if (string.Equals(baseKey, MachineTagKeys.JobStepInfeedPerPassRadiusMm, StringComparison.Ordinal)
                || string.Equals(baseKey, MachineTagKeys.JobStepContinuousInfeedRadiusMmPerMin, StringComparison.Ordinal))
            {
                bool cuts = (ToDouble(value.Raw) ?? 0.0) > 0.0;
                this.stepCuts[index] = cuts || (this.stepCuts.TryGetValue(index, out bool already) && already);
                return;
            }
        }

        switch (logicalName)
        {
            case MachineTagKeys.JobRollRadiusMm:
                this.targetRadiusMm = ToDouble(value.Raw) ?? this.targetRadiusMm;
                break;

            case MachineTagKeys.JobBodyLengthMm:
                this.bodyLengthMm = ToDouble(value.Raw) ?? this.bodyLengthMm;
                break;

            case MachineTagKeys.JobFeedMmPerMin:
                this.feedMmPerMin = ToDouble(value.Raw) ?? this.feedMmPerMin;
                break;

            case MachineTagKeys.JobStepCount:
                this.stepCount = (int)(ToDouble(value.Raw) ?? 0.0);
                break;

            case MachineTagKeys.JobParametersValid when value.Raw is bool valid:
                if (valid)
                {
                    StartProgram();
                }
                else
                {
                    ChannelState = NcChannelState.Reset;
                }

                break;

            default:
                break;
        }
    }

    /// <summary>推进仿真时间。</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        this.elapsedSeconds += delta.TotalSeconds;
        if (ChannelState != NcChannelState.Running)
        {
            AdvanceManual(delta);
            return;
        }

        double minutes = delta.TotalMinutes;

        // 拖板不走的工序：原地停一段时间再进下一道。
        // 以前按"进给 0"照样判端点，拖板停在 0 位，每一拍都算走完一刀——
        // 以"开始"打头的程序因此 3 秒就"磨完"了（第一轮甲方测试）。
        double feedMmPerMin = CurrentFeedMmPerMin;
        if (feedMmPerMin <= 0.0)
        {
            this.stepDwellSeconds += delta.TotalSeconds;
            if (this.stepDwellSeconds >= NonTraverseStepSeconds)
            {
                this.stepDwellSeconds = 0.0;
                this.currentPass = 0;
                AdvanceToNextStep();
            }

            return;
        }

        // 纵向拖板在辊身两端之间往复，速度按当前这道工序自己的进给。
        double travelMm = feedMmPerMin * minutes;
        this.carriagePositionMm += travelMm * this.carriageDirection;
        if (this.carriagePositionMm >= this.bodyLengthMm)
        {
            this.carriagePositionMm = this.bodyLengthMm;
            this.carriageDirection = -1;
            CompleteStroke();
        }
        else if (this.carriagePositionMm <= 0.0)
        {
            this.carriagePositionMm = 0.0;
            this.carriageDirection = 1;
            CompleteStroke();
        }

        // 去除量按恒定速率逼近目标半径，同一份量也从辊面上磨掉——
        // 磨到余量见底，辊面就落在「指令辊形 + 系统性偏差」上。
        // 只有带进给的工序才去除；余量见底后也不提前收工——和真 NC 一样把剩下的工序（测量、圆度……）走完。
        if (CurrentStepCuts && this.currentRadiusMm > this.targetRadiusMm)
        {
            double removalMm = Math.Min(RemovalRateMmPerMinute * minutes, this.currentRadiusMm - this.targetRadiusMm);
            this.currentRadiusMm -= removalMm;
            this.surface.Remove(removalMm);
        }
    }

    /// <summary>
    /// 当前工序的拖板进给。下发过逐道进给就用这一道的；没下发过（老式调用）用通用进给。
    /// </summary>
    private double CurrentFeedMmPerMin =>
        this.stepFeeds.TryGetValue(this.currentStepOrder - 1, out double feed) ? feed : this.feedMmPerMin;

    /// <summary>当前工序去不去除材料。没下发过进给信息的（老式调用）按会去除处理。</summary>
    private bool CurrentStepCuts =>
        !this.stepCuts.TryGetValue(this.currentStepOrder - 1, out bool cuts) || cuts;

    /// <summary>读取一个逻辑变量；仿真不认识的变量返回 null，由调用方决定怎么处理。</summary>
    public object? Read(string logicalName)
    {
        if (logicalName == MachineTagKeys.JobCycleComplete)
        {
            return this.cycleComplete ? 1 : 0;
        }

        if (logicalName == MachineTagKeys.ChannelState)
        {
            return (int)ChannelState;
        }

        // 倍率：仿真按 100 %。
        if (logicalName is MachineTagKeys.FeedOverridePercent or MachineTagKeys.SpindleOverridePercent or MachineTagKeys.WheelOverridePercent)
        {
            return 100.0;
        }

        // 程序段：运行时给一行像样的段文字（真机由 NC 的当前段变量来）。
        if (logicalName is MachineTagKeys.CurrentBlock or MachineTagKeys.NextBlock)
        {
            if (ChannelState == NcChannelState.Reset || string.IsNullOrEmpty(ProgramName))
            {
                return string.Empty;
            }

            bool next = logicalName == MachineTagKeys.NextBlock;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"N{(next ? 20 : 10)} G1 Z={this.carriagePositionMm + (next ? 50.0 : 0.0):F3} F=R10   ; {ProgramName}");
        }

        if (logicalName == MachineTagKeys.ProgramName)
        {
            return ProgramName;
        }

        // 挂着程序就是 AUTO；空闲时报方式选择（上位机的方式请求改它，真机上由 PLC 决定切不切）。
        if (logicalName == MachineTagKeys.OperatingMode)
        {
            return ChannelState == NcChannelState.Reset ? this.requestedMode : 2;
        }

        if (ReadManualGrinding(logicalName) is { } manual)
        {
            return manual;
        }

        if (logicalName.StartsWith(MachineTagKeys.StatusPrefix, StringComparison.Ordinal))
        {
            string status = logicalName[MachineTagKeys.StatusPrefix.Length..];

            // 中心架没有手动动作（托瓦待补），仿真里磨削时顶上、空闲时落下。
            if (status == "steadyRest.engaged")
            {
                return ChannelState != NcChannelState.Reset;
            }

            // 门、液压、润滑：仿真里一直正常。
            if (status is "door.closed" or "hydraulics.ok" or "lubrication.ok")
            {
                return true;
            }

            return this.statuses.TryGetValue(status, out bool on) ? on : null;
        }

        if (logicalName == MachineTagKeys.MeasuredDiameterMm)
        {
            // 按**拖板当前位置**读辊面：测量服务是逐点扫的（读位置 + 读直径），
            // 这里与位置无关的话，扫出来就是一根光溜溜的圆柱，误差曲线永远是平的。
            return UnitConversion.RadiusMmToDiameterMm(
                this.surface.MeanRadiusAtMm(this.carriagePositionMm) + Noise());
        }

        // 双测头：A、B 读数围绕实际半径偏差摆动，(A−B)/2 即对中偏差。
        if (logicalName == MachineTagKeys.MeasureProbeAMm)
        {
            return Noise() + (MeasurementNoiseRadiusMm * 0.6);
        }

        if (logicalName == MachineTagKeys.MeasureProbeBMm)
        {
            return Noise() - (MeasurementNoiseRadiusMm * 0.6);
        }

        // 圆度与偏心：真机上由测量系统按一转的读数算好后报上来，
        // 上位机不看原始 r(θ)。仿真按同一个契约给当前截面的结果。
        if (logicalName == MachineTagKeys.MeasureRoundnessMicrometer)
        {
            return this.surface.RoundnessMicrometerAtMm(this.carriagePositionMm);
        }

        if (logicalName == MachineTagKeys.MeasureEccentricityMicrometer)
        {
            return this.surface.EccentricityMicrometerAtMm(this.carriagePositionMm);
        }

        if (logicalName == MachineTagKeys.WheelDiameterMm)
        {
            return this.wheelDiameterMm;
        }

        if (logicalName == MachineTagKeys.WheelSpeedRpm)
        {
            return WheelRpm();
        }

        if (logicalName == MachineTagKeys.GrindingCurrentA)
        {
            return this.surface.GrindingCurrentA(
                RemovalRateMmPerMinute,
                this.carriagePositionMm,
                ChannelState == NcChannelState.Running);
        }

        if (logicalName == MachineTagKeys.JobCurrentStepOrder)
        {
            return this.currentStepOrder;
        }

        if (logicalName == MachineTagKeys.JobCurrentPass)
        {
            return this.currentPass;
        }

        if (logicalName == MachineTagKeys.JobTotalPasses)
        {
            return CurrentStepTotalPasses;
        }

        if (logicalName == MachineTagKeys.CompensationFeedForwardA)
        {
            return -0.006;
        }

        if (logicalName == MachineTagKeys.CompensationFeedForwardB)
        {
            return 2.1e-6;
        }

        if (logicalName == MachineTagKeys.CompensationStrokeVersion)
        {
            return this.strokeVersion;
        }

        if (logicalName == MachineTagKeys.CompensationRealtimeOffsetMm)
        {
            return ChannelState == NcChannelState.Running ? Noise() * 6.0 : 0.0;
        }

        if (this.carriageAxisName is not null && logicalName == MachineTagKeys.AxisActualPositionMm(this.carriageAxisName))
        {
            return this.carriagePositionMm;
        }

        if (this.infeedAxisName is not null && logicalName == MachineTagKeys.AxisActualPositionMm(this.infeedAxisName))
        {
            return this.currentRadiusMm;
        }

        if (this.workpieceSpindleName is not null && logicalName == MachineTagKeys.AxisActualSpeedRpm(this.workpieceSpindleName))
        {
            return ChannelState == NcChannelState.Running ? 20.0 : 0.0;
        }

        if (this.wheelSpindleName is not null && logicalName == MachineTagKeys.AxisActualSpeedRpm(this.wheelSpindleName))
        {
            return WheelRpm();
        }

        string? measuringCarriage = AxisName(MachineAxisRoles.MeasuringCarriage);
        if (measuringCarriage is not null && logicalName == MachineTagKeys.AxisActualPositionMm(measuringCarriage))
        {
            return this.measuringCarriageMm;
        }

        return this.writtenValues.TryGetValue(logicalName, out TagValue? written) ? written.Raw : null;
    }

    /// <summary>
    /// 当前工序要走几刀（进刀道 + 光磨道）。下发过就用下发的值，没下发过按 <see cref="FallbackPassCount"/> 走，
    /// 免得仿真在没有作业时原地不动。
    /// </summary>
    private int CurrentStepTotalPasses =>
        (this.stepPassCounts.TryGetValue(this.currentStepOrder - 1, out int passCount) && passCount > 0
            ? passCount
            : FallbackPassCount)
        + (this.stepSparkOutCounts.TryGetValue(this.currentStepOrder - 1, out int sparkOut) ? sparkOut : 0);

    /// <summary>
    /// 手动磨削的写入：往复的速度 / 行程 / 启停、定位的轴 / 目标 / 速度 / 启动、方式请求。
    /// 启停是脉冲，按上升沿动作（真 PLC 也是这样，命令位随后自复位）。处理了返回 true。
    /// </summary>
    private bool HandleManualGrindingWrite(string logicalName, TagValue value)
    {
        bool rising = value.Raw is true;
        switch (logicalName)
        {
            case MachineTagKeys.ManualCarriageStart:
                if (rising && ChannelState == NcChannelState.Reset
                    && Written(MachineTagKeys.ManualCarriageStrokeEnd) > Written(MachineTagKeys.ManualCarriageStrokeStart))
                {
                    this.manualCarriageRunning = true;
                    this.positionState = 0;
                    this.carriageDirection = 1;
                }

                return true;

            case MachineTagKeys.ManualCarriageStop:
                if (rising)
                {
                    this.manualCarriageRunning = false;
                }

                return true;

            case MachineTagKeys.ManualPositionStart:
                if (rising && ChannelState == NcChannelState.Reset)
                {
                    this.positionAxis = (int)Written(MachineTagKeys.ManualPositionAxis);
                    this.positionTargetMm = Written(MachineTagKeys.ManualPositionTarget);
                    this.positionSpeedMmPerMin = Written(MachineTagKeys.ManualPositionSpeed);
                    this.manualCarriageRunning = false;
                    this.positionState = this.positionAxis is >= 1 and <= 3 && this.positionSpeedMmPerMin > 0 ? 1 : 3;
                }

                return true;

            case MachineTagKeys.ModeRequest:
                // 上位机的请求值：1 JOG / 2 AUTO；方式码：0 JOG / 2 AUTO。
                this.requestedMode = (int)(ToDouble(value.Raw) ?? 1.0) == 2 ? 2 : 0;
                return true;

            default:
                return false;
        }
    }

    /// <summary>通道空闲时推进手动动作：往复在两个行程端点之间来回，定位走到目标就停。</summary>
    private void AdvanceManual(TimeSpan delta)
    {
        double minutes = delta.TotalMinutes;
        if (this.manualCarriageRunning)
        {
            double from = Written(MachineTagKeys.ManualCarriageStrokeStart);
            double to = Written(MachineTagKeys.ManualCarriageStrokeEnd);
            double speed = Written(MachineTagKeys.ManualCarriageSpeed) * Override(MachineTagKeys.OverrideFeedPercent);
            this.carriagePositionMm += speed * minutes * this.carriageDirection;
            if (this.carriagePositionMm >= to)
            {
                this.carriagePositionMm = to;
                this.carriageDirection = -1;
            }
            else if (this.carriagePositionMm <= from)
            {
                this.carriagePositionMm = from;
                this.carriageDirection = 1;
            }
        }

        if (this.positionState != 1)
        {
            return;
        }

        double step = this.positionSpeedMmPerMin * Override(MachineTagKeys.OverrideFeedPercent) * minutes;
        double MoveTowards(double current)
        {
            double remaining = this.positionTargetMm - current;
            if (Math.Abs(remaining) <= step)
            {
                this.positionState = 2;
                return this.positionTargetMm;
            }

            return current + (Math.Sign(remaining) * step);
        }

        switch (this.positionAxis)
        {
            case 1:
                this.carriagePositionMm = MoveTowards(this.carriagePositionMm);
                break;
            case 2:
                this.measuringCarriageMm = MoveTowards(this.measuringCarriageMm);
                break;
            case 3:
                // 磨架（X）退到安全位：仿真不改辊子半径，只报"到了"。
                this.positionState = 2;
                break;
        }
    }

    /// <summary>手动磨削相关的读：安全链、手持盒、倍率、往复与定位状态、Z1/Z2 同步差。不归这里管的返回 null。</summary>
    private object? ReadManualGrinding(string logicalName) => logicalName switch
    {
        MachineTagKeys.FaultLevel => 0,
        MachineTagKeys.PendantAxisSelect => 3,
        MachineTagKeys.PendantHandwheelFactor => 10,
        MachineTagKeys.PendantEnable => false,
        MachineTagKeys.CarriageSyncDiffMm => Noise() * 2.0,
        MachineTagKeys.OverrideFeedPercent or MachineTagKeys.OverrideWheelPercent or MachineTagKeys.OverrideHeadstockPercent
            => this.writtenValues.TryGetValue(logicalName, out TagValue? written) ? written.Raw : 100,
        MachineTagKeys.ManualCarriageRunning => this.manualCarriageRunning,
        MachineTagKeys.ManualPositionState => this.positionState,
        MachineTagKeys.PanelCycleStart => false,
        _ => null,
    };

    /// <summary>
    /// 砂轮转速：自动循环里按固定值；空闲时砂轮开着（手动"砂轮启动"）就按给定线速度 × 倍率反算转速。
    /// </summary>
    private double WheelRpm()
    {
        if (ChannelState == NcChannelState.Running)
        {
            return 590.0;
        }

        bool on = this.writtenValues.TryGetValue(MachineTagKeys.ManualCommandState("wheel.run"), out TagValue? state) && state.Raw is true;
        if (!on)
        {
            return 0.0;
        }

        double surfaceMPerSec = this.writtenValues.ContainsKey(MachineTagKeys.ManualWheelSurfaceSpeedSetpoint)
            ? Written(MachineTagKeys.ManualWheelSurfaceSpeedSetpoint)
            : 35.0;
        return surfaceMPerSec * Override(MachineTagKeys.OverrideWheelPercent) * 60000.0 / (Math.PI * this.wheelDiameterMm);
    }

    /// <summary>写进来的数值（没写过为 0）。</summary>
    private double Written(string logicalName) =>
        this.writtenValues.TryGetValue(logicalName, out TagValue? value) ? ToDouble(value.Raw) ?? 0.0 : 0.0;

    /// <summary>倍率（0–1）；没写过按 100 %。</summary>
    private double Override(string logicalName) =>
        this.writtenValues.ContainsKey(logicalName) ? Math.Clamp(Written(logicalName) / 100.0, 0.0, 1.5) : 1.0;

    private void CompleteStroke()
    {
        // 一个来回算一道；走完本工序的道次就推进到下一道工序。
        this.currentPass++;
        this.strokeVersion++;
        if (this.currentPass < CurrentStepTotalPasses)
        {
            return;
        }

        this.currentPass = 0;
        AdvanceToNextStep();
    }

    private void AdvanceToNextStep()
    {
        this.currentStepOrder++;
        this.stepDwellSeconds = 0.0;

        // 最后一道工序走完，程序结束——和真机一样，上位机不需要参与。
        // 结束前置"循环正常结束"位，对应 NC 程序里 M30 之前那一句 R124=1。
        if (this.stepCount > 0 && this.currentStepOrder > this.stepCount)
        {
            this.cycleComplete = true;
            ChannelState = NcChannelState.Reset;
            ProgramName = string.Empty;
        }
    }

    /// <summary>收齐一个下标上的位置与偏差之后，同步进辊面模型。</summary>
    private void SyncProfilePoint(int index)
    {
        if (this.profilePositionsMm.TryGetValue(index, out double positionMm)
            && this.profileOffsetsMm.TryGetValue(index, out double offsetMm))
        {
            this.surface.SetCommandedPoint(index, positionMm, offsetMm);
        }
    }

    private void StartProgram()
    {
        // 对应 NC 程序开头那一句 R124=0。
        this.cycleComplete = false;

        // 新一支辊：辊面按当前几何重建，余量与来料误差回到进来时的样子。
        this.surface = new RollSurfaceModel(this.bodyLengthMm, this.targetRadiusMm);
        foreach (int index in this.profilePositionsMm.Keys)
        {
            SyncProfilePoint(index);
        }

        this.currentRadiusMm = this.targetRadiusMm + InitialStockRadiusMm;
        this.carriagePositionMm = 0.0;
        this.carriageDirection = 1;
        this.currentStepOrder = 1;
        this.currentPass = 0;
        this.stepDwellSeconds = 0.0;
        this.strokeVersion = 0;
        ChannelState = NcChannelState.Running;
        ProgramName = string.Create(
            CultureInfo.InvariantCulture,
            $"SIM_{this.machine.MachineId}.MPF");
    }

    private double Noise()
    {
        // 确定性的"噪声"：同样的时间序列给同样的结果，测试才可复现。
        return MeasurementNoiseRadiusMm * Math.Sin(this.elapsedSeconds * 1.7);
    }

    private string? AxisName(string role) => this.machine.Axes
        .FirstOrDefault(axis => axis.IsPresent && string.Equals(axis.Role, role, StringComparison.Ordinal))?.Name;

    private static double? ToDouble(object? raw) => raw switch
    {
        double number => number,
        int integer => integer,
        IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
        _ => null,
    };
}
