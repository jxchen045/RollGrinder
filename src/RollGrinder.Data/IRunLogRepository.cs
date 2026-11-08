using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>磨削过程记录（设计说明第 6 节）：道次表、倍率摘要、事件、NC 确认。都挂在一条磨削记录下。</summary>
public interface IRunLogRepository
{
    /// <summary>追加道次行（同序号重复写只留一行）。</summary>
    Task AppendPassesAsync(string recordId, IReadOnlyList<RecordPassRow> rows, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecordPassRow>> ListPassesAsync(string recordId, CancellationToken cancellationToken);

    /// <summary>写入或更新倍率摘要（按工序、道次、倍率种类）。</summary>
    Task SaveOverridesAsync(string recordId, IReadOnlyList<OverrideSummaryRow> rows, CancellationToken cancellationToken);

    Task<IReadOnlyList<OverrideSummaryRow>> ListOverridesAsync(string recordId, CancellationToken cancellationToken);

    Task AddEventAsync(string recordId, RunEvent runEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<RunEvent>> ListEventsAsync(string recordId, CancellationToken cancellationToken);

    /// <summary>若干条记录的事件计数（越界、缺道）。没有事件的不在字典里。</summary>
    Task<IReadOnlyDictionary<string, RunEventCounts>> CountEventsAsync(IReadOnlyList<string> recordIds, CancellationToken cancellationToken);

    Task SetNcInfoAsync(string recordId, RecordNcInfo info, CancellationToken cancellationToken);

    Task<RecordNcInfo?> GetNcInfoAsync(string recordId, CancellationToken cancellationToken);
}
