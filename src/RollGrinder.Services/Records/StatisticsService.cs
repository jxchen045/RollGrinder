using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Records;

/// <summary>按某个键数了几次（原因、人、参数）。</summary>
public sealed record CountRow(string Key, int Count);

/// <summary>一支辊这段时间磨了几次、磨掉多少（直径 mm）。</summary>
public sealed record RollRemovalRow(string RollId, int Grinds, double RemovedMm);

/// <summary>
/// 例外与变更统计（关系设计 M1、设计稿 v4 5.8"统计"）：这段时间的仅本次例外（按原因、按人）、
/// 改计划（按人）、本次调整（按人、按参数）、每支辊去除量。
/// </summary>
public sealed record ShopStatistics(
    int RecordCount,
    IReadOnlyList<CountRow> ExceptionsByReason,
    IReadOnlyList<CountRow> ExceptionsByPerson,
    IReadOnlyList<CountRow> PlanChangesByPerson,
    IReadOnlyList<CountRow> AdjustmentsByPerson,
    IReadOnlyList<CountRow> AdjustmentsByParameter,
    IReadOnlyList<RollRemovalRow> Removal);

/// <summary>例外与变更统计。</summary>
public interface IStatisticsService
{
    Task<ShopStatistics> ComputeAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IStatisticsService"/>
public sealed class StatisticsService : IStatisticsService
{
    /// <summary>一次最多统计多少条记录。</summary>
    public const int Limit = 5000;

    private readonly IGrindingRecordRepository records;
    private readonly IJobRepository jobs;
    private readonly IChangeLogRepository changeLog;

    public StatisticsService(IGrindingRecordRepository records, IJobRepository jobs, IChangeLogRepository changeLog)
    {
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.changeLog = changeLog ?? throw new ArgumentNullException(nameof(changeLog));
    }

    public async Task<ShopStatistics> ComputeAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        IReadOnlyList<GrindingRecord> found = await this.records.QueryAsync(fromUtc, toUtc, Limit, cancellationToken).ConfigureAwait(false);
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var exceptionPeople = new Dictionary<string, int>(StringComparer.Ordinal);
        var adjustPeople = new Dictionary<string, int>(StringComparer.Ordinal);
        var adjustParameters = new Dictionary<string, int>(StringComparer.Ordinal);
        var removal = new Dictionary<string, (int Grinds, double Removed)>(StringComparer.Ordinal);
        var seenJobs = new HashSet<string>(StringComparer.Ordinal);

        foreach (GrindingRecord record in found)
        {
            // 一份作业可能有几条记录（中断续磨）：按作业算一次。
            if (!seenJobs.Add(record.JobId) || (await this.jobs.GetAsync(record.JobId, cancellationToken).ConfigureAwait(false))?.Job is not { } job)
            {
                continue;
            }

            if (job.Deviation == JobDeviation.ThisTimeOnly)
            {
                Add(reasons, string.IsNullOrWhiteSpace(job.DeviationReason) ? "--" : job.DeviationReason!);
                Add(exceptionPeople, await OperatorAsync(record.RecordId, cancellationToken).ConfigureAwait(false));
            }

            foreach (JobAdjustment adjustment in job.Adjustments.Where(item => !item.IsPending))
            {
                Add(adjustPeople, string.IsNullOrWhiteSpace(adjustment.ChangedBy) ? "--" : adjustment.ChangedBy);
                Add(adjustParameters, adjustment.ParameterKey);
            }

            (int grinds, double removed) = removal.TryGetValue(job.RollId, out var sum) ? sum : (0, 0.0);
            removal[job.RollId] = (grinds + 1, removed + ((job.StockMicrometer ?? 0.0) / 1000.0));
        }

        var planPeople = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ChangeLogEntry entry in await this.changeLog.ListAsync(Limit, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Area == ChangeLogAreas.RollPlan && entry.ChangedAtUtc >= fromUtc && entry.ChangedAtUtc < toUtc)
            {
                Add(planPeople, string.IsNullOrWhiteSpace(entry.ChangedBy) ? "--" : entry.ChangedBy);
            }
        }

        return new ShopStatistics(
            seenJobs.Count,
            Sorted(reasons),
            Sorted(exceptionPeople),
            Sorted(planPeople),
            Sorted(adjustPeople),
            Sorted(adjustParameters),
            removal
                .Select(pair => new RollRemovalRow(pair.Key, pair.Value.Grinds, pair.Value.Removed))
                .OrderByDescending(row => row.RemovedMm)
                .ToArray());
    }

    private async Task<string> OperatorAsync(string recordId, CancellationToken cancellationToken)
    {
        string? json = await this.records.GetSnapshotAsync(recordId, cancellationToken).ConfigureAwait(false);
        try
        {
            return json is not null && JsonNode.Parse(json)?["operator"]?.GetValue<string>() is { Length: > 0 } name ? name : "--";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return "--";
        }
    }

    private static void Add(Dictionary<string, int> counts, string key) => counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;

    private static IReadOnlyList<CountRow> Sorted(Dictionary<string, int> counts) =>
        counts.Select(pair => new CountRow(pair.Key, pair.Value)).OrderByDescending(row => row.Count).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
}
