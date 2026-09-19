using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Records;

/// <summary>
/// 一条磨削记录的查询视图：把记录、作业与辊件拼在一起，界面直接消费。
/// </summary>
/// <param name="RecordId">记录标识。</param>
/// <param name="JobId">作业标识。</param>
/// <param name="RollId">辊件标识。</param>
/// <param name="RollCode">辊号。</param>
/// <param name="ProfileTypeKey">辊形类型键。</param>
/// <param name="StartedAtUtc">开始时刻。</param>
/// <param name="FinishedAtUtc">结束时刻。</param>
/// <param name="State">状态。</param>
/// <param name="Note">备注。</param>
/// <param name="WorstDeviationDiameterMicrometer">最近一次测量的最差偏差（直径量 µm），无测量为空。</param>
public sealed record GrindingRecordView(
    string RecordId,
    string JobId,
    string RollId,
    string RollCode,
    string ProfileTypeKey,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    JobState State,
    string? Note,
    double? WorstDeviationDiameterMicrometer)
{
    /// <summary>磨削时长；未结束为空。</summary>
    public TimeSpan? Duration => FinishedAtUtc is null ? null : FinishedAtUtc.Value - StartedAtUtc;
}

/// <summary>磨削记录的查询、收尾与导出。</summary>
public interface IRecordService
{
    Task<IReadOnlyList<GrindingRecordView>> QueryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>给一条记录收尾。</summary>
    Task FinishAsync(string recordId, JobState state, string? note, CancellationToken cancellationToken);

    /// <summary>导出为 CSV（UTF-8 带 BOM，供 Excel 直接打开）。</summary>
    Task ExportCsvAsync(
        IReadOnlyList<GrindingRecordView> records,
        string filePath,
        CancellationToken cancellationToken);

    /// <summary>按 hmi.json 的保留天数清理过期记录与报警，返回删除条数。</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRecordService"/>
public sealed class RecordService : IRecordService
{
    private readonly IGrindingRecordRepository records;
    private readonly IJobRepository jobs;
    private readonly IRollRepository rolls;
    private readonly IMeasurementRepository measurements;
    private readonly IAlarmRepository alarms;
    private readonly HmiSettings settings;
    private readonly TimeProvider timeProvider;

    public RecordService(
        IGrindingRecordRepository records,
        IJobRepository jobs,
        IRollRepository rolls,
        IMeasurementRepository measurements,
        IAlarmRepository alarms,
        HmiSettings settings,
        TimeProvider timeProvider)
    {
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.measurements = measurements ?? throw new ArgumentNullException(nameof(measurements));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<GrindingRecordView>> QueryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GrindingRecord> found = await this.records
            .QueryAsync(fromUtc, toUtc, limit, cancellationToken).ConfigureAwait(false);

        var views = new List<GrindingRecordView>(found.Count);
        foreach (GrindingRecord record in found)
        {
            (Core.Steps.GrindingJob Job, JobState State)? job = await this.jobs
                .GetAsync(record.JobId, cancellationToken).ConfigureAwait(false);
            RollRecord? roll = job is null
                ? null
                : await this.rolls.GetAsync(job.Value.Job.RollId, cancellationToken).ConfigureAwait(false);

            views.Add(new GrindingRecordView(
                record.RecordId,
                record.JobId,
                job?.Job.RollId ?? string.Empty,
                roll?.Code ?? string.Empty,
                job?.Job.ProfileTypeKey ?? string.Empty,
                record.StartedAtUtc,
                record.FinishedAtUtc,
                record.State,
                record.Note,
                await WorstDeviationAsync(job, cancellationToken).ConfigureAwait(false)));
        }

        return views;
    }

    public Task FinishAsync(string recordId, JobState state, string? note, CancellationToken cancellationToken) =>
        this.records.FinishAsync(recordId, this.timeProvider.GetUtcNow(), state, note, cancellationToken);

    public async Task ExportCsvAsync(
        IReadOnlyList<GrindingRecordView> exported,
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exported);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var text = new StringBuilder();
        text.AppendLine(
            "recordId,jobId,rollId,rollCode,profileType,startedAtUtc,finishedAtUtc,durationSeconds,state,worstDeviationDiameterMicrometer,note");

        foreach (GrindingRecordView view in exported)
        {
            text.Append(Escape(view.RecordId)).Append(',')
                .Append(Escape(view.JobId)).Append(',')
                .Append(Escape(view.RollId)).Append(',')
                .Append(Escape(view.RollCode)).Append(',')
                .Append(Escape(view.ProfileTypeKey)).Append(',')
                .Append(view.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(view.FinishedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(view.Duration?.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(view.State.ToString()).Append(',')
                .Append(view.WorstDeviationDiameterMicrometer?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(Escape(view.Note ?? string.Empty))
                .AppendLine();
        }

        await File.WriteAllTextAsync(filePath, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset threshold = this.timeProvider.GetUtcNow().AddDays(-this.settings.RecordRetentionDays);

        int purgedRecords = await this.records.PurgeOlderThanAsync(threshold, cancellationToken).ConfigureAwait(false);
        int purgedAlarms = await this.alarms.PurgeOlderThanAsync(threshold, cancellationToken).ConfigureAwait(false);

        return purgedRecords + purgedAlarms;
    }

    private async Task<double?> WorstDeviationAsync(
        (Core.Steps.GrindingJob Job, JobState State)? job,
        CancellationToken cancellationToken)
    {
        if (job is null)
        {
            return null;
        }

        MeasurementRecord? measurement = await this.measurements
            .GetLatestByJobAsync(job.Value.Job.JobId, cancellationToken).ConfigureAwait(false);
        if (measurement is null)
        {
            return null;
        }

        // 只用测量值本身相对公称半径的离散程度，避免这里再依赖辊形注册表。
        double maxRadiusMm = double.MinValue;
        double minRadiusMm = double.MaxValue;
        foreach (MeasurementPoint point in measurement.Profile.Points)
        {
            double deviationMm = point.MeasuredRadiusMm - job.Value.Job.Geometry.NominalRadiusMm;
            maxRadiusMm = Math.Max(maxRadiusMm, deviationMm);
            minRadiusMm = Math.Min(minRadiusMm, deviationMm);
        }

        return UnitConversion.RadiusMmToDiameterMicrometer(Math.Max(Math.Abs(maxRadiusMm), Math.Abs(minRadiusMm)));
    }

    private static string Escape(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
}
