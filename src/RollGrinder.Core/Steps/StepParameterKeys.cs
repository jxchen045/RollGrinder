namespace RollGrinder.Core.Steps;

/// <summary>
/// 内置工序共用的参数键。界面量一律"直径量 + 微米"（架构约束 ⑨），
/// 名字里带单位（架构约束 ⑧）——设计稿上写 0.010 mm 的地方，这里是 10 µm，是同一个量。
/// </summary>
public static class StepParameterKeys
{
    /// <summary>本工序的目标去除量（直径量 µm）。两种进给方式下都是终止条件。</summary>
    public const string StockDiameterMicrometer = "stockDiameterMicrometer";

    /// <summary>
    /// 周期进给分量：每道次换向时进的量（直径量 µm）。置 0 表示不用这一路。
    /// 与连续进给**可以同时非零**，两者相加才是这道工序的实际切入。
    /// </summary>
    public const string InfeedPerPassDiameterMicrometer = "infeedPerPassDiameterMicrometer";

    /// <summary>
    /// 连续进给分量：持续切入的速率（直径量 µm/min）。置 0 表示不用这一路。
    /// </summary>
    public const string ContinuousInfeedDiameterMicrometerPerMin = "continuousInfeedDiameterMicrometerPerMin";

    /// <summary>拖板速度（轴向进给，mm/min）。</summary>
    public const string FeedMmPerMin = "feedMmPerMin";

    /// <summary>头架转速（工件转速，r/min）。</summary>
    public const string WorkpieceSpeedRpm = "workpieceSpeedRpm";

    /// <summary>砂轮转速（r/min）。留给不做恒线速控制的机床；为 0 表示由 NC 按线速度换算。</summary>
    public const string WheelSpeedRpm = "wheelSpeedRpm";

    /// <summary>砂轮线速度（m/s）。工艺人员按这个想问题，rpm 由 NC 按当前砂轮直径换算。</summary>
    public const string WheelSurfaceSpeedMPerSec = "wheelSurfaceSpeedMPerSec";

    /// <summary>折返时间：换向点的停顿（s）。周期进给就在这个停顿里完成。</summary>
    public const string ReversalDwellSeconds = "reversalDwellSeconds";

    /// <summary>光磨道次（不进给的往复次数）。</summary>
    public const string SparkOutPassCount = "sparkOutPassCount";

    /// <summary>磨削道次。一道 = 一个往复。</summary>
    public const string PassCount = "passCount";

    /// <summary>在线测量：本工序进行中是否允许测量臂跟测。</summary>
    public const string InProcessMeasurement = "inProcessMeasurement";

    /// <summary>变速模式：关闭 / 轧辊 / 砂轮 / 两者。</summary>
    public const string SpeedVariationTarget = "speedVariationTarget";

    /// <summary>变速幅度（±%）。</summary>
    public const string SpeedVariationPercent = "speedVariationPercent";

    /// <summary>变速周期（s）。</summary>
    public const string SpeedVariationPeriodSeconds = "speedVariationPeriodSeconds";

    /// <summary>沿辊身的测点数（测量工序）。</summary>
    public const string MeasurePointCount = "measurePointCount";

    /// <summary>修整进给：每道修整的切深（半径量 µm，砂轮半径）。</summary>
    public const string DressInfeedRadiusMicrometer = "dressInfeedRadiusMicrometer";

    /// <summary>修整道次。</summary>
    public const string DressPassCount = "dressPassCount";

    /// <summary>修整走刀速度（mm/min）。</summary>
    public const string DressFeedMmPerMin = "dressFeedMmPerMin";

    /// <summary>倒角宽度（mm，沿辊身方向）。</summary>
    public const string ChamferWidthMm = "chamferWidthMm";

    /// <summary>倒角角度（°）。</summary>
    public const string ChamferAngleDegree = "chamferAngleDegree";

    /// <summary>探伤扫查螺距（mm/转）。</summary>
    public const string ScanPitchMm = "scanPitchMm";
}

/// <summary>变速模式参数的选项键。</summary>
public static class SpeedVariationChoices
{
    public const string Off = "off";
    public const string Workpiece = "workpiece";
    public const string Wheel = "wheel";
    public const string Both = "both";
}
