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
/// <param name="InfeedPerPassRadiusMm">周期进给分量：每道次换向时的切深（半径量 mm）。为 0 表示不用这一路。</param>
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
    /// <summary>连续进给分量：持续切入的速率（半径量 mm/min）。为 0 表示不用这一路。</summary>
    public double ContinuousInfeedRadiusMmPerMin { get; init; }

    /// <summary>
    /// 本工序用到的进给分量，由两个进给量推导，不单独存——
    /// 存了就有可能与进给量对不上，而 NC 侧信哪一个就说不清了。
    /// </summary>
    public StepFeedMode FeedMode => (InfeedPerPassRadiusMm > 0.0, ContinuousInfeedRadiusMmPerMin > 0.0) switch
    {
        (true, true) => StepFeedMode.Combined,
        (false, true) => StepFeedMode.Continuous,
        (true, false) => StepFeedMode.PerReversal,
        _ => StepFeedMode.None,
    };

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

    /// <summary>周期分量按道次预算算出的总切深（半径量 mm）。不含连续分量——
    /// 连续分量要知道行程时间才算得出来，见 <see cref="PlannedStockRadiusMm"/>。</summary>
    public double TotalInfeedRadiusMm => PassCount * InfeedPerPassRadiusMm;

    /// <summary>本工序的总去除量（直径量 µm），界面显示用。</summary>
    public double TotalStockDiameterMicrometer =>
        UnitConversion.RadiusMmToDiameterMicrometer(TotalInfeedRadiusMm);

    /// <summary>本工序是否真的在切削。</summary>
    public bool IsCutting => FeedMode != StepFeedMode.None;

    /// <summary>
    /// 一道（一个往复）要走多久：两个单行程 + 两次换向停顿（min）。
    /// 拖板速度为 0 时返回 0——这道工序走不起来，别拿它当除数。
    /// </summary>
    public double ReturnStrokeMinutes(RollGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return FeedMmPerMin <= 0.0
            ? 0.0
            : (2.0 * geometry.BodyLengthMm / FeedMmPerMin) + (2.0 * ReversalDwellSeconds / 60.0);
    }

    /// <summary>
    /// 两路分量合起来，一道次实际切进去多少（半径量 mm）。
    /// 单刀切深限幅按这个卡——分两路设不该成为绕过机床能力的办法。
    /// </summary>
    public double InfeedPerPassWithContinuousRadiusMm(RollGeometry geometry) =>
        InfeedPerPassRadiusMm + (ContinuousInfeedRadiusMmPerMin * ReturnStrokeMinutes(geometry));

    /// <summary>
    /// 按道次预算，两路合起来打算磨掉多少（半径量 mm）。
    /// 与 <see cref="TargetStockRadiusMm"/> 谁先到先停。
    /// </summary>
    public double PlannedStockRadiusMm(RollGeometry geometry) =>
        PassCount * InfeedPerPassWithContinuousRadiusMm(geometry);

    /// <summary>
    /// 估算本工序耗时。
    ///
    /// 一道 = 一个往复 = 两个单行程 + 两次换向停顿。
    /// 切削段有两个终止条件——走满 <see cref="PassCount"/> 道，或者两路分量累计切入
    /// 达到 <see cref="TargetStockRadiusMm"/>，谁先到先停；光磨道次在那之后照走。
    ///
    /// 这是排产用的估算，不是承诺——实际还受修整、测量与暂停影响。
    /// </summary>
    public TimeSpan EstimateDuration(RollGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        double returnStrokeMinutes = ReturnStrokeMinutes(geometry);
        if (returnStrokeMinutes <= 0.0)
        {
            return TimeSpan.Zero;
        }

        double cuttingMinutes = PassCount * returnStrokeMinutes;

        // 两路合起来的去除速率（半径量 mm/min）：连续分量本来就是速率，
        // 周期分量是"每道一刀"，除以一道的时长折算成速率。
        double removalRateRadiusMmPerMin =
            ContinuousInfeedRadiusMmPerMin + (InfeedPerPassRadiusMm / returnStrokeMinutes);

        if (TargetStockRadiusMm > 0.0 && removalRateRadiusMmPerMin > 0.0)
        {
            cuttingMinutes = Math.Min(cuttingMinutes, TargetStockRadiusMm / removalRateRadiusMmPerMin);
        }

        return TimeSpan.FromMinutes(cuttingMinutes + (SparkOutPassCount * returnStrokeMinutes));
    }
}
