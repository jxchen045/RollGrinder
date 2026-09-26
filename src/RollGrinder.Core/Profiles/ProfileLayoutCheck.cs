using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Profiles;

/// <summary>辊形排布上的一类问题。</summary>
public enum ProfileIssueKind
{
    /// <summary>一段都没有。</summary>
    NoSegments = 0,

    /// <summary>某段的区间伸到了辊身外。</summary>
    OutsideBody = 1,

    /// <summary>辊身上有一截没有任何段覆盖（断开）。</summary>
    NotCovered = 2,

    /// <summary>两段区间重叠。现在的辊形是逐段叠加的，重叠处两段偏差相加——这是提示，不是错误。</summary>
    Overlap = 3,

    /// <summary>某段的参数不成立（越界、缺失……），详见 <see cref="ProfileIssue.Violation"/>。</summary>
    ParameterInvalid = 4,

    /// <summary>顺接辊形相邻两段在段界上对不上（跳变见 <see cref="ProfileIssue.JumpMicrometer"/>）。</summary>
    BoundaryJump = 5,
}

/// <summary>辊形排布上的一条问题。</summary>
/// <param name="Kind">问题种类。</param>
/// <param name="IsError">是错误（不能保存）还是提示。</param>
/// <param name="SegmentOrder">涉及的段（从 1 起）；与整条辊形有关时为空。</param>
/// <param name="OtherSegmentOrder">重叠时的另一段。</param>
/// <param name="FromMm">问题所在区间起点（辊身坐标 mm）。</param>
/// <param name="ToMm">问题所在区间终点（辊身坐标 mm）。</param>
/// <param name="Violation">参数不成立时的具体原因。</param>
/// <param name="JumpMicrometer">段界跳变（直径量 µm，后一段减前一段）。</param>
public sealed record ProfileIssue(
    ProfileIssueKind Kind,
    bool IsError,
    int? SegmentOrder = null,
    int? OtherSegmentOrder = null,
    double FromMm = 0.0,
    double ToMm = 0.0,
    ParameterViolation? Violation = null,
    double JumpMicrometer = 0.0);

/// <summary>
/// 辊形编辑器每改一次就跑一遍的排布检查（第一轮甲方测试：辊形 1⑥⑨）。
///
/// 甲方那条辊形的设计长度是 300 mm，曲线段却填到了 100–800 mm，预览按 300 mm 画，
/// 和输入对不上，界面上也没有任何提示。这里把这类问题逐条找出来：
/// 段伸出辊身、辊身有一截没被覆盖（段长合计不等于设计长度）、参数越界、
/// 顺接辊形段界跳变都是错误，有错不能保存；
/// 旧的叠加辊形里两段重叠是正常用法（主辊形上再叠端部锥度），只作提示。
/// </summary>
public static class ProfileLayoutCheck
{
    /// <summary>小于这个长度（mm）的出界、断开、重叠当作数值误差，不报。</summary>
    public const double ToleranceMm = 0.01;

    /// <summary>顺接辊形段界上允许的跳变（直径量 µm）。再大就是一个台阶，磨不出来。</summary>
    public const double MaxBoundaryJumpMicrometer = 1.0;

    /// <summary>检查一条辊形；<paramref name="profile"/> 为 null 表示一段都没有。</summary>
    public static IReadOnlyList<ProfileIssue> Check(
        CompositeRollProfile? profile,
        double bodyLengthMm,
        RollProfileTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var issues = new List<ProfileIssue>();
        if (profile is null)
        {
            issues.Add(new ProfileIssue(ProfileIssueKind.NoSegments, IsError: true));
            return issues;
        }

        IReadOnlyList<RollProfileSegment> segments = profile.Segments;

        foreach (RollProfileSegment segment in segments)
        {
            if (segment.FromMm < -ToleranceMm || segment.ToMm > bodyLengthMm + ToleranceMm)
            {
                issues.Add(new ProfileIssue(
                    ProfileIssueKind.OutsideBody, true, segment.Order, null, segment.FromMm, segment.ToMm));
            }

            if (registry.TryGet(segment.ProfileTypeKey, out IRollProfileType? profileType) && profileType is not null)
            {
                foreach (ParameterViolation violation in profileType.Schema.Validate(segment.Parameters).Violations)
                {
                    issues.Add(new ProfileIssue(
                        ProfileIssueKind.ParameterInvalid, true, segment.Order, null, segment.FromMm, segment.ToMm, violation));
                }
            }
        }

        issues.AddRange(FindUncovered(segments, bodyLengthMm));
        if (profile.Layout == ProfileLayout.Superimposed)
        {
            issues.AddRange(FindOverlaps(segments));
        }
        else
        {
            issues.AddRange(FindJumps(profile, bodyLengthMm, registry));
        }

        return issues;
    }

    private static IEnumerable<ProfileIssue> FindJumps(
        CompositeRollProfile profile, double bodyLengthMm, RollProfileTypeRegistry registry)
    {
        // 段界跳变只看两段曲线在交界处的值，几何里的直径不参与；给一个占位直径即可。
        RollGeometry geometry = RollGeometry.Create(Math.Max(bodyLengthMm, profile.EndZMm), 1.0);
        foreach (ProfileBoundary boundary in profile.Boundaries(geometry, registry, 201))
        {
            double jumpMicrometer = UnitConversion.RadiusMmToDiameterMicrometer(boundary.JumpRadiusMm);
            if (Math.Abs(jumpMicrometer) > MaxBoundaryJumpMicrometer)
            {
                yield return new ProfileIssue(
                    ProfileIssueKind.BoundaryJump, true, boundary.RightOrder, boundary.LeftOrder,
                    boundary.ZMm, boundary.ZMm, null, jumpMicrometer);
            }
        }
    }

    /// <summary>辊身上没被任何段盖住的地方。</summary>
    private static IEnumerable<ProfileIssue> FindUncovered(IReadOnlyList<RollProfileSegment> segments, double bodyLengthMm)
    {
        double coveredTo = 0.0;
        foreach (RollProfileSegment segment in segments.OrderBy(s => s.FromMm))
        {
            double from = Math.Max(0.0, segment.FromMm);
            if (from - coveredTo > ToleranceMm && coveredTo < bodyLengthMm)
            {
                yield return new ProfileIssue(
                    ProfileIssueKind.NotCovered, true, null, null, coveredTo, Math.Min(from, bodyLengthMm));
            }

            coveredTo = Math.Max(coveredTo, Math.Min(segment.ToMm, bodyLengthMm));
        }

        if (bodyLengthMm - coveredTo > ToleranceMm)
        {
            yield return new ProfileIssue(ProfileIssueKind.NotCovered, true, null, null, coveredTo, bodyLengthMm);
        }
    }

    private static IEnumerable<ProfileIssue> FindOverlaps(IReadOnlyList<RollProfileSegment> segments)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            for (int j = i + 1; j < segments.Count; j++)
            {
                double from = Math.Max(segments[i].FromMm, segments[j].FromMm);
                double to = Math.Min(segments[i].ToMm, segments[j].ToMm);
                if (to - from > ToleranceMm)
                {
                    yield return new ProfileIssue(
                        ProfileIssueKind.Overlap, false, segments[j].Order, segments[i].Order, from, to);
                }
            }
        }
    }
}
