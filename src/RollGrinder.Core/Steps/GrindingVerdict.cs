using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Core.Steps;

/// <summary>合格判定里的一项。</summary>
/// <param name="ItemKey">哪一项（资源键后缀：ProfileError、Roundness……）。</param>
/// <param name="MeasuredMicrometer">实测（µm）；没量到为 null，不参与判定。</param>
/// <param name="AllowedMicrometer">允许（µm）。</param>
public sealed record VerdictItem(string ItemKey, double? MeasuredMicrometer, double AllowedMicrometer)
{
    /// <summary>这一项过了没有；没量到为 null。</summary>
    public bool? Passed => MeasuredMicrometer is double measured ? measured <= AllowedMicrometer + 1e-9 : null;
}

/// <summary>
/// 磨完的结论（关系设计 Q1）：辊形误差 ≤ 辊形公差，且圆度等 ≤ 标定里的验收公差。
/// 一项都没量到时不下结论（<see cref="Passed"/> 为 null）——"没量"不能当"合格"。
/// </summary>
public sealed record GrindingVerdict(IReadOnlyList<VerdictItem> Items)
{
    public const string ProfileError = "ProfileError";
    public const string Roundness = "Roundness";
    public const string Taper = "Taper";

    /// <summary>合格 true / 不合格 false / 没量到 null。</summary>
    public bool? Passed =>
        Items.All(item => item.Passed is null) ? null : Items.All(item => item.Passed != false);

    /// <summary>按实测与公差判定。</summary>
    /// <param name="profileErrorPeakMicrometer">辊形误差峰值（直径量 µm，|实测 − 目标| 的最大值）。</param>
    /// <param name="profileToleranceMicrometer">辊形公差（直径量 µm）。</param>
    /// <param name="roundnessMicrometer">圆度（µm，各截面最差）。</param>
    /// <param name="roundnessToleranceMicrometer">圆度验收公差（µm）。</param>
    /// <param name="taperMicrometer">锥度（两端直径差绝对值，µm）。</param>
    /// <param name="taperToleranceMicrometer">锥度允许（µm）；没设为 null 时不判。</param>
    public static GrindingVerdict Evaluate(
        double? profileErrorPeakMicrometer,
        double profileToleranceMicrometer,
        double? roundnessMicrometer,
        double roundnessToleranceMicrometer,
        double? taperMicrometer = null,
        double? taperToleranceMicrometer = null)
    {
        var items = new List<VerdictItem>
        {
            new(ProfileError, profileErrorPeakMicrometer, profileToleranceMicrometer),
            new(Roundness, roundnessMicrometer, roundnessToleranceMicrometer),
        };
        if (taperToleranceMicrometer is double taperTolerance)
        {
            items.Add(new VerdictItem(Taper, taperMicrometer is double taper ? Math.Abs(taper) : null, taperTolerance));
        }

        return new GrindingVerdict(items);
    }
}
