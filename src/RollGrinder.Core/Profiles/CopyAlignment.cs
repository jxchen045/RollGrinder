using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Profiles;

/// <summary>把一条辊形复制到名义长度不同的辊上，怎么套的（设计说明 4.5"复制"一列）。</summary>
public enum CopyAlignmentKind
{
    /// <summary>名义长度差在复制门槛以内：视为相同，直接复制（差的那一点按中心对齐吸收）。</summary>
    Same = 0,

    /// <summary>凸度、锥度、圆柱等解析段：两端段保持绝对长度，中间段按新长度铺满，形状参数不变。须确认。</summary>
    MiddleFilled = 1,

    /// <summary>CVC / 点表，本辊更短：以中心为基准截掉两端多出的部分（精确）。</summary>
    Trimmed = 2,

    /// <summary>CVC / 点表，本辊稍长（容差内）：两端按切线延伸。</summary>
    Extended = 3,

    /// <summary>CVC / 点表，本辊长出超过容差：多项式外推会发散，不套用——请在本辊上重新输入或导入。</summary>
    Refused = 4,
}

/// <summary>复制对齐的结论（设计稿 v4 5.5.5 P9：两边名义长度、差值、方式、每端截 / 延、判定）。</summary>
/// <param name="Kind">怎么套的。</param>
/// <param name="SourceLengthMm">来源的名义长度。</param>
/// <param name="TargetLengthMm">本辊的名义长度。</param>
/// <param name="NeedsConfirm">要看过叠图确认才复制（解析段中间铺满；截取 / 延伸超出端部非工作区）。</param>
/// <param name="HeadZoneMm">来源辊形头架侧的端部非工作区。</param>
/// <param name="TailZoneMm">来源辊形尾架侧的端部非工作区。</param>
/// <param name="SegmentsBefore">来源各段长度（段表对照）。</param>
/// <param name="SegmentsAfter">套好后各段长度；拒绝时为空。</param>
/// <param name="Profile">套好的辊形（辊身坐标 0 … 本辊名义长度）；拒绝时为 null。</param>
public sealed record CopyAlignment(
    CopyAlignmentKind Kind,
    double SourceLengthMm,
    double TargetLengthMm,
    bool NeedsConfirm,
    double HeadZoneMm,
    double TailZoneMm,
    IReadOnlyList<double> SegmentsBefore,
    IReadOnlyList<double> SegmentsAfter,
    CompositeRollProfile? Profile)
{
    /// <summary>本辊减来源（mm）：负数是本辊更短。</summary>
    public double DifferenceMm => TargetLengthMm - SourceLengthMm;

    /// <summary>每端截（负）或延（正）多少 mm；中间铺满时为 0（两端不动）。</summary>
    public double PerEndMm => Kind is CopyAlignmentKind.Trimmed or CopyAlignmentKind.Extended ? DifferenceMm / 2.0 : 0.0;

    /// <summary>可以复制（拒绝以外）。</summary>
    public bool CanCopy => Kind != CopyAlignmentKind.Refused && Profile is not null;
}

/// <summary>
/// 复制方案时的长度对齐（设计说明 4.5）：以辊身中心为基准，不做等比伸缩。
/// <list type="bullet">
/// <item>差值不超过复制门槛（默认 1 mm）视为相同，不提示；</item>
/// <item>凸度、锥度、圆柱等解析段：两端段保持绝对长度，中间段按新长度铺满（形状参数不变），预览确认后套用；</item>
/// <item>CVC、点表：更短以中心截掉两端；稍长（容差内）按切线延伸；超出容差拒绝；截 / 延超出端部非工作区要确认。</item>
/// </list>
/// </summary>
public static class ProfileCopyAlignment
{
    /// <summary>默认复制门槛（mm）。</summary>
    public const double DefaultThresholdMm = 1.0;

    /// <summary>形状随长度走、只能截取 / 延伸的段类型（曲线本来按轧机坐标定义）。</summary>
    public static bool IsCurveDefined(CompositeRollProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Layout != ProfileLayout.Sequential
            || profile.Segments.Any(segment => segment.ProfileTypeKey is ProfileTypeKeys.Cvc or ProfileTypeKeys.PointTable);
    }

