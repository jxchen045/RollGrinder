using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>辊件档案。</summary>
public interface IRollRepository
{
    Task UpsertAsync(RollRecord roll, CancellationToken cancellationToken);

    Task<RollRecord?> GetAsync(string rollId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RollRecord>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>在用清单：计划里用这条辊形或这支程序的辊（不含作废的），按辊号排。</summary>
    Task<IReadOnlyList<RollRecord>> ListUsingAsync(string? profileId, string? programId, CancellationToken cancellationToken);
}
