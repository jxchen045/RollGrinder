using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>磨削记录。</summary>
public interface IGrindingRecordRepository
{
    Task AddAsync(GrindingRecord record, CancellationToken cancellationToken);

    Task FinishAsync(
        string recordId,
        DateTimeOffset finishedAtUtc,
        JobState state,
        string? note,
        CancellationToken cancellationToken);

    Task<GrindingRecord?> GetAsync(string recordId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GrindingRecord>> QueryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>删除早于给定时刻的记录，返回删除条数。</summary>
    Task<int> PurgeOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken);
}

/// <summary>测量结果。</summary>
public interface IMeasurementRepository
{
    Task AddAsync(MeasurementRecord measurement, CancellationToken cancellationToken);

    Task<MeasurementRecord?> GetLatestByJobAsync(string jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MeasurementRecord>> ListByJobAsync(string jobId, int limit, CancellationToken cancellationToken);
}

/// <summary>补偿结果。</summary>
public interface ICompensationRepository
{
    Task AddAsync(CompensationRecord compensation, CancellationToken cancellationToken);

    Task<CompensationRecord?> GetLatestByJobAsync(string jobId, CancellationToken cancellationToken);
}

/// <summary>报警归档。</summary>
public interface IAlarmRepository
{
    Task AddAsync(DateTimeOffset raisedAtUtc, int severity, string messageResourceKey, string? detail, CancellationToken cancellationToken);

    Task<IReadOnlyList<AlarmRecord>> ListAsync(int limit, CancellationToken cancellationToken);

    Task<int> PurgeOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken);
}
