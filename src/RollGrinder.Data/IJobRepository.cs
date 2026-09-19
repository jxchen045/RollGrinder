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
}
