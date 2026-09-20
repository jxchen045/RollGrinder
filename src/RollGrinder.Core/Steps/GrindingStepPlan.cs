using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 一道工序展开后的执行计划。领域层只算"磨多少、怎么走"，
/// 具体怎么变成 NC 指令由 RollGrinder.Nc 负责。
///
/// 位置参数是所有工序都有的基本量；带 init 的属性是各类工序按需补充的，
/// 不填就是"本工序不涉及"。
/// </summary>
/// <param name="StepTypeKey">工序类型键。</param>
/// <param name="PassCount">磨削道次。一道 = 一个往复。</param>
/// <param name="InfeedPerPassRadiusMm">周期进给：每个换向点的切深（半径量 mm）。连续进给时为 0。</param>
/// <param name="FeedMmPerMin">拖板速度（mm/min）。</param>
/// <param name="WorkpieceSpeedRpm">头架转速（r/min）。</param>
/// <param name="WheelSpeedRpm">砂轮转速（r/min）。为 0 表示由 NC 按线速度恒线速换算。</param>
/// <param name="SparkOutPassCount">光磨道次。</param>
/// <param name="RequiresMeasurement">本工序结束后是否需要测量。</param>
public sealed record GrindingStepPlan(
    string StepTypeKey,
    int PassCount,
    double InfeedPerPassRadiusMm,
    double FeedMmPerMin,
    double WorkpieceSpeedRpm,
    double WheelSpeedRpm,
    int SparkOutPassCount,
    bool RequiresMeasurement)
{
    /// <summary>横向进给方式。默认周期进给——这是磨削工序里更常见、也更安全的那一种。</summary>
    public StepFeedMode FeedMode { get; init; } = StepFeedMode.PerReversal;

    /// <summary>连续进给速率（半径量 mm/min）。周期进给时为 0。</summary>
    public double ContinuousInfeedRadiusMmPerMin { get; init; }

    /// <summary>本工序的目标去除量（半径量 mm）。两种进给方式下都是终止条件。</summary>
    public double TargetStockRadiusMm { get; init; }

    /// <summary>砂轮线速度（m/s）。</summary>
    public double WheelSurfaceSpeedMPerSec { get; init; }

    /// <summary>换向点停顿（s）。周期进给就在这个停顿里完成。</summary>
    public double ReversalDwellSeconds { get; init; }

    /// <summary>本工序进行中是否允许在线测量。</summary>
    public bool InProcessMeasurement { get; init; }

    /// <summary>变速设置（打散再生颤振）。</summary>
    public SpeedVariation SpeedVariation { get; init; } = SpeedVariation.Off;

    /// <summary>本工序按道次预算算出的总切深（半径量 mm）。仅周期进给有意义。</summary>
    public double TotalInfeedRadiusMm => PassCount * InfeedPerPassRadiusMm;

    /// <summary>本工序的总去除量（直径量 µm），界面显示用。</summary>
    public double TotalStockDiameterMicrometer =>
        UnitConversion.RadiusMmToDiameterMicrometer(TotalInfeedRadiusMm);

    /// <summary>本工序是否真的在切削。</summary>
    public bool IsCutting => FeedMode != StepFeedMode.None
        && (InfeedPerPassRadiusMm > 0.0 || ContinuousInfeedRadiusMmPerMin > 0.0);

    /// <summary>
    /// 估算本工序耗时。
    ///
    /// 一道 = 一个往复 = 两个单行程 + 两次换向停顿。
    /// 连续进给时，除了道次预算还有一个终止条件——累计切入达到目标去除量，
    /// 两者取先到者；这与 NC 侧的循环终止逻辑一致。
    ///
    /// 这是排产用的估算，不是承诺——实际还受修整、测量与暂停影响。
    /// </summary>
    public TimeSpan EstimateDuration(RollGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (FeedMmPerMin <= 0.0)
        {
            return TimeSpan.Zero;
        }

        double singleStrokeMinutes = geometry.BodyLengthMm / FeedMmPerMin;
        double dwellMinutes = ReversalDwellSeconds / 60.0;
        double returnStrokeMinutes = (2.0 * singleStrokeMinutes) + (2.0 * dwellMinutes);
        double passBudgetMinutes = (PassCount + SparkOutPassCount) * returnStrokeMinutes;

        if (FeedMode != StepFeedMode.Continuous
            || ContinuousInfeedRadiusMmPerMin <= 0.0
            || TargetStockRadiusMm <= 0.0)
        {
            return TimeSpan.FromMinutes(passBudgetMinutes);
        }

        double stockMinutes = TargetStockRadiusMm / ContinuousInfeedRadiusMmPerMin;
        return TimeSpan.FromMinutes(Math.Min(passBudgetMinutes, stockMinutes));
    }
}
