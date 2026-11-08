using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Records;

/// <summary>
/// 一道工序的摘要（设计稿 v4 5.3.4 / 5.8.2）：设定与本次的拖板速度、平均倍率、有效速度
/// （"拖板设定 2300 × 平均倍率 93 % ≈ 2140 mm/min"）、折返实际位置的散布、补偿走向、越界几次。
/// </summary>
/// <param name="StepOrder">工序序号。</param>
/// <param name="StepTypeKey">工序类型键（作业里没有这一道为空串）。</param>
/// <param name="PassCount">道次表里这一道工序有几行。</param>
/// <param name="PlanFeedMmPerMin">设定（方案值）拖板速度；不知道为 null。</param>
/// <param name="JobFeedMmPerMin">本次（作业里，含本次调整）拖板速度；没有这个参数为 null。</param>
/// <param name="MeanFeedOverridePercent">进给倍率时间加权平均；没采到为 null。</param>
/// <param name="MinFeedOverridePercent">最低进给倍率。</param>
/// <param name="EffectiveFeedMmPerMin">有效速度 = 本次 × 平均倍率。</param>
/// <param name="ReversalSpreadMm">左右折返实际 Z 的最大散布（mm）；道次表没有为 null。</param>
/// <param name="FirstCompensationMicrometer">这一道工序第一道次的补偿量。</param>
/// <param name="LastCompensationMicrometer">最后一道次的补偿量。</param>
/// <param name="OutOfBoundCount">越界事件几次。</param>
public sealed record StepRunSummary(
    int StepOrder,
    string StepTypeKey,
    int PassCount,
    double? PlanFeedMmPerMin,
    double? JobFeedMmPerMin,
    double? MeanFeedOverridePercent,
    double? MinFeedOverridePercent,
    double? EffectiveFeedMmPerMin,
    double? ReversalSpreadMm,
    double? FirstCompensationMicrometer,
    double? LastCompensationMicrometer,
    int OutOfBoundCount);

/// <summary>一条记录的过程数据。</summary>
/// <param name="Passes">道次表。</param>
/// <param name="Overrides">倍率摘要（按工序、道次、种类）。</param>
/// <param name="Events">事件，按时间。</param>
/// <param name="Nc">下发序号与 NC 确认。</param>
/// <param name="Steps">逐工序摘要。</param>
/// <param name="MissingRanges">缺的道次序号段（"3–5, 9"）；不缺为空串。</param>
public sealed record RunLogView(
    IReadOnlyList<RecordPassRow> Passes,
    IReadOnlyList<OverrideSummaryRow> Overrides,
    IReadOnlyList<RunEvent> Events,
    RecordNcInfo? Nc,
    IReadOnlyList<StepRunSummary> Steps,
    string MissingRanges)
{
    public static RunLogView Empty { get; } = new(
        Array.Empty<RecordPassRow>(), Array.Empty<OverrideSummaryRow>(), Array.Empty<RunEvent>(), null, Array.Empty<StepRunSummary>(), string.Empty);

    public int OutOfBoundCount => Events.Count(item => RunEventKinds.IsOutOfBound(item.Kind));

    /// <summary>这一道次的进给倍率摘要。</summary>
    public OverrideSummaryRow? FeedOverride(int step, int pass) =>
        Overrides.FirstOrDefault(row => row.StepOrder == step && row.Pass == pass && row.Kind == OverrideKinds.Feed);
}

/// <summary>读一条记录的过程数据，并按作业算逐工序摘要与有效值。</summary>
public interface IRunLogService
{
    Task<RunLogView> LoadAsync(string recordId, CancellationToken cancellationToken);

    /// <summary>按已有数据（自动页用正在记的那一份）算逐工序摘要。</summary>
    IReadOnlyList<StepRunSummary> Summarize(
        GrindingJob? job,
        IReadOnlyList<RecordPassRow> passes,
        IReadOnlyList<OverrideSummaryRow> overrides,
        IReadOnlyList<RunEvent> events);

