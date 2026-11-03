using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Profiles;

/// <summary>方案的名义长度对到这一次要磨的辊身长度上，怎么对的。</summary>
public enum LengthAlignmentKind
{
    /// <summary>一样长，原样用。</summary>
    Exact = 0,

    /// <summary>辊身更短：以中心为基准，两端各截掉一截（只在端部非工作区里）。</summary>
    Trimmed = 1,

    /// <summary>辊身稍长：以中心为基准，两端按切线各延伸一截（只在端部非工作区里）。</summary>
    Extended = 2,

    /// <summary>差得超过下发容差，或每端的截 / 延量超出端部非工作区：拦下，请工艺员处理。</summary>
    OutOfTolerance = 3,
}

/// <summary>
/// 长度对齐的结论（设计说明 4.5）：以辊身中心为 0，两端截取或延伸，任何时候都不等比伸缩。
/// </summary>
/// <param name="Kind">怎么对的。</param>
/// <param name="NominalLengthMm">方案辊形的名义长度。</param>
/// <param name="BodyLengthMm">这一次要磨的辊身长度（有实测用实测，否则用名义）。</param>
/// <param name="HeadZoneMm">头架侧端部非工作区宽度。</param>
/// <param name="TailZoneMm">尾架侧端部非工作区宽度。</param>
/// <param name="ToleranceMm">下发容差（实测与名义之差的上限）。</param>
/// <param name="Profile">对好的辊形（辊身坐标 0 … 辊身长度）；超出容差时为 null。</param>
public sealed record LengthAlignment(
    LengthAlignmentKind Kind,
    double NominalLengthMm,
    double BodyLengthMm,
    double HeadZoneMm,
    double TailZoneMm,
    double ToleranceMm,
    CompositeRollProfile? Profile)
{
    /// <summary>辊身减名义（mm）：负数是更短。</summary>
    public double DifferenceMm => BodyLengthMm - NominalLengthMm;

    /// <summary>每端截掉（负）或延伸（正）多少 mm。</summary>
    public double PerEndMm => DifferenceMm / 2.0;

    /// <summary>可以下发。</summary>
    public bool CanUse => Kind != LengthAlignmentKind.OutOfTolerance;
}

/// <summary>
/// 以辊身中心为基准对齐长度（设计说明 4.5"截取与延伸处的连续性"）：
/// <list type="bullet">
/// <item>截取只缩短定义域，留下的部分逐点与原曲线相同，不产生台阶；</item>
/// <item>延伸按切线：接点处值与斜率连续，曲率变为 0；</item>
/// <item>只发生在端部非工作区：每端的截 / 延量超出非工作区宽度、或总差超出下发容差，按"超出容差"拦下；</item>
/// <item>不加平滑过渡、不等比伸缩——工作区内的曲线始终是原曲线。</item>
/// </list>
/// 端部是圆柱段时直接把圆柱段截短或加长（结构不变、精确）；否则按辊身采样成一段点表（线性插值，与下发一致）。
/// </summary>
public static class ProfileLengthAlignment
{
    /// <summary>差这么多以内算一样长（浮点、显示取整）。</summary>
    public const double ExactToleranceMm = 0.001;

    /// <summary>
    /// 端部非工作区的默认宽度：端部是圆柱段时取它的长度（"不修形的端部写成圆柱段"），否则 0。
    /// </summary>
    public static (double HeadMm, double TailMm) DefaultEndZones(CompositeRollProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Layout != ProfileLayout.Sequential)
        {
            return (0.0, 0.0);
        }

        RollProfileSegment head = profile.Segments[0];
        RollProfileSegment tail = profile.Segments[^1];
        if (profile.Segments.Count == 1)
        {
            // 只有一段：整条就是这一段，没有"端部"可言。
            return (0.0, 0.0);
        }

