using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;

using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Core.Steps;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Calibration;
using RollGrinder.Services.Monitoring;

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

    /// <summary>
    /// 一条记录的结果指标（设计稿那 12 项）。记录不存在时返回
    /// <see cref="GrindingOutcome.Empty"/>——界面上一排 "--"，而不是一屏错误。
    /// </summary>
    Task<GrindingOutcome> LoadOutcomeAsync(string recordId, CancellationToken cancellationToken);

    /// <summary>
    /// 一条记录的某一张曲线。没有数据时返回空曲线，界面照实说，
    /// 不画一条编出来的线。
    /// </summary>
    Task<RecordCurve> LoadCurveAsync(
        string recordId, RecordCurveKind kind, CancellationToken cancellationToken);

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
    /// <summary>报表出不来的资源键。记录已经收尾了，只是没打出来。</summary>
    public const string ReportNotPrintedResourceKey = "Alarm_ReportNotPrinted";

    /// <summary>圆度没存下来的资源键。</summary>
    public const string RoundnessNotArchivedResourceKey = "Alarm_RoundnessNotArchived";

    /// <summary>圆度记录的来源标记：沿辊身收来的，不是人工量的。</summary>
    public const string RoundnessSource = "trace";

    private readonly IGrindingRecordRepository records;
    private readonly IJobRepository jobs;
    private readonly IRollRepository rolls;
    private readonly IMeasurementRepository measurements;
    private readonly IAlarmRepository alarms;
    private readonly IRoundnessRepository roundness;
    private readonly ICompensationRepository compensations;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly ISurfaceTraceService traces;
    private readonly ICalibrationService calibration;
    private readonly IReportService reports;
    private readonly IReportPrintQueue printQueue;
    private readonly IAlarmSink alarmSink;
    private readonly HmiSettings settings;
    private readonly TimeProvider timeProvider;

    public RecordService(
        IGrindingRecordRepository records,
        IJobRepository jobs,
        IRollRepository rolls,
        IMeasurementRepository measurements,
        IAlarmRepository alarms,
        IRoundnessRepository roundness,
        ICompensationRepository compensations,
        RollProfileTypeRegistry profileTypes,
        ISurfaceTraceService traces,
        ICalibrationService calibration,
        IReportService reports,
        IReportPrintQueue printQueue,
        IAlarmSink alarmSink,
        HmiSettings settings,
        TimeProvider timeProvider)
    {
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.measurements = measurements ?? throw new ArgumentNullException(nameof(measurements));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.roundness = roundness ?? throw new ArgumentNullException(nameof(roundness));
        this.compensations = compensations ?? throw new ArgumentNullException(nameof(compensations));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.traces = traces ?? throw new ArgumentNullException(nameof(traces));
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.reports = reports ?? throw new ArgumentNullException(nameof(reports));
        this.printQueue = printQueue ?? throw new ArgumentNullException(nameof(printQueue));
        this.alarmSink = alarmSink ?? throw new ArgumentNullException(nameof(alarmSink));
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

    public async Task<GrindingOutcome> LoadOutcomeAsync(string recordId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        GrindingRecord? record = await this.records.GetAsync(recordId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return GrindingOutcome.Empty;
        }

        (Core.Steps.GrindingJob Job, JobState State)? stored = await this.jobs
            .GetAsync(record.JobId, cancellationToken).ConfigureAwait(false);

        MeasurementRecord? preGrind = await this.measurements
            .GetLatestByStageAsync(record.JobId, MeasurementStage.PreGrind, cancellationToken).ConfigureAwait(false);
        MeasurementRecord? postGrind = await this.measurements
            .GetLatestByStageAsync(record.JobId, MeasurementStage.PostGrind, cancellationToken).ConfigureAwait(false);
        RoundnessMeasurement? roundnessMeasurement = await this.roundness
            .GetLatestByJobAsync(record.JobId, cancellationToken).ConfigureAwait(false);

        // 磨后测量可能还没分阶段存过（旧记录），退回到"最近一次"。
        postGrind ??= await this.measurements
            .GetLatestByJobAsync(record.JobId, cancellationToken).ConfigureAwait(false);

        Core.Geometry.RollProfile? target = stored is null
            ? null
            : stored.Value.Job.Profile.Compose(
                stored.Value.Job.Geometry, this.profileTypes, this.settings.ProfileSampleCount);

        return GrindingOutcome.Create(
            record,
            stored?.Job.Geometry,
            preGrind,
            postGrind,
            roundnessMeasurement,
            target,
            await this.compensations.CountByJobAsync(record.JobId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<RecordCurve> LoadCurveAsync(
        string recordId, RecordCurveKind kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        GrindingRecord? record = await this.records.GetAsync(recordId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return RecordCurve.Empty(kind);
        }

        return kind switch
        {
            RecordCurveKind.BeforeAfterProfile =>
                await BeforeAfterCurveAsync(record.JobId, cancellationToken).ConfigureAwait(false),
            RecordCurveKind.Deviation =>
                await DeviationCurveAsync(record.JobId, cancellationToken).ConfigureAwait(false),
            RecordCurveKind.Roundness =>
                await RoundnessCurveAsync(record.JobId, cancellationToken).ConfigureAwait(false),
            RecordCurveKind.CompensationConvergence =>
                await ConvergenceCurveAsync(record.JobId, cancellationToken).ConfigureAwait(false),
            _ => RecordCurve.Empty(kind),
        };
    }

    /// <summary>
    /// 磨前 / 磨后辊形：两条线叠着画。
    ///
    /// 纵坐标是**相对公称半径的偏差**（直径量 µm）而不是绝对直径——
    /// 两条相差不到一毫米的线画在 800 mm 的量程上，肉眼看就是重合的。
    /// </summary>
    private async Task<RecordCurve> BeforeAfterCurveAsync(string jobId, CancellationToken cancellationToken)
    {
        (Core.Steps.GrindingJob Job, JobState State)? stored = await this.jobs
            .GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return RecordCurve.Empty(RecordCurveKind.BeforeAfterProfile);
        }

        double nominalRadiusMm = stored.Value.Job.Geometry.NominalRadiusMm;
        var series = new List<RecordCurveSeries>();

        MeasurementRecord? pre = await this.measurements
            .GetLatestByStageAsync(jobId, MeasurementStage.PreGrind, cancellationToken).ConfigureAwait(false);
        if (pre is not null)
        {
            series.Add(new RecordCurveSeries("Curve_BeforeGrinding", Offsets(pre, nominalRadiusMm)));
        }

        MeasurementRecord? post = await this.measurements
            .GetLatestByStageAsync(jobId, MeasurementStage.PostGrind, cancellationToken).ConfigureAwait(false);
        post ??= await this.measurements.GetLatestByJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (post is not null)
        {
            series.Add(new RecordCurveSeries("Curve_AfterGrinding", Offsets(post, nominalRadiusMm)));
        }

        return new RecordCurve(
            RecordCurveKind.BeforeAfterProfile, series, "Unit_Millimeter", "Unit_Micrometer");
    }

    /// <summary>误差曲线：磨后实测相对目标辊形。</summary>
    private async Task<RecordCurve> DeviationCurveAsync(string jobId, CancellationToken cancellationToken)
    {
        (Core.Steps.GrindingJob Job, JobState State)? stored = await this.jobs
            .GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        MeasurementRecord? post = await this.measurements
            .GetLatestByStageAsync(jobId, MeasurementStage.PostGrind, cancellationToken).ConfigureAwait(false);
        post ??= await this.measurements.GetLatestByJobAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (stored is null || post is null)
        {
            return RecordCurve.Empty(RecordCurveKind.Deviation);
        }

        Core.Geometry.RollProfile target = stored.Value.Job.Profile.Compose(
            stored.Value.Job.Geometry, this.profileTypes, this.settings.ProfileSampleCount);
        Core.Geometry.RollProfile deviation = CompensationCalculator.ComputeDeviation(
            post.Profile, target, stored.Value.Job.Geometry);

        var points = new List<(double, double)>(deviation.Points.Count);
        foreach (ProfilePoint point in deviation.Points)
        {
            points.Add((point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)));
        }

        return new RecordCurve(
            RecordCurveKind.Deviation,
            new[] { new RecordCurveSeries("Curve_Deviation", points) },
            "Unit_Millimeter",
            "Unit_Micrometer");
    }

    /// <summary>圆度：各截面的圆度与偏心，两条线。</summary>
    private async Task<RecordCurve> RoundnessCurveAsync(string jobId, CancellationToken cancellationToken)
    {
        RoundnessMeasurement? measurement = await this.roundness
            .GetLatestByJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (measurement is null || measurement.Points.Count == 0)
        {
            return RecordCurve.Empty(RecordCurveKind.Roundness);
        }

        var roundnessPoints = new List<(double, double)>(measurement.Points.Count);
        var eccentricityPoints = new List<(double, double)>(measurement.Points.Count);
        foreach (RoundnessPoint point in measurement.Points)
        {
            roundnessPoints.Add((point.BodyPositionMm, point.RoundnessMicrometer));
            eccentricityPoints.Add((point.BodyPositionMm, point.EccentricityMicrometer));
        }

        return new RecordCurve(
            RecordCurveKind.Roundness,
            new[]
            {
                new RecordCurveSeries("Curve_Roundness", roundnessPoints),
                new RecordCurveSeries("Curve_Eccentricity", eccentricityPoints),
            },
            "Unit_Millimeter",
            "Unit_Micrometer");
    }

    /// <summary>
    /// 补偿收敛过程：第几次迭代 → 那一次补偿量的最大值（直径量 µm）。
    ///
    /// 看的是它有没有在往下走。补偿量越补越小，说明这一支在收敛；
    /// 一直不降就是补错了方向，那时候该停下来查而不是接着补。
    /// </summary>
    private async Task<RecordCurve> ConvergenceCurveAsync(string jobId, CancellationToken cancellationToken)
    {
        IReadOnlyList<CompensationRecord> history = await this.compensations
            .ListByJobAsync(jobId, 200, cancellationToken).ConfigureAwait(false);
        if (history.Count == 0)
        {
            return RecordCurve.Empty(RecordCurveKind.CompensationConvergence);
        }

        var points = new List<(double, double)>(history.Count);
        for (int i = 0; i < history.Count; i++)
        {
            double worst = 0.0;
            foreach (ProfilePoint point in history[i].Points)
            {
                worst = Math.Max(worst, Math.Abs(UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)));
            }

            points.Add((i + 1, worst));
        }

        return new RecordCurve(
            RecordCurveKind.CompensationConvergence,
            new[] { new RecordCurveSeries("Curve_Convergence", points) },
            "Unit_Count",
            "Unit_Micrometer");
    }

    /// <summary>实测点列换成"相对公称半径的偏差"（直径量 µm）。</summary>
    private static IReadOnlyList<(double X, double Y)> Offsets(
        MeasurementRecord measurement, double nominalRadiusMm)
    {
        var points = new List<(double, double)>(measurement.Profile.Points.Count);
        foreach (MeasurementPoint point in measurement.Profile.Points)
        {
            points.Add((
                point.BodyPositionMm,
                UnitConversion.RadiusMmToDiameterMicrometer(point.MeasuredRadiusMm - nominalRadiusMm)));
        }

        return points;
    }

    public async Task FinishAsync(
        string recordId, JobState state, string? note, CancellationToken cancellationToken)
    {
        // 收尾时把两样东西定格下来：砂轮有多大、圆度与偏心量成什么样。
        // 都是"过了这一刻就再也取不到"的量——砂轮明天就小了，
        // 轨迹下一支辊一上来就清了。
        await this.records
            .FinishAsync(
                recordId,
                this.timeProvider.GetUtcNow(),
                state,
                note,
                this.calibration.Current.WheelDiameterMm,
                cancellationToken)
            .ConfigureAwait(false);

        await SaveRoundnessAsync(recordId, cancellationToken).ConfigureAwait(false);
        await QueuePostGrindReportAsync(recordId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把这一支辊收到的圆度与偏心轨迹存下来。
    ///
    /// 轨迹是按位置攒在内存里的，换一支辊就清掉——不存下来，记录页上的
    /// 圆度误差与同轴度永远是空的。一个点都没收到就不建空记录：
    /// "没量过"与"量了是 0"是两回事。
    /// </summary>
    private async Task SaveRoundnessAsync(string recordId, CancellationToken cancellationToken)
    {
        try
        {
            GrindingRecord? record = await this.records.GetAsync(recordId, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return;
            }

            IReadOnlyList<SurfaceTracePoint> roundness = this.traces.Trace(SurfaceTraceKind.Roundness);
            IReadOnlyList<SurfaceTracePoint> eccentricity = this.traces.Trace(SurfaceTraceKind.Eccentricity);
            if (roundness.Count == 0 && eccentricity.Count == 0)
            {
                return;
            }

            // 两条轨迹按位置对齐：同一个格子上收到的两个数才算同一个截面。
            var byPosition = new SortedDictionary<double, (double Roundness, double Eccentricity)>();
            foreach (SurfaceTracePoint point in roundness)
            {
                byPosition[point.BodyPositionMm] = (point.Value, 0.0);
            }

            foreach (SurfaceTracePoint point in eccentricity)
            {
                byPosition.TryGetValue(point.BodyPositionMm, out (double Roundness, double Eccentricity) existing);
                byPosition[point.BodyPositionMm] = (existing.Roundness, point.Value);
            }

            var points = new List<RoundnessPoint>(byPosition.Count);
            foreach (KeyValuePair<double, (double Roundness, double Eccentricity)> pair in byPosition)
            {
                points.Add(new RoundnessPoint(pair.Key, pair.Value.Roundness, pair.Value.Eccentricity));
            }

            await this.roundness.AddAsync(
                new RoundnessMeasurement(
                    Guid.NewGuid().ToString("N"),
                    record.JobId,
                    this.timeProvider.GetUtcNow(),
                    RoundnessSource,
                    points),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 记录已经收尾了，圆度没存下来不该把它退回去。
            this.alarmSink.Raise(
                AlarmSeverity.Warning, RoundnessNotArchivedResourceKey, ex.Message, AlarmCodes.HandoverNotArchived);
        }
    }

    /// <summary>
    /// 勾了"打印磨后数据"就出一张磨削报告交给界面去打。
    ///
    /// 收尾本身已经落库了，所以这一步出不来也不回滚：报表打不出来不该
    /// 让一条磨完的记录变成没收尾的。打不成只报一条提示级报警。
    /// </summary>
    private async Task QueuePostGrindReportAsync(string recordId, CancellationToken cancellationToken)
    {
        try
        {
            GrindingRecord? record = await this.records.GetAsync(recordId, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return;
            }

            (Core.Steps.GrindingJob Job, JobState State)? job = await this.jobs
                .GetAsync(record.JobId, cancellationToken).ConfigureAwait(false);
            if (job?.Job.IsProgramOptionEnabled(ProgramOptionKeys.PrintPostGrindData) != true)
            {
                return;
            }

            GrindingReport? report = await this.reports
                .BuildAsync(recordId, ReportKind.PostGrind, cancellationToken).ConfigureAwait(false);
            if (report is not null)
            {
                this.printQueue.Enqueue(report);
            }
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException)
        {
            this.alarmSink.Raise(
                AlarmSeverity.Warning, ReportNotPrintedResourceKey, ex.Message, AlarmCodes.ReportNotPrinted);
        }
    }

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
