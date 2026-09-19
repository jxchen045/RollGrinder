using System;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Compensation;

/// <summary>
/// 一次测量相对目标辊形的质量评价。界面按直径量微米显示。
/// </summary>
/// <param name="MaxDeviationRadiusMm">最大正偏差（半径量 mm）。</param>
/// <param name="MinDeviationRadiusMm">最大负偏差（半径量 mm）。</param>
/// <param name="MeanDeviationRadiusMm">平均偏差（半径量 mm）。</param>
public sealed record ProfileQuality(
    double MaxDeviationRadiusMm,
    double MinDeviationRadiusMm,
    double MeanDeviationRadiusMm)
{
    /// <summary>峰谷值（直径量 µm）。</summary>
    public double PeakToValleyDiameterMicrometer =>
        UnitConversion.RadiusMmToDiameterMicrometer(MaxDeviationRadiusMm - MinDeviationRadiusMm);

    /// <summary>绝对值最大的偏差（直径量 µm）。</summary>
    public double WorstDeviationDiameterMicrometer =>
        UnitConversion.RadiusMmToDiameterMicrometer(
            Math.Max(Math.Abs(MaxDeviationRadiusMm), Math.Abs(MinDeviationRadiusMm)));

    /// <summary>是否在给定的公差内（公差为直径量 µm）。</summary>
    public bool IsWithinToleranceDiameterMicrometer(double toleranceDiameterMicrometer) =>
        WorstDeviationDiameterMicrometer <= toleranceDiameterMicrometer;

    /// <summary>从偏差曲线统计。</summary>
    public static ProfileQuality FromDeviation(RollProfile deviation)
    {
        ArgumentNullException.ThrowIfNull(deviation);
        return new ProfileQuality(
            deviation.Points.Max(point => point.RadiusOffsetMm),
            deviation.Points.Min(point => point.RadiusOffsetMm),
            deviation.Points.Average(point => point.RadiusOffsetMm));
    }
}
