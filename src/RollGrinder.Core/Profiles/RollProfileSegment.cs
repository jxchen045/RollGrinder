using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// 辊形的一段。整条辊形由若干段叠加而成：
/// 主辊形铺满全长，端部锥度只作用在两端的一小段上，倒角再叠一层。
/// 段只在自己的区间内贡献偏差，区间外为零。
/// </summary>
/// <param name="Order">叠加顺序，从 1 开始。</param>
/// <param name="ProfileTypeKey">曲线类型键（注册表里的类型）。</param>
/// <param name="FromMm">区间起点（辊身坐标 mm）。</param>
/// <param name="ToMm">区间终点（辊身坐标 mm）。</param>
/// <param name="Parameters">该段的参数（界面量）。</param>
/// <param name="IsMirrored">是否沿区间中点镜像。</param>
public sealed record RollProfileSegment(
    int Order,
    string ProfileTypeKey,
    double FromMm,
    double ToMm,
    ParameterSet Parameters,
    bool IsMirrored = false)
{
    /// <summary>区间长度（mm）。</summary>
    public double LengthMm => ToMm - FromMm;

    /// <summary>构造并校验区间。</summary>
    public static RollProfileSegment Create(
        int order,
        string profileTypeKey,
        double fromMm,
        double toMm,
        ParameterSet parameters,
        bool isMirrored = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileTypeKey);
        ArgumentNullException.ThrowIfNull(parameters);

        if (toMm <= fromMm)
        {
            throw new DomainException(
                $"Profile segment {order} ('{profileTypeKey}') has an empty range {fromMm}–{toMm} mm.");
        }

        if (fromMm < 0.0)
        {
            throw new DomainException($"Profile segment {order} starts before the roll body.");
        }

        return new RollProfileSegment(order, profileTypeKey, fromMm, toMm, parameters, isMirrored);
    }
}

/// <summary>
/// 由若干段叠加而成的辊形。合成结果仍然是一条 <see cref="RollProfile"/>，
/// 下发与补偿都只认合成结果。
/// </summary>
public sealed record CompositeRollProfile
{
    public CompositeRollProfile(IEnumerable<RollProfileSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        RollProfileSegment[] ordered = segments.OrderBy(segment => segment.Order).ToArray();
        if (ordered.Length == 0)
        {
            throw new DomainException("A roll profile needs at least one segment.");
        }

        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Order != i + 1)
            {
                throw new DomainException($"Profile segments have a gap or duplicate at order {i + 1}.");
            }
        }

        Segments = ordered;
    }

    /// <summary>按叠加顺序排列的段。</summary>
    public IReadOnlyList<RollProfileSegment> Segments { get; }

    /// <summary>只有一段主辊形的简单辊形。</summary>
    public static CompositeRollProfile Single(string profileTypeKey, RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return new CompositeRollProfile(new[]
        {
            RollProfileSegment.Create(1, profileTypeKey, 0.0, geometry.BodyLengthMm, parameters),
        });
    }

    /// <summary>
    /// 合成整条辊形。每段在自己的区间内按本段长度独立求值，再逐点相加。
    /// </summary>
    public RollProfile Compose(RollGeometry geometry, RollProfileTypeRegistry registry, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(registry);

        var contributions = new List<Func<double, double>>(Segments.Count);
        foreach (RollProfileSegment segment in Segments)
        {
            IRollProfileType profileType = registry.Get(segment.ProfileTypeKey);
            RollGeometry segmentGeometry = RollGeometry.Create(segment.LengthMm, geometry.NominalRadiusMm);

            // 段内用该类型自己的曲线，采样点数按段长占比分配，至少两点。
            int segmentSamples = Math.Max(
                2,
                (int)Math.Round(sampleCount * segment.LengthMm / geometry.BodyLengthMm, MidpointRounding.AwayFromZero));
            RollProfile segmentProfile = profileType.CreateProfile(segmentGeometry, segment.Parameters, segmentSamples);

            double fromMm = segment.FromMm;
            double lengthMm = segment.LengthMm;
            bool mirrored = segment.IsMirrored;

            contributions.Add(bodyPositionMm =>
            {
                if (bodyPositionMm < fromMm || bodyPositionMm > segment.ToMm)
                {
                    return 0.0;
                }

                double withinSegmentMm = bodyPositionMm - fromMm;
                return segmentProfile.RadiusOffsetAtMm(mirrored ? lengthMm - withinSegmentMm : withinSegmentMm);
            });
        }

        return RollProfile.Sample(
            geometry.BodyLengthMm,
            sampleCount,
            bodyPositionMm => contributions.Sum(contribution => contribution(bodyPositionMm)));
    }

    /// <summary>加一段，顺序接在最后。</summary>
    public CompositeRollProfile Add(RollProfileSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new CompositeRollProfile(Segments.Append(segment with { Order = Segments.Count + 1 }));
    }

    /// <summary>删掉一段并重排顺序。</summary>
    public CompositeRollProfile RemoveAt(int order) =>
        new(Segments
            .Where(segment => segment.Order != order)
            .Select((segment, index) => segment with { Order = index + 1 }));

    /// <summary>换掉某一段，顺序不变。编辑器改完参数或区间就走这一条回写。</summary>
    public CompositeRollProfile Replace(int order, RollProfileSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (order < 1 || order > Segments.Count)
        {
            throw new DomainException($"Profile segment {order} does not exist.");
        }

        return new CompositeRollProfile(Segments
            .Select(existing => existing.Order == order ? segment with { Order = order } : existing));
    }

    /// <summary>把一段上移一位。</summary>
    public CompositeRollProfile MoveUp(int order)
    {
        if (order <= 1 || order > Segments.Count)
        {
            return this;
        }

        var reordered = Segments.ToList();
        (reordered[order - 1], reordered[order - 2]) = (reordered[order - 2], reordered[order - 1]);
        return new CompositeRollProfile(reordered.Select((segment, index) => segment with { Order = index + 1 }));
    }

    /// <summary>把一段下移一位。</summary>
    public CompositeRollProfile MoveDown(int order) =>
        order >= 1 && order < Segments.Count ? MoveUp(order + 1) : this;
}
