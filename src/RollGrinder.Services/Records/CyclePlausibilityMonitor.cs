using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Steps;

namespace RollGrinder.Services.Records;

/// <summary>一支辊磨完时用时合不合理。</summary>
/// <param name="IsImplausible">用时不合理（快得不可能）。</param>
/// <param name="Estimated">下发时的预计用时（机床时间）。</param>
/// <param name="Actual">实际用时（已换算成机床时间）。</param>
public sealed record CyclePlausibility(bool IsImplausible, TimeSpan Estimated, TimeSpan Actual);

/// <summary>
/// 盯住"磨得快得不可能"这种情况。
///
/// 第一轮甲方测试里，一支 9 道工序、预计一个多小时的辊 3 秒就报"循环正常结束"，
/// 界面上毫无提示——下发写错了进给（F0），机床那头当然"磨"得飞快。
/// 这里在下发时记下预计用时，磨完时拿实际用时比一下：不到预计的
/// <see cref="MinimumRatio"/> 就报警。只报警，不改记录、不碰机床。
///
/// 慢得多不报：暂停、修整、人工测量都会让实际时间变长，那是正常的。
/// 上位机中途重启过就没有预计值可比，这一支不判。
/// </summary>
public sealed class CyclePlausibilityMonitor
{
    /// <summary>实际用时低于预计的这个比例就算不合理。</summary>
    public const double MinimumRatio = 0.25;

    /// <summary>预计用时不到这么长的作业不判：太短的估算本身误差就大。</summary>
    public static readonly TimeSpan MinimumEstimate = TimeSpan.FromMinutes(2.0);

    private readonly double timeScale;
    private readonly object gate = new();
    private readonly Dictionary<string, (TimeSpan Estimate, DateTimeOffset HandedAtUtc)> handovers = new(StringComparer.Ordinal);

    public CyclePlausibilityMonitor(MachineTimeScale scale)
    {
        ArgumentNullException.ThrowIfNull(scale);
        if (!(scale.Factor > 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "The machine time scale must be positive.");
        }

        this.timeScale = scale.Factor;
    }

    /// <summary>整支作业的预计用时：各道工序估算相加。</summary>
    public static TimeSpan Estimate(IEnumerable<GrindingStepPlan> plans, RollGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(geometry);
        return plans.Aggregate(TimeSpan.Zero, (sum, plan) => sum + plan.EstimateDuration(geometry));
    }

    /// <summary>下发成功时记下这支辊的预计用时。同一作业再下发一次就重新计时。</summary>
    public void OnHandedOver(string jobId, TimeSpan estimate, DateTimeOffset handedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (this.gate)
        {
            this.handovers[jobId] = (estimate, handedAtUtc);
        }
    }

    /// <summary>磨完时判一次。没有这支辊的下发记录（上位机重启过）就返回 null。</summary>
    public CyclePlausibility? OnCompleted(string jobId, DateTimeOffset finishedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        (TimeSpan Estimate, DateTimeOffset HandedAtUtc) handover;
        lock (this.gate)
        {
            if (!this.handovers.Remove(jobId, out handover))
            {
                return null;
            }
        }

        TimeSpan actual = TimeSpan.FromTicks((long)((finishedAtUtc - handover.HandedAtUtc).Ticks * this.timeScale));
        bool implausible = handover.Estimate >= MinimumEstimate
            && actual.Ticks < handover.Estimate.Ticks * MinimumRatio;
        return new CyclePlausibility(implausible, handover.Estimate, actual);
    }
}
