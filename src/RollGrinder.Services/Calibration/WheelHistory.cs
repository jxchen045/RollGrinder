using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Calibration;

/// <summary>
/// 砂轮的修整与更换记录（修改稿 5.7）：换砂轮向导走完、程序里的修整工序走完、手动按了修整循环，
/// 各记一行。只是记账——修整、换砂轮都是机床和人做的事，上位机不参与。
/// </summary>
public interface IWheelHistory
{
    /// <summary>记了一条新的。</summary>
    event EventHandler? Changed;

    Task RecordAsync(
        WheelEventKind kind,
        WheelEventSource source,
        double? wheelDiameterMm,
        string changedBy,
        string detail,
        CancellationToken cancellationToken);

    /// <summary>最近的若干条，新的在前。</summary>
    Task<IReadOnlyList<WheelEvent>> ListAsync(int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWheelHistory"/>
public sealed class WheelHistory : IWheelHistory
{
    private readonly IWheelEventRepository repository;
    private readonly TimeProvider timeProvider;

    public WheelHistory(IWheelEventRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler? Changed;

    public async Task RecordAsync(
        WheelEventKind kind,
        WheelEventSource source,
        double? wheelDiameterMm,
        string changedBy,
        string detail,
        CancellationToken cancellationToken)
    {
        await this.repository.AddAsync(
            new WheelEvent(
                Guid.NewGuid().ToString("N"),
                this.timeProvider.GetUtcNow(),
                kind,
                source,
                wheelDiameterMm,
                changedBy ?? string.Empty,
                detail ?? string.Empty),
            cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<WheelEvent>> ListAsync(int limit, CancellationToken cancellationToken) =>
        this.repository.ListAsync(limit, cancellationToken);
}
