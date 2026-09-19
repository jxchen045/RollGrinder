using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Geometry;

/// <summary>
/// 采样后的辊形曲线。点按辊身坐标升序排列，不可变。
/// </summary>
public sealed record RollProfile
{
    public RollProfile(IEnumerable<ProfilePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        ProfilePoint[] ordered = points.OrderBy(point => point.BodyPositionMm).ToArray();
        if (ordered.Length < 2)
        {
            throw new DomainException("A roll profile needs at least two points.");
        }

        Points = ordered;
    }

    public IReadOnlyList<ProfilePoint> Points { get; }

    public double BodyLengthMm => Points[^1].BodyPositionMm - Points[0].BodyPositionMm;

    /// <summary>最大半径偏差（mm）。</summary>
    public double MaxRadiusOffsetMm => Points.Max(point => point.RadiusOffsetMm);

    /// <summary>最小半径偏差（mm）。</summary>
    public double MinRadiusOffsetMm => Points.Min(point => point.RadiusOffsetMm);

    /// <summary>峰谷值，界面常用的直径量微米。</summary>
    public double PeakToValleyDiameterMicrometer =>
        UnitConversion.RadiusMmToDiameterMicrometer(MaxRadiusOffsetMm - MinRadiusOffsetMm);

    /// <summary>按辊身坐标线性插值取半径偏差；超出范围按端点取值。</summary>
    public double RadiusOffsetAtMm(double bodyPositionMm)
    {
        if (bodyPositionMm <= Points[0].BodyPositionMm)
        {
            return Points[0].RadiusOffsetMm;
        }

        if (bodyPositionMm >= Points[^1].BodyPositionMm)
        {
            return Points[^1].RadiusOffsetMm;
        }

        for (int i = 1; i < Points.Count; i++)
        {
            ProfilePoint right = Points[i];
            if (bodyPositionMm > right.BodyPositionMm)
            {
                continue;
            }

            ProfilePoint left = Points[i - 1];
            double span = right.BodyPositionMm - left.BodyPositionMm;
            if (span <= 0.0)
            {
                return right.RadiusOffsetMm;
            }

            double ratio = (bodyPositionMm - left.BodyPositionMm) / span;
            return left.RadiusOffsetMm + ((right.RadiusOffsetMm - left.RadiusOffsetMm) * ratio);
        }

        return Points[^1].RadiusOffsetMm;
    }

    /// <summary>逐点相加，用于把补偿量叠加到目标辊形上。两条曲线的采样点必须一致。</summary>
    public RollProfile Add(RollProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new RollProfile(Points.Select(point =>
            new ProfilePoint(point.BodyPositionMm, point.RadiusOffsetMm + other.RadiusOffsetAtMm(point.BodyPositionMm))));
    }

    /// <summary>在 [0, bodyLengthMm] 上等距采样一个函数（返回半径偏差 mm）。</summary>
    public static RollProfile Sample(double bodyLengthMm, int sampleCount, Func<double, double> radiusOffsetMmAt)
    {
        ArgumentNullException.ThrowIfNull(radiusOffsetMmAt);
        if (bodyLengthMm <= 0.0)
        {
            throw new DomainException("Roll body length must be positive.");
        }

        if (sampleCount < 2)
        {
            throw new DomainException("A roll profile needs at least two samples.");
        }

        var points = new List<ProfilePoint>(sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            double bodyPositionMm = bodyLengthMm * i / (sampleCount - 1);
            points.Add(new ProfilePoint(bodyPositionMm, radiusOffsetMmAt(bodyPositionMm)));
        }

        return new RollProfile(points);
    }
}