        return (
            head.ProfileTypeKey == ProfileTypeKeys.Cylindrical ? head.LengthMm : 0.0,
            tail.ProfileTypeKey == ProfileTypeKeys.Cylindrical ? tail.LengthMm : 0.0);
    }

    /// <summary>对齐。</summary>
    /// <param name="profile">方案的辊形（辊身坐标 0 … 名义长度）。</param>
    /// <param name="nominalLengthMm">名义长度。</param>
    /// <param name="bodyLengthMm">这一次的辊身长度。</param>
    /// <param name="toleranceMm">下发容差（mm）。</param>
    /// <param name="endZoneOverrideMm">方案填的端部非工作区宽度（两端一样）；null 取默认。</param>
    /// <param name="registry">曲线类型。</param>
    /// <param name="sampleCount">采样点数（就是下发的点数）。</param>
    public static LengthAlignment Align(
        CompositeRollProfile profile,
        double nominalLengthMm,
        double bodyLengthMm,
        double toleranceMm,
        double? endZoneOverrideMm,
        RollProfileTypeRegistry registry,
        int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);
        if (nominalLengthMm <= 0.0 || bodyLengthMm <= 0.0)
        {
            throw new DomainException("Nominal length and body length must be positive.");
        }

        (double head, double tail) = endZoneOverrideMm is double zone && zone >= 0.0 ? (zone, zone) : DefaultEndZones(profile);
        double difference = bodyLengthMm - nominalLengthMm;
        if (Math.Abs(difference) <= ExactToleranceMm)
        {
            return new LengthAlignment(LengthAlignmentKind.Exact, nominalLengthMm, bodyLengthMm, head, tail, toleranceMm, profile);
        }

        double perEnd = Math.Abs(difference) / 2.0;
        if (Math.Abs(difference) > toleranceMm + 1e-9 || perEnd > head + 1e-9 || perEnd > tail + 1e-9)
        {
            return new LengthAlignment(LengthAlignmentKind.OutOfTolerance, nominalLengthMm, bodyLengthMm, head, tail, toleranceMm, null);
        }

        LengthAlignmentKind kind = difference < 0.0 ? LengthAlignmentKind.Trimmed : LengthAlignmentKind.Extended;
        CompositeRollProfile aligned = TryAdjustEndCylinders(profile, nominalLengthMm, difference / 2.0)
            ?? Resample(profile, nominalLengthMm, bodyLengthMm, registry, sampleCount);
        return new LengthAlignment(kind, nominalLengthMm, bodyLengthMm, head, tail, toleranceMm, aligned);
    }

    /// <summary>
    /// 两端是圆柱段、铺满名义长度：两端圆柱段各加 / 减 <paramref name="perEndMm"/>。
    /// 圆柱的切线延伸就是它自己，所以这与"截取 / 切线延伸"完全一样，还保留了段结构。
    /// </summary>
    private static CompositeRollProfile? TryAdjustEndCylinders(CompositeRollProfile profile, double nominalLengthMm, double perEndMm)
    {
        if (profile.Layout != ProfileLayout.Sequential || profile.Segments.Count < 2
            || Math.Abs(profile.StartZMm) > CompositeRollProfile.JoinToleranceMm
            || Math.Abs(profile.EndZMm - nominalLengthMm) > 1e-6)
        {
            return null;
        }

        SequentialSegment[] parts = profile.SequentialSegments().ToArray();
        if (parts[0].ProfileTypeKey != ProfileTypeKeys.Cylindrical || parts[^1].ProfileTypeKey != ProfileTypeKeys.Cylindrical
            || parts[0].LengthMm + perEndMm <= 1e-6 || parts[^1].LengthMm + perEndMm <= 1e-6)
        {
            return null;
        }

        parts[0] = parts[0] with { LengthMm = parts[0].LengthMm + perEndMm };
        parts[^1] = parts[^1] with { LengthMm = parts[^1].LengthMm + perEndMm };
        return CompositeRollProfile.Sequential(0.0, parts);
    }

    /// <summary>按辊身采样成一段点表：中心对齐取原曲线的值，名义长度以外按端点切线延伸。</summary>
    private static CompositeRollProfile Resample(
        CompositeRollProfile profile, double nominalLengthMm, double bodyLengthMm, RollProfileTypeRegistry registry, int sampleCount)
    {
        int count = Math.Max(sampleCount, 2);
        RollProfile design = profile.Compose(RollGeometry.Create(nominalLengthMm, 1.0), registry, count);
        IReadOnlyList<ProfilePoint> points = design.Points;
        double headSlope = Slope(points[0], points[1]);
        double tailSlope = Slope(points[^2], points[^1]);
        double offset = (bodyLengthMm - nominalLengthMm) / 2.0;

        double ValueAt(double bodyPositionMm)
        {
            double z = bodyPositionMm - offset;
            if (z < 0.0)
            {
                return points[0].RadiusOffsetMm + (headSlope * z);
            }

            if (z > nominalLengthMm)
            {
                return points[^1].RadiusOffsetMm + (tailSlope * (z - nominalLengthMm));
            }

            return design.RadiusOffsetAtMm(z);
        }

        RollProfile onBody = RollProfile.Sample(bodyLengthMm, count, ValueAt);
        TablePoint[] table = onBody.Points
            .Select(point => new TablePoint(point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();
        ParameterSet parameters = PointTableProfileType.DefaultsFor(bodyLengthMm)
            .With(PointTableProfileType.PointsKey, ParameterValue.FromPoints(table))
            .With(PointTableProfileType.InterpolationKey, ParameterValue.FromChoice(nameof(InterpolationMethod.Linear)));
        return CompositeRollProfile.Sequential(
            0.0, new[] { new SequentialSegment(ProfileTypeKeys.PointTable, bodyLengthMm, parameters) });
    }

    private static double Slope(ProfilePoint left, ProfilePoint right)
    {
        double span = right.BodyPositionMm - left.BodyPositionMm;
        return span <= 0.0 ? 0.0 : (right.RadiusOffsetMm - left.RadiusOffsetMm) / span;
    }
}
