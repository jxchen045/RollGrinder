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
}
