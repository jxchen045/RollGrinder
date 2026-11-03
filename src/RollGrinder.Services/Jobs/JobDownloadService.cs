using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Nc;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;

namespace RollGrinder.Services.Jobs;

/// <summary>
/// 下发结果。参数不合法或 tagmap 缺项时什么都不写。
/// </summary>
/// <param name="Succeeded">是否已下发。</param>
/// <param name="Violations">参数校验失败项。</param>
/// <param name="MissingTags">tagmap 里缺失的必需逻辑名。</param>
/// <param name="RecordId">本次磨削记录标识，未下发时为空。</param>
/// <param name="WriteCount">实际写入的变量条数。</param>
/// <param name="DownloadSequence">这次的下发序号（接口约定第 2 节）；tagmap 没映射时为空。</param>
public sealed record JobDownloadResult(
    bool Succeeded,
    IReadOnlyList<ParameterViolation> Violations,
    IReadOnlyList<string> MissingTags,
    string? RecordId,
    int WriteCount,
    int? DownloadSequence = null);

/// <summary>参数下发。</summary>
public interface IJobDownloadService
{
    /// <summary>
    /// 校验并下发一份作业（接口约定第 2 节）：① 参数有效置 false ② 读回确认 ③ 写全部参数
    /// ④ 下发序号加一 ⑤ 参数有效置 true。
    /// 标志写完之后磨削由 NC 与 PLC 负责，上位机即使被强制结束，这支辊也能磨完。
    /// </summary>
    Task<JobDownloadResult> DownloadAsync(GrindingJob job, CancellationToken cancellationToken);

