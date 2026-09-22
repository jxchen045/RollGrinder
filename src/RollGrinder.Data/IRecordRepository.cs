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

    /// <summary>
    /// 给一条记录收尾。<paramref name="wheelDiameterMm"/> 为 null 时保留原值——
    /// 收尾可能被重来一次，不该把已经记下的砂轮直径抹成空。
    /// </summary>
    Task FinishAsync(
        string recordId,
        DateTimeOffset finishedAtUtc,
        JobState state,
        string? note,
        double? wheelDiameterMm,
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

    /// <summary>取某支作业某个阶段最近一次测量。磨前直径、锥度这些指标靠它分得清是哪一次。</summary>
    Task<MeasurementRecord?> GetLatestByStageAsync(
        string jobId, MeasurementStage stage, CancellationToken cancellationToken);

    Task<IReadOnlyList<MeasurementRecord>> ListByJobAsync(string jobId, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// 圆度测量。与辊形测量分开：一个沿轴线扫出半径，一个绕圆周扫出圆度与偏心。
/// </summary>
public interface IRoundnessRepository
{
    Task AddAsync(RoundnessMeasurement measurement, CancellationToken cancellationToken);

    Task<RoundnessMeasurement?> GetLatestByJobAsync(string jobId, CancellationToken cancellationToken);
}

/// <summary>补偿结果。</summary>
public interface ICompensationRepository
{
    Task AddAsync(CompensationRecord compensation, CancellationToken cancellationToken);

    Task<CompensationRecord?> GetLatestByJobAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>这支作业迭代了几次补偿。记录页的"补偿迭代次数"就是它。</summary>
    Task<int> CountByJobAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>这支作业的补偿历史，**从早到晚**。收敛曲线要的就是这个顺序。</summary>
    Task<IReadOnlyList<CompensationRecord>> ListByJobAsync(
        string jobId, int limit, CancellationToken cancellationToken);
}

/// <summary>报警归档。</summary>
public interface IAlarmRepository
{
    Task AddAsync(
        DateTimeOffset raisedAtUtc,
        int severity,
        string messageResourceKey,
        string? detail,
        int code,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AlarmRecord>> ListAsync(int limit, CancellationToken cancellationToken);

    Task<int> PurgeOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken);
}
