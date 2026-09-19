using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Core.Compensation;

/// <summary>
/// 一个测点：辊身坐标 + 实测半径（mm）。
/// </summary>
/// <param name="BodyPositionMm">辊身坐标（mm）。</param>
/// <param name="MeasuredRadiusMm">实测半径（mm）。</param>
public sealed record MeasurementPoint(double BodyPositionMm, double MeasuredRadiusMm);

/// <summary>
/// 一次测量的结果，按辊身坐标升序。不可变。
/// </summary>
public sealed record MeasuredProfile
{
    public MeasuredProfile(IEnumerable<MeasurementPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        MeasurementPoint[] ordered = points.OrderBy(point => point.BodyPositionMm).ToArray();
        if (ordered.Length < 2)
        {
            throw new DomainException("A measured profile needs at least two points.");
        }

        Points = ordered;
    }

    public IReadOnlyList<MeasurementPoint> Points { get; }

    /// <summary>实测平均半径（mm）。</summary>
    public double MeanRadiusMm => Points.Average(point => point.MeasuredRadiusMm);

    /// <summary>按辊身坐标线性插值取实测半径；超出范围按端点取值。</summary>
    public double MeasuredRadiusAtMm(double bodyPositionMm)
    {
        if (bodyPositionMm <= Points[0].BodyPositionMm)
        {
            return Points[0].MeasuredRadiusMm;
        }

        if (bodyPositionMm >= Points[^1].BodyPositionMm)
        {
            return Points[^1].MeasuredRadiusMm;
        }

        for (int i = 1; i < Points.Count; i++)
        {
            MeasurementPoint right = Points[i];
            if (bodyPositionMm > right.BodyPositionMm)
            {
                continue;
            }

            MeasurementPoint left = Points[i - 1];
            double span = right.BodyPositionMm - left.BodyPositionMm;
            if (span <= 0.0)
            {
                return right.MeasuredRadiusMm;
            }

            double ratio = (bodyPositionMm - left.BodyPositionMm) / span;
            return left.MeasuredRadiusMm + ((right.MeasuredRadiusMm - left.MeasuredRadiusMm) * ratio);
        }

        return Points[^1].MeasuredRadiusMm;
    }
}
