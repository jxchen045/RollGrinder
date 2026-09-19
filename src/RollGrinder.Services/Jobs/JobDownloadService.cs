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
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Nc;
using RollGrinder.Services.Alarms;

namespace RollGrinder.Services.Jobs;

/// <summary>
/// 下发结果。参数不合法或 tagmap 缺项时什么都不写。
/// </summary>
/// <param name="Succeeded">是否已下发。</param>
/// <param name="Violations">参数校验失败项。</param>
/// <param name="MissingTags">tagmap 里缺失的必需逻辑名。</param>
/// <param name="RecordId">本次磨削记录标识，未下发时为空。</param>
/// <param name="WriteCount">实际写入的变量条数。</param>
public sealed record JobDownloadResult(
    bool Succeeded,
    IReadOnlyList<ParameterViolation> Violations,
    IReadOnlyList<string> MissingTags,
    string? RecordId,
    int WriteCount);

/// <summary>参数下发。</summary>
public interface IJobDownloadService
{
    /// <summary>
    /// 校验并下发一份作业：先写全部参数，最后写"参数有效"标志。
    /// 标志写完之后磨削由 NC 与 PLC 负责，上位机即使被强制结束，这支辊也能磨完。
    /// </summary>
    Task<JobDownloadResult> DownloadAsync(GrindingJob job, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IJobDownloadService"/>
public sealed class JobDownloadService : IJobDownloadService
{
    /// <summary>下发成功的报警（提示级）资源键。</summary>
    public const string HandoverCompletedResourceKey = "Alarm_HandoverCompleted";

    /// <summary>下发后归档失败的资源键：NC 已拿到参数，只是记录没落库。</summary>
    public const string HandoverNotArchivedResourceKey = "Alarm_HandoverNotArchived";

    private readonly IMachineGateway gateway;
    private readonly GrindingJobValidator validator;
    private readonly NcJobTranslator translator;
    private readonly MachineCapability capability;
    private readonly HmiSettings settings;
    private readonly IRollRepository rolls;
    private readonly IJobRepository jobs;
    private readonly IGrindingRecordRepository records;
    private readonly ICompensationRepository compensations;
    private readonly IAlarmSink alarms;
    private readonly TimeProvider timeProvider;

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
        IAlarmSink alarms,
        TimeProvider timeProvider)
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
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<JobDownloadResult> DownloadAsync(GrindingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

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

        NcDownload download = this.translator.Translate(job, compensation, this.settings.ProfileSampleCount, now);

        // 先写机床：写不进去就不该在库里留下"已下发"。
        await this.gateway.WriteTagsAsync(download.Writes, cancellationToken).ConfigureAwait(false);

        string recordId = Guid.NewGuid().ToString("N");
        try
        {
            await EnsureRollAsync(job, now, cancellationToken).ConfigureAwait(false);
            await this.jobs.SaveAsync(job, JobState.Handed, cancellationToken).ConfigureAwait(false);
            await this.records.AddAsync(
                new GrindingRecord(recordId, job.JobId, now, null, JobState.Handed, null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 参数已经在 NC 手里，这支辊照磨；只是记录没落库，必须让人看见。
            this.alarms.Raise(AlarmSeverity.Error, HandoverNotArchivedResourceKey, ex.Message, AlarmCodes.HandoverNotArchived);
            return new JobDownloadResult(true, Array.Empty<ParameterViolation>(), Array.Empty<string>(), null, download.Writes.Count);
        }

        this.alarms.Raise(AlarmSeverity.Information, HandoverCompletedResourceKey, job.JobId, AlarmCodes.HandoverCompleted);
        return new JobDownloadResult(true, Array.Empty<ParameterViolation>(), Array.Empty<string>(), recordId, download.Writes.Count);
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
