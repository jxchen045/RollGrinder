using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Core;

namespace RollGrinder.Services.Alarms;

/// <summary>
/// 内存报警表，按上限滚动保留。线程安全：后台轮询与界面线程都会用到。
/// </summary>
public sealed class AlarmLog : IAlarmLog
{
    /// <summary>网关异常对应的资源键。</summary>
    public const string GatewayFailureResourceKey = "Alarm_GatewayFailure";

    /// <summary>领域异常对应的资源键。</summary>
    public const string DomainFailureResourceKey = "Alarm_DomainFailure";

    /// <summary>其他未预期异常对应的资源键。</summary>
    public const string UnexpectedFailureResourceKey = "Alarm_UnexpectedFailure";

    private readonly LinkedList<AlarmEntry> entries = new();
    private readonly object gate = new();
    private readonly int limit;
    private readonly TimeProvider timeProvider;

    private long nextId;

    public AlarmLog(int limit, TimeProvider timeProvider)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        this.limit = limit;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler? Changed;

    public void Raise(AlarmSeverity severity, string messageResourceKey, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageResourceKey);

        lock (this.gate)
        {
            this.entries.AddFirst(new AlarmEntry(
                ++this.nextId,
                this.timeProvider.GetUtcNow(),
                severity,
                messageResourceKey,
                detail));

            while (this.entries.Count > this.limit)
            {
                this.entries.RemoveLast();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string resourceKey = exception switch
        {
            GatewayException => GatewayFailureResourceKey,
            DomainException => DomainFailureResourceKey,
            _ => UnexpectedFailureResourceKey,
        };

        Raise(AlarmSeverity.Error, resourceKey, exception.Message);
    }

    public IReadOnlyList<AlarmEntry> Snapshot()
    {
        lock (this.gate)
        {
            return this.entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.entries.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
