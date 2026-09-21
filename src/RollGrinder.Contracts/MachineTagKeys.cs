using System;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Contracts;

/// <summary>
/// 逻辑变量名的构成约定。物理地址一律由 tagmap.json 决定，这里只定义逻辑名怎么拼，
/// 轴名来自 machine.json，不在代码里写死。
/// </summary>
public static class MachineTagKeys
{
    /// <summary>NC 通道状态。</summary>
    public const string ChannelState = "machine.channelState";

    /// <summary>当前 NC 程序名。</summary>
    public const string ProgramName = "machine.programName";

    /// <summary>已下发参数是否有效（上位机写、NC 读）。</summary>
    public const string JobParametersValid = "job.parametersValid";

    /// <summary>辊件公称半径（mm）。</summary>
    public const string JobRollRadiusMm = "job.rollRadiusMm";

    /// <summary>辊身长度（mm）。</summary>
    public const string JobBodyLengthMm = "job.bodyLengthMm";

    /// <summary>轴向进给（mm/min）。</summary>
    public const string JobFeedMmPerMin = "job.feedMmPerMin";

    /// <summary>测量得到的直径（mm）。</summary>
    public const string MeasuredDiameterMm = "measure.diameterMm";

    /// <summary>程序步骤开关的逻辑名前缀。</summary>
    public const string JobOptionPrefix = "job.option.";

    /// <summary>某个程序步骤开关的逻辑名。</summary>
    public static string JobOption(string optionKey) => JobOptionPrefix + optionKey;

    /// <summary>手动动作命令位的逻辑名前缀。</summary>
    public const string ManualCommandPrefix = "manual.";

    /// <summary>某个手动动作的命令位逻辑名。</summary>
    public static string ManualCommand(string actionKey) => ManualCommandPrefix + actionKey;

    /// <summary>某个保持型手动动作的状态回读逻辑名。</summary>
    public static string ManualCommandState(string actionKey) => ManualCommandPrefix + actionKey + ".state";

    /// <summary>
    /// 需要回读状态的保持型手动动作。动作目录在 RollGrinder.Services 里，
    /// 而 Contracts 不引用它，所以这几个键在这里单独列一次——
    /// 有测试盯着两边一致，加一个保持型动作而漏了这里，测试会红。
    ///
    /// 头架是正转/反转两位（原理图上没有"启动/停止"），两位都 false 才是停，
    /// 所以两边的状态都要回读，不能只看一位。
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string> ManualToggleStateKeys { get; } = new[]
    {
        ManualCommandState("coolant"),
        ManualCommandState("headstock.forward"),
        ManualCommandState("headstock.reverse"),
        ManualCommandState("wheel.run"),
    };

    /// <summary>本次作业的工序数。</summary>
    public const string JobStepCount = "job.stepCount";

    /// <summary>工序类型代码数组（取值由 machine.json 的 stepTypeCodes 决定）。</summary>
    public const string JobStepTypeCode = "job.step.typeCode";

    /// <summary>工序走刀次数数组。</summary>
    public const string JobStepPassCount = "job.step.passCount";

    /// <summary>工序每刀切深数组（半径量 mm）。</summary>
    public const string JobStepInfeedPerPassRadiusMm = "job.step.infeedPerPassRadiusMm";

    /// <summary>工序进给数组（mm/min）。</summary>
    public const string JobStepFeedMmPerMin = "job.step.feedMmPerMin";

    /// <summary>工序工件转速数组（r/min）。</summary>
    public const string JobStepWorkpieceSpeedRpm = "job.step.workpieceSpeedRpm";

    /// <summary>工序砂轮转速数组（r/min）。</summary>
    public const string JobStepWheelSpeedRpm = "job.step.wheelSpeedRpm";

    /// <summary>工序光磨次数数组。</summary>
    public const string JobStepSparkOutPassCount = "job.step.sparkOutPassCount";

    /// <summary>工序进给方式数组（0 不进给 / 1 连续 / 2 周期）。</summary>
    public const string JobStepFeedMode = "job.step.feedMode";

    /// <summary>工序连续进给速率数组（半径量 mm/min）。周期进给时为 0。</summary>
    public const string JobStepContinuousInfeedRadiusMmPerMin = "job.step.continuousInfeedRadiusMmPerMin";

    /// <summary>工序目标去除量数组（半径量 mm）。两种进给方式下都是终止条件。</summary>
    public const string JobStepTargetStockRadiusMm = "job.step.targetStockRadiusMm";

    /// <summary>工序砂轮线速度数组（m/s）。rpm 由 NC 按当前砂轮直径恒线速换算。</summary>
    public const string JobStepWheelSurfaceSpeedMPerSec = "job.step.wheelSurfaceSpeedMPerSec";

    /// <summary>工序换向停顿数组（s）。</summary>
    public const string JobStepReversalDwellSeconds = "job.step.reversalDwellSeconds";

    /// <summary>工序在线测量开关数组。</summary>
    public const string JobStepInProcessMeasurement = "job.step.inProcessMeasurement";

    /// <summary>工序变速作用对象数组（0 关 / 1 轧辊 / 2 砂轮 / 3 两者）。</summary>
    public const string JobStepSpeedVariationTarget = "job.step.speedVariationTarget";

    /// <summary>工序变速幅度数组（±%）。</summary>
    public const string JobStepSpeedVariationPercent = "job.step.speedVariationPercent";

    /// <summary>工序变速周期数组（s）。</summary>
    public const string JobStepSpeedVariationPeriodSeconds = "job.step.speedVariationPeriodSeconds";

    /// <summary>辊形曲线的点数。</summary>
    public const string JobProfilePointCount = "job.profile.pointCount";

