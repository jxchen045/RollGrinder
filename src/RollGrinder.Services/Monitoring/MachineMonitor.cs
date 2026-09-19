using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;

namespace RollGrinder.Services.Monitoring;

/// <summary>
/// 按固定周期向网关取数并发布不可变快照。
/// 取数失败转为报警并继续重试——上位机不参与实时控制，取不到数不影响磨削。
/// </summary>
public sealed class MachineMonitor : IMachineMonitor, IAsyncDisposable
{
    /// <summary>连接断开对应的资源键。</summary>
    public const string ConnectionLostResourceKey = "Alarm_ConnectionLost";

    /// <summary>连接恢复对应的资源键。</summary>
    public const string ConnectionRestoredResourceKey = "Alarm_ConnectionRestored";

    private readonly IMachineGateway gateway;
    private readonly IReadOnlyList<string> monitoredKeys;
    private readonly TimeSpan pollInterval;
    private readonly IAlarmSink alarms;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    private CancellationTokenSource? loopCancellation;
    private Task? loopTask;
    private MachineStateSnapshot current;
    private bool lastPollFailed;

    public MachineMonitor(
        IMachineGateway gateway,
        MachineDescription machine,
        HmiSettings settings,
        IAlarmSink alarms,
        TimeProvider timeProvider)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(settings);
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        this.monitoredKeys = MachineTagKeys.MonitoringKeys(machine);
        this.pollInterval = TimeSpan.FromMilliseconds(settings.PollIntervalMs);
        this.current = MachineStateSnapshot.Empty(timeProvider.GetUtcNow());
    }

    public event EventHandler<MachineStateSnapshot>? SnapshotUpdated;

    public MachineStateSnapshot Current => Volatile.Read(ref this.current);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.loopTask is not null)
            {
                return;
            }

            await this.gateway.ConnectAsync(cancellationToken).ConfigureAwait(false);
            this.loopCancellation = new CancellationTokenSource();
            this.loopTask = Task.Run(() => PollLoopAsync(this.loopCancellation.Token), CancellationToken.None);
        }
        finally
        {
            this.lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await this.lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.loopCancellation is null || this.loopTask is null)
            {
                return;
            }

            await this.loopCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await this.loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }

            this.loopCancellation.Dispose();
            this.loopCancellation = null;
            this.loopTask = null;

            await this.gateway.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 取一次数并发布快照。后台循环用它，测试也可直接调用。
    /// 上一拍失败过就先尝试重连——会话断了不能一直等人重启上位机。
    /// </summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (this.lastPollFailed)
            {
                await this.gateway.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }

            MachineStateSnapshot snapshot = await this.gateway
                .ReadStateAsync(this.monitoredKeys, cancellationToken)
                .ConfigureAwait(false);

            Volatile.Write(ref this.current, snapshot);
            if (this.lastPollFailed)
            {
                this.lastPollFailed = false;
                this.alarms.Raise(AlarmSeverity.Information, ConnectionRestoredResourceKey);
            }

            SnapshotUpdated?.Invoke(this, snapshot);
        }
        catch (GatewayException ex)
        {
            Volatile.Write(
                ref this.current,
                new MachineStateSnapshot(
                    this.timeProvider.GetUtcNow(),
                    GatewayConnectionState.Faulted,
                    Array.Empty<TagValue>()));

            if (!this.lastPollFailed)
            {
                this.lastPollFailed = true;
                this.alarms.Raise(AlarmSeverity.Error, ConnectionLostResourceKey, ex.Message);
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(this.pollInterval, this.timeProvider);
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.lifecycleGate.Dispose();
    }
}
