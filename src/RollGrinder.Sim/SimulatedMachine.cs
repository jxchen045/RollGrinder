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

    /// <summary>接受一次写入。参数有效标志置真即开始模拟磨削。</summary>
    public void Write(string logicalName, TagValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentNullException.ThrowIfNull(value);

        this.writtenValues[logicalName] = value;

        // 手动动作：把命令位原样回显到状态位，界面上的"冷却水开着"这类指示才有东西可读。
        // 仿真机床就是这台"机床"，所以这是真回读，不是假数据。
        if (logicalName.StartsWith(MachineTagKeys.ManualCommandPrefix, StringComparison.Ordinal)
            && !logicalName.EndsWith(ManualStateSuffix, StringComparison.Ordinal)
            && value.Raw is bool)
        {
            string stateName = logicalName + ManualStateSuffix;
            this.writtenValues[stateName] = value with { Key = stateName };
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

        if (logicalName == MachineTagKeys.ProgramName)
        {
            return ProgramName;
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
            return ChannelState == NcChannelState.Running ? 590.0 : 0.0;
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
            return ChannelState == NcChannelState.Running ? 900.0 : 0.0;
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
