using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// 辊形的一段，在辊身坐标上占 [<see cref="FromMm"/>, <see cref="ToMm"/>]。
/// Z 原点是磨削起点（头架侧辊身端面），向尾架为正。
/// 段内按本段自己的长度求本类型的曲线，区间外不贡献。
/// 段与段怎么拼成整条辊形见 <see cref="ProfileLayout"/>。
/// </summary>
/// <param name="Order">顺序，从 1 开始（顺接辊形里就是从头架到尾架的次序）。</param>
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

/// <summary>段与段怎么拼成整条辊形。</summary>
public enum ProfileLayout
{
    /// <summary>
    /// 叠加（阶段 0 及以前）：各段区间可以重叠，重叠处偏差相加。
    /// 只为读回旧的辊形库条目与作业快照而保留，新编的辊形不再用。
    /// </summary>
    Superimposed = 0,

    /// <summary>
    /// 顺接：第一段给起点 Z，之后每段只给长度，首尾相接，不会重叠或断开。
    /// 每一点只属于一段（段界处属于后一段；最后一段含终点）。
    /// </summary>
    Sequential = 1,
}

/// <summary>顺接辊形的一段：只有长度，起止 Z 由前面的段推出来。</summary>
/// <param name="ProfileTypeKey">曲线类型键。</param>
/// <param name="LengthMm">段长（mm），大于 0。</param>
/// <param name="Parameters">该段参数（界面量）。</param>
/// <param name="IsMirrored">是否沿段中点镜像（头架端的锥度就是尾架端锥度的镜像）。</param>
public sealed record SequentialSegment(
    string ProfileTypeKey,
    double LengthMm,
    ParameterSet Parameters,
    bool IsMirrored = false)
{
    public static SequentialSegment From(RollProfileSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new SequentialSegment(segment.ProfileTypeKey, segment.LengthMm, segment.Parameters, segment.IsMirrored);
    }
}

/// <summary>
/// 由若干段拼成的辊形。合成结果仍然是一条 <see cref="RollProfile"/>，下发与补偿都只认合成结果。
/// </summary>
public sealed record CompositeRollProfile
{
    /// <summary>顺接辊形里相邻两段首尾差多少 mm 以内算接上（浮点误差）。</summary>
    public const double JoinToleranceMm = 1e-6;

    private CompositeRollProfile(RollProfileSegment[] ordered, ProfileLayout layout)
    {
        Segments = ordered;
        Layout = layout;
    }

    /// <summary>按段拼成辊形。顺接辊形要求段首尾相接，接不上就拒绝。</summary>
    public CompositeRollProfile(IEnumerable<RollProfileSegment> segments, ProfileLayout layout)
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

