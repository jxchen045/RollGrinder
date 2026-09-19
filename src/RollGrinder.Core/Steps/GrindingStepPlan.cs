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
}
