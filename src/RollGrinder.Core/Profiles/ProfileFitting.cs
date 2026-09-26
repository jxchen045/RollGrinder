using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Profiles;

/// <summary>辊形设计长度与轧辊辊身长度对不上时怎么办（修改稿 5.1：不静默处理，让人选）。</summary>
public enum ProfileFitMode
{
    /// <summary>按比例拉伸：每段的起点与长度一起乘同一个比例，曲线形状跟着伸缩。</summary>
    Stretch = 0,

    /// <summary>
    /// 以辊身中心对齐、不拉伸：曲线原样放在辊身正中。辊身更长时两端各多出一截，
    /// 保持辊形端部的值不变（不留台阶）；辊身更短时两端各截掉一截。
    /// </summary>
    CenterAlign = 1,
}

/// <summary>把库里的辊形对到一支具体的辊上。作业里存的是对好以后的这一份。</summary>
public static class ProfileFitting
{
    /// <summary>设计长度与辊身长度差多少 mm 以内算一样长，不用问。</summary>
    public const double LengthToleranceMm = 0.5;

    public static bool LengthsMatch(double designLengthMm, double bodyLengthMm) =>
        Math.Abs(designLengthMm - bodyLengthMm) <= LengthToleranceMm;

    public static CompositeRollProfile Fit(
        CompositeRollProfile profile,
        double designLengthMm,
        double bodyLengthMm,
        ProfileFitMode mode,
        RollProfileTypeRegistry registry,
        int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);
        if (designLengthMm <= 0.0 || bodyLengthMm <= 0.0)
        {
            throw new DomainException("Design length and body length must be positive.");
        }

        if (LengthsMatch(designLengthMm, bodyLengthMm))
        {
            return profile;
        }

        return mode == ProfileFitMode.Stretch
            ? Stretch(profile, bodyLengthMm / designLengthMm)
            : CenterAlign(profile, designLengthMm, bodyLengthMm, registry, sampleCount);
    }

    /// <summary>每段起止一起乘比例；点表段里的点跟着伸缩。叠加辊形与顺接辊形都一样处理。</summary>
    private static CompositeRollProfile Stretch(CompositeRollProfile profile, double ratio)
    {
        IEnumerable<RollProfileSegment> scaled = profile.Segments.Select(segment => RollProfileSegment.Create(
            segment.Order,
            segment.ProfileTypeKey,
            segment.FromMm * ratio,
            segment.ToMm * ratio,
            ScalePoints(segment.Parameters, ratio),
            segment.IsMirrored));
        return new CompositeRollProfile(scaled, profile.Layout);
    }

    private static ParameterSet ScalePoints(ParameterSet parameters, double ratio)
    {
        if (!parameters.TryGet(PointTableProfileType.PointsKey, out ParameterValue? value) || value is not { Kind: ParameterValueKind.Points })
        {
            return parameters;
        }

        return parameters.With(
            PointTableProfileType.PointsKey,
            ParameterValue.FromPoints(value.Points.Select(point => point with { X = point.X * ratio })));
    }

    /// <summary>
    /// 把设计曲线平移到辊身正中，采样成铺满辊身的一段点表（折线）。采样点数就是下发的点数，
    /// 所以下发出去的每个点都正好落在设计曲线上；设计长度以外按端点的值延伸。
    /// </summary>
    private static CompositeRollProfile CenterAlign(
        CompositeRollProfile profile, double designLengthMm, double bodyLengthMm, RollProfileTypeRegistry registry, int sampleCount)
    {
        RollGeometry design = RollGeometry.Create(designLengthMm, 1.0);
        RollProfile designCurve = profile.Compose(design, registry, sampleCount);
        double offsetMm = (bodyLengthMm - designLengthMm) / 2.0;

        RollProfile onBody = RollProfile.Sample(
            bodyLengthMm, sampleCount, bodyPositionMm => designCurve.RadiusOffsetAtMm(bodyPositionMm - offsetMm));
        TablePoint[] points = onBody.Points
            .Select(point => new TablePoint(point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();

        ParameterSet parameters = PointTableProfileType.DefaultsFor(bodyLengthMm)
            .With(PointTableProfileType.PointsKey, ParameterValue.FromPoints(points))
            .With(PointTableProfileType.InterpolationKey, ParameterValue.FromChoice(nameof(InterpolationMethod.Linear)));
        return CompositeRollProfile.Sequential(
            0.0, new[] { new SequentialSegment(ProfileTypeKeys.PointTable, bodyLengthMm, parameters) });
    }
}
