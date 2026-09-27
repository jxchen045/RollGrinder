using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Audit;

/// <summary>一项改动：哪一项，从多少改成多少。</summary>
/// <param name="Item">哪一项（参数键、配置路径、变量逻辑名）。</param>
/// <param name="OldValue">原值；新增为空。</param>
/// <param name="NewValue">新值；删除为空。</param>
public sealed record ChangedItem(string Item, string? OldValue, string? NewValue);

/// <summary>
/// 改动记录（修改稿 5.8）：补偿设置、机床配置、标签映射的每一次改动都记下谁、何时、改了什么。
/// 记全部历史，诊断页"审计"里按时间倒序列出。
/// </summary>
public interface IChangeLog
{
    /// <summary>记了新的。</summary>
    event EventHandler? Changed;

    /// <summary>记一次改动（可以是几项）。没有真正变了的项就什么都不记。</summary>
    Task RecordAsync(string area, IEnumerable<ChangedItem> items, string changedBy, CancellationToken cancellationToken);

    /// <summary>最近的若干条，新的在前。</summary>
    Task<IReadOnlyList<ChangeLogEntry>> ListAsync(int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IChangeLog"/>
public sealed class ChangeLog : IChangeLog
{
    private readonly IChangeLogRepository repository;
    private readonly TimeProvider timeProvider;

    public ChangeLog(IChangeLogRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler? Changed;

    public async Task RecordAsync(string area, IEnumerable<ChangedItem> items, string changedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentNullException.ThrowIfNull(items);

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        ChangeLogEntry[] entries = items
            .Where(item => !string.Equals(item.OldValue, item.NewValue, StringComparison.Ordinal))
            .Select(item => new ChangeLogEntry(
                Guid.NewGuid().ToString("N"), now, changedBy ?? string.Empty, area, item.Item, item.OldValue, item.NewValue))
            .ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        await this.repository.AddAsync(entries, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<ChangeLogEntry>> ListAsync(int limit, CancellationToken cancellationToken) =>
        this.repository.ListAsync(limit, cancellationToken);
}