    /// <summary>对齐。</summary>
    /// <param name="profile">来源辊形（辊身坐标 0 … 来源名义长度）。</param>
    /// <param name="sourceLengthMm">来源名义长度。</param>
    /// <param name="targetLengthMm">本辊名义长度。</param>
    /// <param name="thresholdMm">复制门槛：差这么多以内视为相同。</param>
    /// <param name="toleranceMm">CVC / 点表延伸的上限（下发容差）。</param>
    /// <param name="registry">曲线类型。</param>
    /// <param name="sampleCount">采样点数。</param>
    public static CopyAlignment Align(
        CompositeRollProfile profile,
        double sourceLengthMm,
        double targetLengthMm,
        double thresholdMm,
        double toleranceMm,
        RollProfileTypeRegistry registry,
        int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);
        if (sourceLengthMm <= 0.0 || targetLengthMm <= 0.0)
        {
            throw new DomainException("Source and target lengths must be positive.");
        }

        double[] before = profile.Segments.Select(segment => segment.LengthMm).ToArray();
        (double head, double tail) = ProfileLengthAlignment.DefaultEndZones(profile);
        double difference = targetLengthMm - sourceLengthMm;
        if (Math.Abs(difference) <= ProfileLengthAlignment.ExactToleranceMm)
        {
            return new CopyAlignment(CopyAlignmentKind.Same, sourceLengthMm, targetLengthMm, false, head, tail, before, before, profile);
        }

        bool same = Math.Abs(difference) <= thresholdMm + 1e-9;
        if (!IsCurveDefined(profile))
        {
            CompositeRollProfile? filled = FillMiddle(profile, sourceLengthMm, targetLengthMm);
            if (filled is not null)
            {
                return new CopyAlignment(
                    same ? CopyAlignmentKind.Same : CopyAlignmentKind.MiddleFilled, sourceLengthMm, targetLengthMm, !same, head, tail,
                    before, filled.Segments.Select(segment => segment.LengthMm).ToArray(), filled);
            }
        }

        // CVC / 点表（或中间段铺不下的解析辊形）：以中心截取 / 切线延伸。更短一律能截；更长只许在容差内。
        if (difference > toleranceMm + 1e-9 && !same)
        {
            return new CopyAlignment(CopyAlignmentKind.Refused, sourceLengthMm, targetLengthMm, true, head, tail, before, Array.Empty<double>(), null);
        }

        LengthAlignment aligned = ProfileLengthAlignment.Align(
            profile, sourceLengthMm, targetLengthMm, double.PositiveInfinity, double.MaxValue / 4.0, registry, sampleCount);
        double perEnd = Math.Abs(difference) / 2.0;
        bool beyondZone = perEnd > head + 1e-9 || perEnd > tail + 1e-9;
        CopyAlignmentKind kind = same ? CopyAlignmentKind.Same : difference < 0.0 ? CopyAlignmentKind.Trimmed : CopyAlignmentKind.Extended;
        return new CopyAlignment(
            kind, sourceLengthMm, targetLengthMm, !same && beyondZone, head, tail,
            before, aligned.Profile!.Segments.Select(segment => segment.LengthMm).ToArray(), aligned.Profile);
    }

    /// <summary>
    /// 两端段保持绝对长度，中间段按原比例分掉长度差（形状参数不变）。只有一段时这一段就是中间段。
    /// 起点不在 0、没铺满名义长度、中间段被压到 0 以下时返回 null。
    /// </summary>
    private static CompositeRollProfile? FillMiddle(CompositeRollProfile profile, double sourceLengthMm, double targetLengthMm)
    {
        if (profile.Layout != ProfileLayout.Sequential
            || Math.Abs(profile.StartZMm) > CompositeRollProfile.JoinToleranceMm
            || Math.Abs(profile.EndZMm - sourceLengthMm) > 1e-6)
        {
            return null;
        }

        SequentialSegment[] parts = profile.SequentialSegments().ToArray();
        int first = parts.Length > 2 ? 1 : 0;
        int last = parts.Length > 2 ? parts.Length - 2 : parts.Length - 1;
        if (parts.Length == 2)
        {
            // 两段：没有"中间"，让长的那段（主辊形）吸收。
            first = last = parts[0].LengthMm >= parts[1].LengthMm ? 0 : 1;
        }

        double middle = parts.Skip(first).Take(last - first + 1).Sum(part => part.LengthMm);
        double newMiddle = middle + (targetLengthMm - sourceLengthMm);
        if (middle <= 0.0 || newMiddle <= 0.0)
        {
            return null;
        }

        double ratio = newMiddle / middle;
        for (int i = first; i <= last; i++)
        {
            // 只改段长；凸度量、锥度量等形状参数原样（点表不会走到这里）。
            parts[i] = parts[i] with { LengthMm = parts[i].LengthMm * ratio };
        }

        return CompositeRollProfile.Sequential(0.0, parts);
    }
}
