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

/// <summary>辊形设计长度套到辊身上的结果（关系设计第 4 节"2% 规则"）。</summary>
public enum BodyFitKind
{
    /// <summary>一样长，原样用。</summary>
    Exact = 0,

    /// <summary>两端锥度段保持绝对长度，中间段伸缩铺满（一般辊形不限差多少；CVC / 点表差 ≤ 容差）。</summary>
    MiddleAdjusted = 1,

    /// <summary>差得太多：中间段是 CVC 或点表（形状随长度走），长度差超过容差。拦住。</summary>
    TooDifferent = 2,
}

/// <summary>套到辊身上的结果。</summary>
/// <param name="Kind">怎么套的。</param>
/// <param name="DesignLengthMm">辊形设计长度。</param>
/// <param name="BodyLengthMm">辊身长度。</param>
/// <param name="DifferencePercent">长度差占设计长度的百分比（带符号：辊身更长为正）。</param>
/// <param name="Profile">套好的辊形；<see cref="BodyFitKind.TooDifferent"/> 时为 null。</param>
public sealed record BodyFitResult(
    BodyFitKind Kind,
    double DesignLengthMm,
    double BodyLengthMm,
    double DifferencePercent,
    CompositeRollProfile? Profile);

/// <summary>
/// 2% 规则：库里的辊形与具体轧辊无关——两端的锥度段按绝对 mm 保持不动，中间段伸缩铺满辊身。
/// 中间段是 CVC 或点表时，形状本身随长度走，长度差超过容差（machine.json 的 lengthTolerancePercent，默认 2%）就拦住。
/// 取代以前的"拉伸 / 居中"二选一：规则确定，不用人选。
/// </summary>
public static class BodyLengthFit
{
    /// <summary>默认长度容差（%）。</summary>
    public const double DefaultTolerancePercent = 2.0;

    public static BodyFitResult Fit(CompositeRollProfile profile, double designLengthMm, double bodyLengthMm, double tolerancePercent)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (designLengthMm <= 0.0 || bodyLengthMm <= 0.0)
        {
            throw new DomainException("Design length and body length must be positive.");
        }

        double percent = (bodyLengthMm - designLengthMm) / designLengthMm * 100.0;
        if (ProfileFitting.LengthsMatch(designLengthMm, bodyLengthMm))
        {
            return new BodyFitResult(BodyFitKind.Exact, designLengthMm, bodyLengthMm, percent, profile);
        }

        RollProfileSegment[] segments = profile.Segments.ToArray();

        // 两端紧贴端面的锥度段不动；剩下的是"中间段"，按原比例分掉长度差。
        bool headTaper = segments.Length > 1 && segments[0].ProfileTypeKey == ProfileTypeKeys.Taper;
        bool tailTaper = segments.Length > 1 && segments[^1].ProfileTypeKey == ProfileTypeKeys.Taper;
        int first = headTaper ? 1 : 0;
        int last = tailTaper ? segments.Length - 2 : segments.Length - 1;
        if (profile.Layout != ProfileLayout.Sequential || first > last)
        {
            // 旧的叠加辊形分不出段的先后：整条按比例伸缩。
            first = 0;
            last = segments.Length - 1;
        }

        bool shapeFollowsLength = segments[first..(last + 1)]
            .Any(segment => segment.ProfileTypeKey is ProfileTypeKeys.Cvc or ProfileTypeKeys.PointTable);
        if (shapeFollowsLength && Math.Abs(percent) > tolerancePercent + 1e-9)
        {
            return new BodyFitResult(BodyFitKind.TooDifferent, designLengthMm, bodyLengthMm, percent, null);
        }

        double fixedLength = segments.Take(first).Sum(s => s.LengthMm) + segments.Skip(last + 1).Sum(s => s.LengthMm);
        double middleDesign = designLengthMm - fixedLength;
        double middleBody = bodyLengthMm - fixedLength;
        if (middleDesign <= 0.0 || middleBody <= 0.0)
        {
            return new BodyFitResult(BodyFitKind.TooDifferent, designLengthMm, bodyLengthMm, percent, null);
        }

        double ratio = middleBody / middleDesign;
        CompositeRollProfile fitted;
        if (profile.Layout == ProfileLayout.Sequential)
        {
            IEnumerable<SequentialSegment> parts = segments.Select((segment, i) => i < first || i > last
                ? SequentialSegment.From(segment)
                : new SequentialSegment(segment.ProfileTypeKey, segment.LengthMm * ratio, ScalePoints(segment.Parameters, ratio), segment.IsMirrored));
            fitted = CompositeRollProfile.Sequential(profile.StartZMm, parts);
        }
        else
        {
            double whole = bodyLengthMm / designLengthMm;
            fitted = new CompositeRollProfile(
                segments.Select(segment => RollProfileSegment.Create(
                    segment.Order, segment.ProfileTypeKey, segment.FromMm * whole, segment.ToMm * whole,
                    ScalePoints(segment.Parameters, whole), segment.IsMirrored)),
                profile.Layout);
        }

        return new BodyFitResult(BodyFitKind.MiddleAdjusted, designLengthMm, bodyLengthMm, percent, fitted);
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
}
