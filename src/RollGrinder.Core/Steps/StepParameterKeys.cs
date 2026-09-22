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

    /// <summary>
    /// 变速周期（头架转数）。**不是秒**——实机上这一项的单位就是"次"。
    /// </summary>
    public const string SpeedVariationPeriodRevolutions = "speedVariationPeriodRevolutions";

    /// <summary>沿辊身的测点数（测量工序）。</summary>
    public const string MeasurePointCount = "measurePointCount";

    /// <summary>圆度测量：沿辊身取几个截面。</summary>
    public const string RoundnessSectionCount = "roundnessSectionCount";

    /// <summary>圆度测量：每个截面绕一圈取几点。36 点 = 每 10°一点，是现场惯例。</summary>
    public const string RoundnessPointsPerRevolution = "roundnessPointsPerRevolution";

    /// <summary>暂停工序的提示文案键，界面按它显示"为什么停在这里"。</summary>
    public const string PauseReason = "pauseReason";

    /// <summary>修整进给：每道修整的切深（半径量 µm，砂轮半径）。</summary>
    public const string DressInfeedRadiusMicrometer = "dressInfeedRadiusMicrometer";

    /// <summary>修整道次。</summary>
    public const string DressPassCount = "dressPassCount";

    /// <summary>修整走刀速度（mm/min）。</summary>
    public const string DressFeedMmPerMin = "dressFeedMmPerMin";

    /// <summary>
    /// 倒角第一段长度（mm，沿辊身方向）。
    ///
    /// 实机的倒角是**两段**加一个形状：长度1/高度1、长度2/高度2、类型。
    /// 这比"宽度 + 角度"能表达的多——两段可以不等，做出台阶式或先缓后陡的端部；
    /// 而"宽度 + 角度"只能描述一条直线，实机上根本填不进去。
    /// </summary>
    public const string ChamferLength1Mm = "chamferLength1Mm";

    /// <summary>倒角第一段高度（mm，半径方向）。</summary>
    public const string ChamferHeight1Mm = "chamferHeight1Mm";

    /// <summary>倒角第二段长度（mm，沿辊身方向）。0 表示只有一段。</summary>
    public const string ChamferLength2Mm = "chamferLength2Mm";

    /// <summary>倒角第二段高度（mm，半径方向）。</summary>
    public const string ChamferHeight2Mm = "chamferHeight2Mm";

    /// <summary>倒角形状：斜坡（实机代码 0）或圆弧（实机代码 1）。</summary>
    public const string ChamferKind = "chamferKind";

    /// <summary>探伤扫查螺距（mm/转）。</summary>
    public const string ScanPitchMm = "scanPitchMm";
}

/// <summary>
/// 倒角形状的选项键。实机只有这两种：说明书上的"倒角类型 0 斜坡 / 1 圆弧"。
/// 顺序就是实机代码的顺序，下发时按索引转成 0 / 1。
/// </summary>
public static class ChamferKindChoices
{
    /// <summary>斜坡（实机代码 0）：一条直线过渡。</summary>
    public const string Ramp = "ramp";

    /// <summary>圆弧（实机代码 1）：一段圆弧过渡。</summary>
    public const string Arc = "arc";
}

/// <summary>暂停原因参数的选项键。</summary>
public static class PauseReasonChoices
{
    /// <summary>换砂轮。</summary>
    public const string WheelChange = "wheelChange";

    /// <summary>人工测量。</summary>
    public const string ManualMeasure = "manualMeasure";

    /// <summary>请人来看一眼（工艺确认、质量检查）。</summary>
    public const string Inspect = "inspect";

    /// <summary>其他，由操作员自行掌握。</summary>
    public const string Other = "other";
}

/// <summary>变速模式参数的选项键。</summary>
public static class SpeedVariationChoices
{
    public const string Off = "off";

    /// <summary>头架（工件）转速。实机只有这一种作用对象。</summary>
    public const string Workpiece = "workpiece";
}
