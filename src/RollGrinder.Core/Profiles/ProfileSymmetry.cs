using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Core.Profiles;

/// <summary>对称编辑展开不了的原因。</summary>
public enum SymmetryFailure
{
    /// <summary>能展开。</summary>
    None = 0,

    /// <summary>一段都没有。</summary>
    NoSegments = 1,

    /// <summary>有不能对称编辑的段（CVC）。</summary>
    UnsupportedType = 2,

    /// <summary>头架端各段（加上中间段的一半）没有正好到辊身中点。</summary>
    DoesNotReachCenter = 3,

    /// <summary>跨在中点上的最后一段曲线本身不对称，不能当中间段。</summary>
    CenterNotSelfSymmetric = 4,
}

/// <summary>对称展开的结果。</summary>
/// <param name="Profile">展开后的整条顺接辊形；展开不了时为 null。</param>
/// <param name="Failure">展开不了的原因。</param>
/// <param name="MissingMm">到中点还差多少 mm（负数是超过了）；只在 <see cref="SymmetryFailure.DoesNotReachCenter"/> 时有意义。</param>
public sealed record SymmetryResult(CompositeRollProfile? Profile, SymmetryFailure Failure, double MissingMm = 0.0);

/// <summary>
/// 对称编辑（阶段 1，问题 Q6 的决定）：只是**编辑辅助**。
///
/// 勾"对称"后只编头架端的段；尾架端由这些段镜像生成。落库的是展开后的整条辊形——
/// 每一段都是独立的段，不存"对称"这个状态。这样求值不用处理对称分支，
/// 也不会出现"改了一端、另一端没跟上"的隐性错误。
///
/// 头架端的段从起点 Z 排到辊身中点：
/// <list type="bullet">
///   <item>正好排到中点：全部段镜像一份接在后面；</item>
///   <item>最后一段跨在中点上、中点正好在它的正中：它是"中间段"，本身要对称（圆柱、凸度），不复制；
///         其余段镜像接在它后面。</item>
/// </list>
/// 镜像 = 次序倒过来、每段的"镜像"标志翻一下。锥度（端部减薄）不看镜像标志：它按所在半边朝向端面，
/// 头架端的在头架端面最低，尾架端的在尾架端面最低。
/// </summary>
public static class ProfileSymmetry
{
    /// <summary>判断是否到中点的容差（mm）。</summary>
    public const double ToleranceMm = 0.001;

    /// <summary>这些段能不能参与对称编辑（没有 CVC 这类不对称的段）。</summary>
    public static bool IsSupported(IEnumerable<string> profileTypeKeys, RollProfileTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(profileTypeKeys);
        ArgumentNullException.ThrowIfNull(registry);
        return profileTypeKeys.All(key => registry.Get(key).SupportsSymmetricEditing);
    }

    /// <summary>把头架端的段展开成整条对称辊形。</summary>
    public static SymmetryResult Expand(
        double designLengthMm,
        double startZMm,
        IReadOnlyList<SequentialSegment> headSide,
        RollProfileTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(headSide);
        ArgumentNullException.ThrowIfNull(registry);

        if (headSide.Count == 0)
        {
            return new SymmetryResult(null, SymmetryFailure.NoSegments);
        }

        if (!IsSupported(headSide.Select(segment => segment.ProfileTypeKey), registry))
        {
            return new SymmetryResult(null, SymmetryFailure.UnsupportedType);
        }

        double centerMm = designLengthMm / 2.0;
        double allEnd = startZMm + headSide.Sum(segment => segment.LengthMm);

        if (Math.Abs(allEnd - centerMm) <= ToleranceMm)
        {
            return Built(startZMm, headSide, headSide, registry);
        }

        SequentialSegment last = headSide[^1];
        double lastMiddle = allEnd - (last.LengthMm / 2.0);
        if (Math.Abs(lastMiddle - centerMm) <= ToleranceMm)
        {
            return registry.Get(last.ProfileTypeKey).IsSelfSymmetric
                ? Built(startZMm, headSide, headSide.Take(headSide.Count - 1).ToArray(), registry)
                : new SymmetryResult(null, SymmetryFailure.CenterNotSelfSymmetric);
        }

        return new SymmetryResult(null, SymmetryFailure.DoesNotReachCenter, centerMm - allEnd);
    }

    /// <summary>
    /// 反过来：整条辊形左右对称的话，取出头架端那一半（含中间段）；不对称返回 null。
    /// 打开一条库里的辊形再勾"对称"时用。
    /// </summary>
    public static IReadOnlyList<SequentialSegment>? TryFold(
        CompositeRollProfile profile, double designLengthMm, RollProfileTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);

        if (profile.Layout != ProfileLayout.Sequential
            || !IsSupported(profile.Segments.Select(segment => segment.ProfileTypeKey), registry)
            || Math.Abs(profile.StartZMm - (designLengthMm - profile.EndZMm)) > ToleranceMm)
        {
            return null;
        }

        IReadOnlyList<SequentialSegment> all = profile.SequentialSegments();
        int count = all.Count;
        for (int i = 0; i < count / 2; i++)
        {
            SequentialSegment head = all[i];
            SequentialSegment tail = all[count - 1 - i];
            if (!IsMirrorOf(head, tail, registry))
            {
                return null;
            }
        }

        IReadOnlyList<SequentialSegment> half = all.Take((count + 1) / 2).ToArray();
        SymmetryResult check = Expand(designLengthMm, profile.StartZMm, half, registry);
        return check.Profile is null ? null : half;
    }

    private static bool IsMirrorOf(SequentialSegment head, SequentialSegment tail, RollProfileTypeRegistry registry) =>
        string.Equals(head.ProfileTypeKey, tail.ProfileTypeKey, StringComparison.Ordinal)
        && Math.Abs(head.LengthMm - tail.LengthMm) <= ToleranceMm
        && head.Parameters.ToOrderedPairs().SequenceEqual(tail.Parameters.ToOrderedPairs())
        && (registry.Get(head.ProfileTypeKey).IsSelfSymmetric
            || registry.Get(head.ProfileTypeKey).IsEndRelief
            || head.IsMirrored != tail.IsMirrored);

    private static SymmetryResult Built(
        double startZMm,
        IReadOnlyList<SequentialSegment> headSide,
        IReadOnlyList<SequentialSegment> mirrored,
        RollProfileTypeRegistry registry)
    {
        // 端部减薄段（锥度）按位置定方向，镜像标志对它无效，展开后照样不设。
        IEnumerable<SequentialSegment> tailSide = mirrored.Reverse().Select(segment => segment with
        {
            IsMirrored = !registry.Get(segment.ProfileTypeKey).IsEndRelief && !segment.IsMirrored,
        });
        return new SymmetryResult(CompositeRollProfile.Sequential(startZMm, headSide.Concat(tailSide)), SymmetryFailure.None);
    }
}
