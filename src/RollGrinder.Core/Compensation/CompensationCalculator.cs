using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Geometry;

namespace RollGrinder.Core.Compensation;

/// <summary>
/// 补偿计算的设定。取值来自配置，领域层不内置任何工艺数字。
/// </summary>
/// <param name="Gain">补偿增益：本次补偿吸收多少比例的偏差，0–1。</param>
/// <param name="SmoothingPoints">平滑窗口点数（奇数，1 表示不平滑）。</param>
/// <param name="MaxCorrectionRadiusMm">单次补偿的最大修正量（半径量 mm）。</param>
public sealed record CompensationSettings(double Gain, int SmoothingPoints, double MaxCorrectionRadiusMm)
{
    /// <summary>构造并校验。</summary>
    public static CompensationSettings Create(double gain, int smoothingPoints, double maxCorrectionRadiusMm)
    {
        if (gain is <= 0.0 or > 1.0)
        {
            throw new DomainException("Compensation gain must be within (0, 1].");
        }

        if (smoothingPoints < 1 || smoothingPoints % 2 == 0)
        {
            throw new DomainException("Compensation smoothing window must be a positive odd number of points.");
        }

        if (maxCorrectionRadiusMm <= 0.0)
        {
            throw new DomainException("The maximum compensation correction must be positive.");
        }

        return new CompensationSettings(gain, smoothingPoints, maxCorrectionRadiusMm);
    }
}

/// <summary>
/// 辊形偏差与补偿量的计算。全部用半径量 mm 与辊身坐标。
/// </summary>
public static class CompensationCalculator
{
    /// <summary>
    /// 实测减目标，得到偏差曲线（半径量 mm，正表示磨少了）。
    /// 采样点取目标辊形的点位，保证与下发的点位一一对应。
    /// </summary>
    public static RollProfile ComputeDeviation(MeasuredProfile measured, RollProfile target, RollGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(geometry);

        return new RollProfile(target.Points.Select(point =>
        {
            double targetRadiusMm = geometry.NominalRadiusMm + point.RadiusOffsetMm;
            double measuredRadiusMm = measured.MeasuredRadiusAtMm(point.BodyPositionMm);
            return new ProfilePoint(point.BodyPositionMm, measuredRadiusMm - targetRadiusMm);
        }));
    }

    /// <summary>
    /// 由上一次补偿与本次偏差算出新的补偿曲线。
    /// 磨少了（偏差为正）就要多磨一点，因此补偿沿偏差的反方向走。
    /// 结果先平滑再限幅，避免把测量噪声直接变成刀路抖动。
    /// </summary>
    public static RollProfile ComputeCompensation(
        RollProfile? previous,
        RollProfile deviation,
        CompensationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(deviation);
        ArgumentNullException.ThrowIfNull(settings);

        var raw = new List<ProfilePoint>(deviation.Points.Count);
        foreach (ProfilePoint point in deviation.Points)
        {
            double previousOffsetMm = previous?.RadiusOffsetAtMm(point.BodyPositionMm) ?? 0.0;
            raw.Add(new ProfilePoint(
                point.BodyPositionMm,
                previousOffsetMm - (settings.Gain * point.RadiusOffsetMm)));
        }

        IReadOnlyList<ProfilePoint> smoothed = Smooth(raw, settings.SmoothingPoints);

        return new RollProfile(smoothed.Select(point => new ProfilePoint(
            point.BodyPositionMm,
            Math.Clamp(point.RadiusOffsetMm, -settings.MaxCorrectionRadiusMm, settings.MaxCorrectionRadiusMm))));
    }

    /// <summary>滑动平均平滑；窗口在两端自动收缩。</summary>
    public static IReadOnlyList<ProfilePoint> Smooth(IReadOnlyList<ProfilePoint> points, int windowPoints)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (windowPoints <= 1)
        {
            return points;
        }

        int halfWindow = windowPoints / 2;
        var smoothed = new List<ProfilePoint>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            int from = Math.Max(0, i - halfWindow);
            int to = Math.Min(points.Count - 1, i + halfWindow);

            double sum = 0.0;
            for (int j = from; j <= to; j++)
            {
                sum += points[j].RadiusOffsetMm;
            }

            smoothed.Add(new ProfilePoint(points[i].BodyPositionMm, sum / (to - from + 1)));
        }

        return smoothed;
    }
}
