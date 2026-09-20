using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Profiles;

namespace RollGrinder.Data;

/// <summary>辊形库条目的摘要，列表页用。不含段，列个表不必把曲线都读出来。</summary>
/// <param name="ProfileId">辊形标识。</param>
/// <param name="Name">辊形名。</param>
/// <param name="ProfileTypeKey">主辊形（第一段）的曲线类型键。</param>
/// <param name="SegmentCount">叠了几段。</param>
/// <param name="BodyLengthMm">编辑时的参考辊身长度（mm）。</param>
/// <param name="ModifiedAtUtc">最后修改时刻。</param>
public sealed record RollProfileSummary(
    string ProfileId,
    string Name,
    string ProfileTypeKey,
    int SegmentCount,
    double BodyLengthMm,
    System.DateTimeOffset ModifiedAtUtc);

/// <summary>
/// 辊形库。辊形是**可复用的模板**，不属于任何一支辊——
/// 同一条 CVC 辊形会被几十支辊用到，所以它有自己的库，不挂在作业下面。
/// </summary>
public interface IRollProfileRepository
{
    /// <summary>按最后修改时间倒序列出辊形。</summary>
    Task<IReadOnlyList<RollProfileSummary>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>取一条完整辊形；不存在返回 null。</summary>
    Task<RollProfileDefinition?> GetAsync(string profileId, CancellationToken cancellationToken);

    /// <summary>整条替换式保存（新建或覆盖）。</summary>
    Task SaveAsync(RollProfileDefinition profile, CancellationToken cancellationToken);

    /// <summary>删掉一条辊形。已经引用过它的作业不受影响——作业存的是快照。</summary>
    Task DeleteAsync(string profileId, CancellationToken cancellationToken);
}