    /// <summary>辊形曲线的辊身坐标数组（mm）。</summary>
    public const string JobProfileBodyPositionMm = "job.profile.bodyPositionMm";

    /// <summary>辊形曲线的半径偏差数组（mm）。</summary>
    public const string JobProfileRadiusOffsetMm = "job.profile.radiusOffsetMm";

    /// <summary>NC 正在执行第几道工序（从 1 开始）。</summary>
    public const string JobCurrentStepOrder = "job.currentStepOrder";

    /// <summary>跳转目标工序号（上位机写，NC 在收到跳转脉冲时读）。</summary>
    public const string JobControlTargetStepOrder = "job.control.targetStepOrder";

    /// <summary>跳到指定工序的命令位（脉冲，PLC 上升沿触发并自复位）。</summary>
    public const string JobControlJumpToStep = "job.control.jumpToStep";

    /// <summary>当前工序提前结束的命令位（脉冲，PLC 上升沿触发并自复位）。</summary>
    public const string JobControlEndStepEarly = "job.control.endStepEarly";

    /// <summary>
    /// 工序流程控制的命令位。跳转目标号是数值，不在这里。
    ///
    /// 这几位和手动动作一样是脉冲：上位机写 true → 等脉宽 → 写 false，
    /// PLC 侧按上升沿触发并自行复位——上位机中途被杀，NC 也不会卡在一个按住的按钮上。
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string> JobControlPulseKeys { get; } = new[]
    {
        JobControlJumpToStep,
        JobControlEndStepEarly,
    };

    /// <summary>当前工序的第几次走刀。</summary>
    public const string JobCurrentPass = "job.currentPass";

    /// <summary>当前工序的总走刀次数。</summary>
    public const string JobTotalPasses = "job.totalPasses";

    /// <summary>A 测头读数（mm）。</summary>
    public const string MeasureProbeAMm = "measure.probeAMm";

    /// <summary>B 测头读数（mm）。</summary>
    public const string MeasureProbeBMm = "measure.probeBMm";

    /// <summary>
    /// 当前截面的圆度（µm，峰谷值）。由测量系统按一转的读数自己算好，
    /// 上位机看不到原始的 r(θ)，只沿辊身把这个数收集成一条曲线。
    /// </summary>
    public const string MeasureRoundnessMicrometer = "measure.roundnessMicrometer";

    /// <summary>当前截面的偏心量（µm）。同样由测量系统给出。</summary>
    public const string MeasureEccentricityMicrometer = "measure.eccentricityMicrometer";

    /// <summary>砂轮直径（mm）。</summary>
    public const string WheelDiameterMm = "wheel.diameterMm";

    /// <summary>砂轮转速（r/min）。</summary>
    public const string WheelSpeedRpm = "wheel.speedRpm";

    /// <summary>磨削电流（A）。</summary>
    public const string GrindingCurrentA = "grinding.currentA";

    /// <summary>轴线前馈一次项 a。</summary>
    public const string CompensationFeedForwardA = "compensation.feedForwardA";

    /// <summary>轴线前馈二次项 b。</summary>
    public const string CompensationFeedForwardB = "compensation.feedForwardB";

    /// <summary>行程间补偿的版本号（每迭代一次加一）。</summary>
    public const string CompensationStrokeVersion = "compensation.strokeVersion";

    /// <summary>实时补偿量 $AA_OFF（mm）。</summary>
    public const string CompensationRealtimeOffsetMm = "compensation.realtimeOffsetMm";

    /// <summary>补偿降级级别：0 全功能（前馈+迭代+实时），1 前馈+迭代，2 仅前馈。</summary>
    public const string CompensationDegradationLevel = "compensation.degradationLevel";

    /// <summary>某根轴的实际位置（mm）。</summary>
    public static string AxisActualPositionMm(string axisName) =>
        Compose("axis", axisName, "actualPositionMm");

    /// <summary>某根轴的实际转速（r/min）。</summary>
    public static string AxisActualSpeedRpm(string axisName) =>
        Compose("axis", axisName, "actualSpeedRpm");

    private static string Compose(string prefix, string axisName, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axisName);
        return string.Concat(prefix, ".", axisName, ".", suffix);
    }

    /// <summary>按机床描述列出监控需要读取的逻辑名（缺哪一项由 tagmap 决定，读不到的会被跳过）。</summary>
    public static System.Collections.Generic.IReadOnlyList<string> MonitoringKeys(MachineDescription machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var keys = new System.Collections.Generic.List<string>
        {
            ChannelState,
            ProgramName,
            JobParametersValid,
            MeasuredDiameterMm,
            JobCurrentStepOrder,
            JobCurrentPass,
            JobTotalPasses,
            MeasureProbeAMm,
            MeasureProbeBMm,
            MeasureRoundnessMicrometer,
            MeasureEccentricityMicrometer,
            WheelDiameterMm,
            WheelSpeedRpm,
            GrindingCurrentA,
            CompensationFeedForwardA,
            CompensationFeedForwardB,
            CompensationStrokeVersion,
            CompensationRealtimeOffsetMm,
            CompensationDegradationLevel,
        };

        // 保持型手动动作的状态回读：界面要按它点亮"冷却水开着"这类指示。
        // 逻辑名在这里出现，物理地址在 tagmap.json 里——没映射就读不到，界面显示"--"。
        keys.AddRange(ManualToggleStateKeys);

        foreach (AxisDescription axis in machine.Axes)
        {
            if (!axis.IsPresent)
            {
                continue;
            }

            keys.Add(AxisActualPositionMm(axis.Name));
            keys.Add(AxisActualSpeedRpm(axis.Name));
        }

        return keys;
    }
}
