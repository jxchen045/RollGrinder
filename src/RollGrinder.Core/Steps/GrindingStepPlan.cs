using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 一道工序展开后的执行计划。领域层只算"磨多少、怎么走"，
/// 具体怎么变成 NC 指令由 RollGrinder.Nc 负责。
/// </summary>
/// <param name="StepTypeKey">工序类型键。</param>
/// <param name="PassCount">走刀次数。</param>
/// <param name="InfeedPerPassRadiusMm">每次走刀的切深（半径量 mm）。</param>
/// <param name="FeedMmPerMin">轴向进给（mm/min）。</param>
/// <param name="WorkpieceSpeedRpm">工件转速（r/min）。</param>
/// <param name="WheelSpeedRpm">砂轮转速（r/min）。</param>
/// <param name="SparkOutPassCount">光磨次数。</param>
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
    /// <summary>本工序的总切深（半径量 mm）。</summary>
    public double TotalInfeedRadiusMm => PassCount * InfeedPerPassRadiusMm;

    /// <summary>本工序的总去除量（直径量 µm），界面显示用。</summary>
    public double TotalStockDiameterMicrometer =>
        UnitConversion.RadiusMmToDiameterMicrometer(TotalInfeedRadiusMm);

    /// <summary>
    /// 估算本工序耗时：每道一个往复，按辊身长度与轴向进给算，光磨道次一并计入。
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
        int strokeCount = (PassCount + SparkOutPassCount) * 2;
        return TimeSpan.FromMinutes(strokeCount * singleStrokeMinutes);
    }
}