    Task<IReadOnlyDictionary<string, RunEventCounts>> CountEventsAsync(IReadOnlyList<string> recordIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRunLogService"/>
public sealed class RunLogService : IRunLogService
{
    private readonly IRunLogRepository runLog;
    private readonly IGrindingRecordRepository records;
    private readonly IJobRepository jobs;

    public RunLogService(IRunLogRepository runLog, IGrindingRecordRepository records, IJobRepository jobs)
    {
        this.runLog = runLog ?? throw new ArgumentNullException(nameof(runLog));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
    }

    public async Task<RunLogView> LoadAsync(string recordId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        IReadOnlyList<RecordPassRow> passes = await this.runLog.ListPassesAsync(recordId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OverrideSummaryRow> overrides = await this.runLog.ListOverridesAsync(recordId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RunEvent> events = await this.runLog.ListEventsAsync(recordId, cancellationToken).ConfigureAwait(false);
        RecordNcInfo? nc = await this.runLog.GetNcInfoAsync(recordId, cancellationToken).ConfigureAwait(false);
        GrindingJob? job = null;
        if (await this.records.GetAsync(recordId, cancellationToken).ConfigureAwait(false) is { } record)
        {
            job = (await this.jobs.GetAsync(record.JobId, cancellationToken).ConfigureAwait(false))?.Job;
        }

        string missing = string.Join(", ", events
            .Where(item => item.Kind == RunEventKinds.PassMissing && !string.IsNullOrEmpty(item.Detail))
            .Select(item => item.Detail));
        return new RunLogView(passes, overrides, events, nc, Summarize(job, passes, overrides, events), missing);
    }

    public Task<IReadOnlyDictionary<string, RunEventCounts>> CountEventsAsync(IReadOnlyList<string> recordIds, CancellationToken cancellationToken) =>
        this.runLog.CountEventsAsync(recordIds, cancellationToken);

    public IReadOnlyList<StepRunSummary> Summarize(
        GrindingJob? job,
        IReadOnlyList<RecordPassRow> passes,
        IReadOnlyList<OverrideSummaryRow> overrides,
        IReadOnlyList<RunEvent> events)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(events);

        IEnumerable<int> orders = passes.Select(row => row.StepOrder)
            .Concat(overrides.Select(row => row.StepOrder))
            .Where(order => order > 0)
            .Distinct()
            .OrderBy(order => order);
        var list = new List<StepRunSummary>();
        foreach (int order in orders)
        {
            GrindingJobStep? step = job?.Steps.FirstOrDefault(item => item.Order == order);
            double? jobFeed = step is not null && step.Parameters.TryGet(StepParameterKeys.FeedMmPerMin, out ParameterValue? feed)
                && feed is { Kind: ParameterValueKind.Number }
                ? feed.Number
                : null;
            JobAdjustment? adjusted = job?.Adjustments.LastOrDefault(
                item => item.StepOrder == order && item.ParameterKey == StepParameterKeys.FeedMmPerMin);
            double? planFeed = adjusted?.PlanValue ?? jobFeed;

            OverrideSummaryRow[] feedRows = overrides
                .Where(row => row.StepOrder == order && row.Kind == OverrideKinds.Feed && row.Seconds > 0.0)
                .ToArray();
            double seconds = feedRows.Sum(row => row.Seconds);
            double? meanOverride = seconds > 0.0 ? feedRows.Sum(row => row.MeanPercent * row.Seconds) / seconds : null;
            double? minOverride = feedRows.Length > 0 ? feedRows.Min(row => row.MinPercent) : null;

            RecordPassRow[] rows = passes.Where(row => row.StepOrder == order).OrderBy(row => row.Sequence).ToArray();
            double? spread = Spread(rows.Select(row => row.LeftZMm)) is double left
                ? Math.Max(left, Spread(rows.Select(row => row.RightZMm)) ?? 0.0)
                : Spread(rows.Select(row => row.RightZMm));

            list.Add(new StepRunSummary(
                order,
                step?.StepTypeKey ?? string.Empty,
                rows.Length,
                planFeed,
                jobFeed,
                meanOverride,
                minOverride,
                jobFeed is double f && meanOverride is double m ? f * m / 100.0 : null,
                spread,
                rows.FirstOrDefault(row => row.CompensationMicrometer is not null)?.CompensationMicrometer,
                rows.LastOrDefault(row => row.CompensationMicrometer is not null)?.CompensationMicrometer,
                events.Count(item => item.StepOrder == order && RunEventKinds.IsOutOfBound(item.Kind))));
        }

        return list;
    }

    private static double? Spread(IEnumerable<double?> values)
    {
        double[] known = values.Where(value => value is double v && double.IsFinite(v)).Select(value => value!.Value).ToArray();
        return known.Length < 2 ? null : known.Max() - known.Min();
    }
}