            if (layout == ProfileLayout.Sequential && i > 0
                && Math.Abs(ordered[i].FromMm - ordered[i - 1].ToMm) > JoinToleranceMm)
            {
                throw new DomainException(
                    $"Sequential profile segment {i + 1} starts at {ordered[i].FromMm} mm, not where segment {i} ends ({ordered[i - 1].ToMm} mm).");
            }
        }

        Segments = ordered;
        Layout = layout;
    }

    /// <summary>按段的次序排列。</summary>
    public IReadOnlyList<RollProfileSegment> Segments { get; }

    public ProfileLayout Layout { get; }

    /// <summary>第一段的起点 Z（mm）。</summary>
    public double StartZMm => Segments[0].FromMm;

    /// <summary>最后一段的终点 Z（mm）。</summary>
    public double EndZMm => Segments[^1].ToMm;

    /// <summary>旧的叠加辊形（读回旧库条目、旧作业快照用）。</summary>
    public static CompositeRollProfile Superimposed(IEnumerable<RollProfileSegment> segments) =>
        new(segments, ProfileLayout.Superimposed);

    /// <summary>顺接辊形：第一段从 <paramref name="startZMm"/> 起，其余段依次接上。</summary>
    public static CompositeRollProfile Sequential(double startZMm, IEnumerable<SequentialSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var built = new List<RollProfileSegment>();
        double z = startZMm;
        foreach (SequentialSegment segment in segments)
        {
            ArgumentNullException.ThrowIfNull(segment);
            built.Add(RollProfileSegment.Create(
                built.Count + 1, segment.ProfileTypeKey, z, z + segment.LengthMm, segment.Parameters, segment.IsMirrored));
            z += segment.LengthMm;
        }

        return new CompositeRollProfile(built, ProfileLayout.Sequential);
    }

    /// <summary>只有一段、铺满全长的辊形。</summary>
    public static CompositeRollProfile Single(string profileTypeKey, RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Sequential(0.0, new[] { new SequentialSegment(profileTypeKey, geometry.BodyLengthMm, parameters) });
    }

    /// <summary>顺接辊形的各段（只有长度）。</summary>
    public IReadOnlyList<SequentialSegment> SequentialSegments() =>
        Segments.Select(SequentialSegment.From).ToArray();

    /// <summary>
    /// 合成整条辊形。每段在自己的区间内按本段长度独立求值：
    /// 叠加辊形逐点相加；顺接辊形每一点取它所在那一段。
    /// </summary>
    public RollProfile Compose(RollGeometry geometry, RollProfileTypeRegistry registry, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(registry);

        Func<double, double>[] contributions = Segments
            .Select(segment => SegmentCurve(segment, geometry, registry, sampleCount))
            .ToArray();

        return RollProfile.Sample(
            geometry.BodyLengthMm,
            sampleCount,
            Layout == ProfileLayout.Superimposed
                ? bodyPositionMm => contributions.Sum(contribution => contribution(bodyPositionMm))
                : bodyPositionMm => SequentialValue(bodyPositionMm, contributions));
    }

    /// <summary>
    /// 顺接辊形的段界：每个相邻两段交界处，前一段终点与后一段起点的值（半径量 mm）。
    /// 编辑器拿它查"段界跳变"。
    /// </summary>
    public IReadOnlyList<ProfileBoundary> Boundaries(RollGeometry geometry, RollProfileTypeRegistry registry, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(registry);

        var boundaries = new List<ProfileBoundary>();
        for (int i = 1; i < Segments.Count; i++)
        {
            RollProfileSegment left = Segments[i - 1];
            RollProfileSegment right = Segments[i];
            boundaries.Add(new ProfileBoundary(
                left.Order,
                right.Order,
                right.FromMm,
                SegmentCurve(left, geometry, registry, sampleCount)(left.ToMm),
                SegmentCurve(right, geometry, registry, sampleCount)(right.FromMm)));
        }

        return boundaries;
    }

    private double SequentialValue(double bodyPositionMm, Func<double, double>[] contributions)
    {
        for (int i = 0; i < Segments.Count; i++)
        {
            RollProfileSegment segment = Segments[i];
            bool last = i == Segments.Count - 1;
            if (bodyPositionMm >= segment.FromMm && (bodyPositionMm < segment.ToMm || (last && bodyPositionMm <= segment.ToMm)))
            {
                return contributions[i](bodyPositionMm);
            }
        }

        return 0.0;
    }

    /// <summary>一段在辊身坐标上的曲线；区间外为 0。</summary>
    private static Func<double, double> SegmentCurve(
        RollProfileSegment segment, RollGeometry geometry, RollProfileTypeRegistry registry, int sampleCount)
    {
        IRollProfileType profileType = registry.Get(segment.ProfileTypeKey);
        RollGeometry segmentGeometry = RollGeometry.Create(segment.LengthMm, geometry.NominalRadiusMm);

        // 段内用该类型自己的曲线，采样点数按段长占比分配，至少两点。
        int segmentSamples = Math.Max(
            2,
            (int)Math.Round(sampleCount * segment.LengthMm / geometry.BodyLengthMm, MidpointRounding.AwayFromZero));
        RollProfile segmentProfile = profileType.CreateProfile(segmentGeometry, segment.Parameters, segmentSamples);

        double fromMm = segment.FromMm;
        double toMm = segment.ToMm;
        double lengthMm = segment.LengthMm;
        bool mirrored = segment.IsMirrored;

        return bodyPositionMm =>
        {
            if (bodyPositionMm < fromMm || bodyPositionMm > toMm)
            {
                return 0.0;
            }

            double withinSegmentMm = bodyPositionMm - fromMm;
            return segmentProfile.RadiusOffsetAtMm(mirrored ? lengthMm - withinSegmentMm : withinSegmentMm);
        };
    }

    /// <summary>加一段，接在最后（顺接辊形从上一段终点起）。</summary>
    public CompositeRollProfile Add(RollProfileSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (Layout == ProfileLayout.Sequential)
        {
            return Rechain(Segments.Append(segment));
        }

        return new CompositeRollProfile(Segments.Append(segment with { Order = Segments.Count + 1 }), Layout);
    }

    /// <summary>在第 <paramref name="afterOrder"/> 段之后插一段（0 = 插在最前）。只用于顺接辊形。</summary>
    public CompositeRollProfile InsertAfter(int afterOrder, SequentialSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        RequireSequential();
        List<SequentialSegment> list = SequentialSegments().ToList();
        list.Insert(Math.Clamp(afterOrder, 0, list.Count), segment);
        return Sequential(StartZMm, list);
    }

    /// <summary>删掉一段并重排顺序；顺接辊形后面的段往前接上。</summary>
    public CompositeRollProfile RemoveAt(int order) =>
        Reorder(Segments.Where(segment => segment.Order != order));

    /// <summary>换掉某一段，顺序不变。顺接辊形里段长变了，后面的段跟着挪。</summary>
    public CompositeRollProfile Replace(int order, RollProfileSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (order < 1 || order > Segments.Count)
        {
            throw new DomainException($"Profile segment {order} does not exist.");
        }

        return Reorder(Segments.Select(existing => existing.Order == order ? segment with { Order = order } : existing));
    }

    /// <summary>顺接辊形换第一段的起点 Z，所有段跟着平移。</summary>
    public CompositeRollProfile WithStartZ(double startZMm)
    {
        RequireSequential();
        return Sequential(startZMm, SequentialSegments());
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
        return Reorder(reordered);
    }

    /// <summary>把一段下移一位。</summary>
    public CompositeRollProfile MoveDown(int order) =>
        order >= 1 && order < Segments.Count ? MoveUp(order + 1) : this;

    /// <summary>按给定次序重新编号；顺接辊形顺带从起点重新接一遍。</summary>
    private CompositeRollProfile Reorder(IEnumerable<RollProfileSegment> ordered) =>
        Layout == ProfileLayout.Sequential
            ? Rechain(ordered)
            : new CompositeRollProfile(ordered.Select((segment, index) => segment with { Order = index + 1 }), Layout);

    private CompositeRollProfile Rechain(IEnumerable<RollProfileSegment> ordered) =>
        Sequential(StartZMm, ordered.Select(SequentialSegment.From));

    private void RequireSequential()
    {
        if (Layout != ProfileLayout.Sequential)
        {
            throw new DomainException("Only sequential profiles are edited by start Z and segment lengths.");
        }
    }
}

/// <summary>顺接辊形的一个段界。</summary>
/// <param name="LeftOrder">前一段。</param>
/// <param name="RightOrder">后一段。</param>
/// <param name="ZMm">段界的 Z（mm）。</param>
/// <param name="LeftRadiusMm">前一段在终点的值（半径量 mm）。</param>
/// <param name="RightRadiusMm">后一段在起点的值（半径量 mm）。</param>
public sealed record ProfileBoundary(int LeftOrder, int RightOrder, double ZMm, double LeftRadiusMm, double RightRadiusMm)
{
    /// <summary>跳变（半径量 mm，后减前）。</summary>
    public double JumpRadiusMm => RightRadiusMm - LeftRadiusMm;
}
