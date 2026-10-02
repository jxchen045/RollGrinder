using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Steps;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>作业与其参数。参数按键值存，新增辊形或工序类型不改表结构。</summary>
public interface IJobRepository
{
    Task SaveAsync(GrindingJob job, JobState state, CancellationToken cancellationToken);

    Task<(GrindingJob Job, JobState State)?> GetAsync(string jobId, CancellationToken cancellationToken);

    Task SetStateAsync(string jobId, JobState state, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListJobIdsByRollAsync(string rollId, int limit, CancellationToken cancellationToken);

    /// <summary>某个状态的作业（待磨清单用：中断的、待返磨的），最近的在前。</summary>
    Task<IReadOnlyList<JobListEntry>> ListByStateAsync(JobState state, int limit, CancellationToken cancellationToken);
}

/// <summary>作业列表里的一行。</summary>
/// <param name="JobId">作业标识。</param>
/// <param name="RollId">辊号。</param>
/// <param name="CreatedAtUtc">建立时刻。</param>
/// <param name="State">状态。</param>
/// <param name="RegrindOf">返磨的话，返的是哪一次作业。</param>
public sealed record JobListEntry(string JobId, string RollId, System.DateTimeOffset CreatedAtUtc, JobState State, string? RegrindOf);