    /// <summary>
    /// 同上，并把下发快照（调整后的工序、合成辊形点列、核对结果）存进这次的磨削记录（关系设计 V3）。
    /// </summary>
    Task<JobDownloadResult> DownloadAsync(
        GrindingJob job, IReadOnlyList<JobCheck> checks, string operatorName, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IJobDownloadService"/>
public sealed class JobDownloadService : IJobDownloadService
{
    /// <summary>下发成功的报警（提示级）资源键。</summary>
    public const string HandoverCompletedResourceKey = "Alarm_HandoverCompleted";

    /// <summary>下发后归档失败的资源键：NC 已拿到参数，只是记录没落库。</summary>
    public const string HandoverNotArchivedResourceKey = "Alarm_HandoverNotArchived";

    /// <summary>报表出不来的资源键。参数已经在 NC 手里，这支辊照磨。</summary>
    public const string ReportNotPrintedResourceKey = "Alarm_ReportNotPrinted";

    private readonly IMachineGateway gateway;
    private readonly GrindingJobValidator validator;
    private readonly NcJobTranslator translator;
    private readonly MachineCapability capability;
    private readonly HmiSettings settings;
    private readonly IRollRepository rolls;
    private readonly IJobRepository jobs;
    private readonly IGrindingRecordRepository records;
    private readonly ICompensationRepository compensations;
    private readonly IReportService reports;
    private readonly IReportPrintQueue printQueue;
    private readonly IAlarmSink alarms;
    private readonly TimeProvider timeProvider;
    private readonly Records.CyclePlausibilityMonitor plausibility;

    public JobDownloadService(
        IMachineGateway gateway,
        GrindingJobValidator validator,
        NcJobTranslator translator,
        MachineCapability capability,
        HmiSettings settings,
        IRollRepository rolls,
        IJobRepository jobs,
        IGrindingRecordRepository records,
        ICompensationRepository compensations,
        IReportService reports,
        IReportPrintQueue printQueue,
        IAlarmSink alarms,
        TimeProvider timeProvider,
        Records.CyclePlausibilityMonitor plausibility)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.translator = translator ?? throw new ArgumentNullException(nameof(translator));
        this.capability = capability ?? throw new ArgumentNullException(nameof(capability));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.compensations = compensations ?? throw new ArgumentNullException(nameof(compensations));
        this.reports = reports ?? throw new ArgumentNullException(nameof(reports));
        this.printQueue = printQueue ?? throw new ArgumentNullException(nameof(printQueue));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.plausibility = plausibility ?? throw new ArgumentNullException(nameof(plausibility));
    }

    public Task<JobDownloadResult> DownloadAsync(GrindingJob job, CancellationToken cancellationToken) =>
        DownloadAsync(job, Array.Empty<JobCheck>(), string.Empty, cancellationToken);

    public async Task<JobDownloadResult> DownloadAsync(
        GrindingJob job, IReadOnlyList<JobCheck> checks, string operatorName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(checks);

        ParameterValidationResult validation = this.validator.Validate(job, this.capability);
        if (!validation.IsValid)
        {
            return new JobDownloadResult(false, validation.Violations, Array.Empty<string>(), null, 0);
        }

        IReadOnlyList<string> missingTags = this.translator.FindMissingRequiredTags();
        if (missingTags.Count > 0)
        {
            return new JobDownloadResult(false, Array.Empty<ParameterViolation>(), missingTags, null, 0);
        }

        RollProfile? compensation = await LoadCompensationAsync(job.JobId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = this.timeProvider.GetUtcNow();

        // 上一次的序号要从 NC 读回来；tagmap 把它声明成只写的就不发序号（诊断页显示 "--"）。
        int? sequence = this.translator.IsReadable(MachineTagKeys.JobDownloadSeq)
            ? await NextDownloadSequenceAsync(cancellationToken).ConfigureAwait(false)
            : null;
        // 上一支辊可能留着没被采用的修改：下发时把改参数序号对齐到 NC 已采用的，作废它。
        int? appliedEdit = this.translator.SupportsStagedEdits && this.translator.IsReadable(MachineTagKeys.JobEditAppliedSeq)
            ? ToInt((await this.gateway.ReadTagAsync(MachineTagKeys.JobEditAppliedSeq, cancellationToken).ConfigureAwait(false)).Raw)
            : null;
        NcDownload download = this.translator.Translate(
            job, compensation, this.settings.ProfileSampleCount, now, sequence, appliedEdit);

        // ① ② 先把"参数有效"置 false 并读回：以前开始写之前不清它，上一支辊留下的 true
        // 会让 NC 在上位机写到一半时把半新半旧的参数当成有效（接口约定第 2 节）。
        await InvalidateAsync(now, cancellationToken).ConfigureAwait(false);

        // ③ ④ ⑤ 先写机床：写不进去就不该在库里留下"已下发"。写到一半失败，
        // 参数有效停在 false，NC 拒绝启动。
        await this.gateway.WriteTagsAsync(download.Writes, cancellationToken).ConfigureAwait(false);
        int writeCount = download.Writes.Count + 1;

        string recordId = Guid.NewGuid().ToString("N");
        try
        {
            await EnsureRollAsync(job, now, cancellationToken).ConfigureAwait(false);
            await this.jobs.SaveAsync(job, JobState.Handed, cancellationToken).ConfigureAwait(false);
            await this.records.AddAsync(
                new GrindingRecord(recordId, job.JobId, now, null, JobState.Handed, null),
                cancellationToken).ConfigureAwait(false);
            await this.records.SetSnapshotAsync(
                recordId,
                LibrarySnapshotJson.WriteDownload(
                    job,
                    download.TargetProfile.Points
                        .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
                        .ToArray(),
                    checks.Select(check => (check.Item, check.Status.ToString(), check.MessageKey)).ToArray(),
                    operatorName,
                    now,
                    sequence),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 参数已经在 NC 手里，这支辊照磨；只是记录没落库，必须让人看见。
            this.alarms.Raise(AlarmSeverity.Error, HandoverNotArchivedResourceKey, ex.Message, AlarmCodes.HandoverNotArchived);
            return new JobDownloadResult(
                true, Array.Empty<ParameterViolation>(), Array.Empty<string>(), null, writeCount, sequence);
        }

        this.alarms.Raise(AlarmSeverity.Information, HandoverCompletedResourceKey, job.JobId, AlarmCodes.HandoverCompleted);
        this.plausibility.OnHandedOver(
            job.JobId, Records.CyclePlausibilityMonitor.Estimate(download.Plans, job.Geometry), now);

        await QueuePreGrindReportAsync(job, recordId, cancellationToken).ConfigureAwait(false);

        return new JobDownloadResult(
            true, Array.Empty<ParameterViolation>(), Array.Empty<string>(), recordId, writeCount, sequence);
    }

    /// <summary>"参数有效"置 false 并读回；读回不是 false 就不往下写。</summary>
    private async Task InvalidateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        TagWrite invalidate = this.translator.InvalidateWrite(now);
        await this.gateway.WriteTagAsync(invalidate.LogicalName, invalidate.Value, cancellationToken).ConfigureAwait(false);

        // 老 tagmap 可能把有效标志声明成只写：读不回来就只能信写成功了，不拦下发。
        if (!this.translator.IsReadable(MachineTagKeys.JobParametersValid))
        {
            return;
        }

        TagValue readBack = await this.gateway
            .ReadTagAsync(MachineTagKeys.JobParametersValid, cancellationToken).ConfigureAwait(false);
        if (IsSet(readBack.Raw))
        {
            throw new GatewayException(
                "NC still reports parametersValid = true after it was cleared; the download was not started.");
        }
    }

    /// <summary>上一次的下发序号加一。读不到（新机床、变量刚建）按 0 算。</summary>
    private async Task<int> NextDownloadSequenceAsync(CancellationToken cancellationToken)
    {
        TagValue current = await this.gateway
            .ReadTagAsync(MachineTagKeys.JobDownloadSeq, cancellationToken).ConfigureAwait(false);
        int last = ToInt(current.Raw);
        return last is < 0 or int.MaxValue ? 1 : last + 1;
    }

    private static int ToInt(object? raw) => raw switch
    {
        int number => number,
        double number => (int)number,
        IConvertible convertible => convertible.ToInt32(System.Globalization.CultureInfo.InvariantCulture),
        _ => 0,
    };

    private static bool IsSet(object? raw) => raw switch
    {
        bool flag => flag,
        int number => number != 0,
        double number => number != 0.0,
        _ => false,
    };

    /// <summary>
    /// 勾了"打印磨前数据"就出一张磨前工艺单交给界面去打。
    ///
    /// 放在最后，而且出不来也不让下发失败：参数已经在 NC 手里，这支辊照磨，
    /// 没纸没墨不是停机的理由（最高原则）。打不成只报一条提示级报警。
    /// </summary>
    private async Task QueuePreGrindReportAsync(
        GrindingJob job, string recordId, CancellationToken cancellationToken)
    {
        if (!job.IsProgramOptionEnabled(ProgramOptionKeys.PrintPreGrindData))
        {
            return;
        }

        try
        {
            GrindingReport? report = await this.reports
                .BuildAsync(recordId, ReportKind.PreGrind, cancellationToken).ConfigureAwait(false);
            if (report is not null)
            {
                this.printQueue.Enqueue(report);
            }
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException)
        {
            this.alarms.Raise(
                AlarmSeverity.Warning, ReportNotPrintedResourceKey, ex.Message, AlarmCodes.ReportNotPrinted);
        }
    }

    private async Task<RollProfile?> LoadCompensationAsync(string jobId, CancellationToken cancellationToken)
    {
        CompensationRecord? latest = await this.compensations.GetLatestByJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        return latest is null || latest.Points.Count < 2 ? null : new RollProfile(latest.Points);
    }

    private async Task EnsureRollAsync(GrindingJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RollRecord? existing = await this.rolls.GetAsync(job.RollId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        await this.rolls.UpsertAsync(
            new RollRecord(job.RollId, job.RollId, job.Geometry, null, now),
            cancellationToken).ConfigureAwait(false);
    }
}
