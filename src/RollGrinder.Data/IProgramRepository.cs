using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Steps;

namespace RollGrinder.Data;

/// <summary>程序库条目的摘要，列表页用。不含工序，列个表不必把整支程序读出来。</summary>
/// <param name="ProgramId">程序标识。</param>
/// <param name="Name">程序名。</param>
/// <param name="StepCount">有几道工序。</param>
/// <param name="ModifiedAtUtc">最后修改时刻。</param>
public sealed record ProgramSummary(
    string ProgramId,
    string Name,
    int StepCount,
    DateTimeOffset ModifiedAtUtc);

/// <summary>
/// 程序库。程序是**可复用的模板**，不属于任何一支辊——
/// 同一支"热轧工作辊粗磨到精磨"的程序会被几百支辊用到，所以它有自己的库，
/// 不像以前那样挂在某一份作业下面。
/// </summary>
public interface IProgramRepository
{
    /// <summary>按最后修改时间倒序列出程序。</summary>
    Task<IReadOnlyList<ProgramSummary>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>取一支完整程序；不存在返回 null。</summary>
    Task<GrindingProgram?> GetAsync(string programId, CancellationToken cancellationToken);

    /// <summary>
    /// 库里已经叫这个名字的**另一条**的标识（去掉首尾空白、不分大小写）；没有返回 null。
    /// 用来判重名，也用来"覆盖"那一条。<paramref name="exceptId"/> 是正在保存的那一条自己，不算；新建时传 null。
    /// </summary>
    Task<string?> FindIdByNameAsync(string name, string? exceptId, CancellationToken cancellationToken);

    /// <summary>整支替换式保存（新建或覆盖）。</summary>
    Task SaveAsync(GrindingProgram program, CancellationToken cancellationToken);

    /// <summary>删掉一支程序。已经引用过它的作业不受影响——作业存的是快照。</summary>
    Task DeleteAsync(string programId, CancellationToken cancellationToken);
}
