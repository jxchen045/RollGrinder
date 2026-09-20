using System;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Measurement;

/// <summary>
/// 补偿计算结果。
/// </summary>
/// <param name="CompensationId">新补偿的标识，未生成时为空。</param>
/// <param name="Deviation">本次偏差曲线（半径量 mm）。</param>
/// <param name="Compensation">新的补偿曲线（半径量 mm）。</param>
/// <param name="Quality">偏差统计。</param>
public sealed record CompensationResult(
    string? CompensationId,
    RollProfile Deviation,
    RollProfile Compensation,
    ProfileQuality Quality);

/// <summary>由最近一次测量算出下一次的补偿量。</summary>
public interface ICompensationService
{
    /// <summary>
    /// 取某支作业最近一次测量，与目标辊形比对后算出新的补偿并归档。
    /// 补偿只在下一次下发时生效，不会去动正在执行的程序。
    /// </summary>
    Task<CompensationResult> ComputeAndStoreAsync(string jobId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICompensationService"/>
public sealed class CompensationService : ICompensationService
{
    /// <summary>单次补偿最大修正量在 machine.json 中的阈值键。</summary>
    public const string MaxCompensationRadiusMmKey = "maxCompensationRadiusMm";

    private readonly IJobRepository jobs;
    private readonly IMeasurementRepository measurements;
    private readonly ICompensationRepository compensations;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;
    private readonly TimeProvider timeProvider;

    public CompensationService(
        IJobRepository jobs,
        IMeasurementRepository measurements,
        ICompensationRepository compensations,
        RollProfileTypeRegistry profileTypes,
        MachineDescription machine,
        HmiSettings settings,
        TimeProvider timeProvider)
    {
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.measurements = measurements ?? throw new ArgumentNullException(nameof(measurements));
        this.compensations = compensations ?? throw new ArgumentNullException(nameof(compensations));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<CompensationResult> ComputeAndStoreAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        (GrindingJob Job, JobState State)? stored = await this.jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            throw new DomainException($"Job '{jobId}' does not exist.");
        }

        MeasurementRecord? measurement = await this.measurements
            .GetLatestByJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (measurement is null)
        {
            throw new DomainException($"Job '{jobId}' has no measurement to compensate from.");
        }

        GrindingJob job = stored.Value.Job;

        // 补偿对着**合成后**的整条辊形算：端部锥度与倒角也是目标的一部分，
        // 只拿主辊形去比，两端的偏差会被当成误差补进去。
        RollProfile target = job.Profile.Compose(
            job.Geometry, this.profileTypes, this.settings.ProfileSampleCount);

        RollProfile deviation = CompensationCalculator.ComputeDeviation(measurement.Profile, target, job.Geometry);

        CompensationRecord? previousRecord = await this.compensations
            .GetLatestByJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        RollProfile? previous = previousRecord is null || previousRecord.Points.Count < 2
            ? null
            : new RollProfile(previousRecord.Points);

        RollProfile compensation = CompensationCalculator.ComputeCompensation(previous, deviation, CreateSettings());

        string compensationId = Guid.NewGuid().ToString("N");
        await this.compensations.AddAsync(
            new CompensationRecord(
                compensationId,
                jobId,
                this.timeProvider.GetUtcNow(),
                measurement.MeasurementId,
                compensation.Points),
            cancellationToken).ConfigureAwait(false);

        return new CompensationResult(
            compensationId,
            deviation,
            compensation,
            ProfileQuality.FromDeviation(deviation));
    }

    private CompensationSettings CreateSettings()
    {
        if (!this.machine.Thresholds.TryGetValue(MaxCompensationRadiusMmKey, out double maxCorrectionRadiusMm))
        {
            throw new GatewayException(
                $"machine.json is missing threshold '{MaxCompensationRadiusMmKey}'; the HMI will not guess a correction limit.");
        }

        return CompensationSettings.Create(
            this.settings.CompensationGain,
            this.settings.CompensationSmoothingPoints,
            maxCorrectionRadiusMm);
    }
}
