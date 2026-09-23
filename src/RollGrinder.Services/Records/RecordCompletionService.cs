using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Records;

/// <summary>
/// 一支辊磨完（或被中止）时，自动给它的磨削记录收尾。
///
/// 判据是 NC 的"循环正常结束"位（<see cref="MachineTagKeys.JobCycleComplete"/>），见
/// <see cref="CycleCompletionDetector"/>。收尾走 <see cref="IRecordService.FinishAsync"/>，
/// 和操作员在记录页按"完成"是同一条路：定格砂轮直径、存圆度与偏心、勾了就出磨后报告。
///
/// 这件事只**记账**，不碰机床：上位机不在的时候这支辊照样磨完，
/// 重启后第一拍看到结束位还是 1，记录照样补上。
/// </summary>
public sealed class RecordCompletionService : IHostedService
{
    public const string CompletedResourceKey = "Alarm_RecordCompleted";
    public const string AbandonedResourceKey = "Alarm_RecordAbandoned";
    public const string NeedsOperatorResourceKey = "Alarm_RecordNeedsManualFinish";
    public const string FailedResourceKey = "Alarm_RecordFinishFailed";

    private readonly IMachineMonitor monitor;
    private readonly IGrindingRecordRepository records;
    private readonly IRecordService recordService;
    private readonly IAlarmSink alarms;
    private readonly TimeProvider timeProvider;
    private readonly CycleCompletionDetector detector = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim serial = new(1, 1);

    public RecordCompletionService(
        IMachineMonitor monitor,
        IGrindingRecordRepository records,
        IRecordService recordService,
        IAlarmSink alarms,
        TimeProvider timeProvider)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.monitor.SnapshotUpdated += OnSnapshot;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.monitor.SnapshotUpdated -= OnSnapshot;
        return Task.CompletedTask;
    }

    /// <summary>喂一拍快照；返回这一拍的判定（给测试看）。收尾本身在后台做。</summary>
    public CycleCompletionDecision Observe(MachineStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool connected = snapshot.ConnectionState == GatewayConnectionState.Connected;
        NcChannelState? channel = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState) is double state
            ? (NcChannelState)(int)state
            : null;
        bool? complete = snapshot.GetNumberOrNull(MachineTagKeys.JobCycleComplete) is double flag
            ? flag > 0.5
            : null;

        lock (this.gate)
        {
            return this.detector.Observe(connected, channel, complete);
        }
    }

    /// <summary>按判定给当前记录收尾。公开出来是为了测试能等它做完。</summary>
    public async Task ApplyAsync(CycleCompletionDecision decision, CancellationToken cancellationToken)
    {
        if (decision == CycleCompletionDecision.None)
        {
            return;
        }

        await this.serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GrindingRecord? open = await FindOpenRecordAsync(cancellationToken).ConfigureAwait(false);
            if (open is null)
            {
                // 没有挂着的记录（手动收过了，或者这一趟不是上位机下发的）：没什么可收的。
                return;
            }

            switch (decision)
            {
                case CycleCompletionDecision.Completed:
                    await this.recordService
                        .FinishAsync(open.RecordId, JobState.Completed, note: null, cancellationToken).ConfigureAwait(false);
                    this.alarms.Raise(AlarmSeverity.Information, CompletedResourceKey, open.JobId, AlarmCodes.RecordCompleted);
                    break;

                case CycleCompletionDecision.Abandoned:
                    await this.recordService
                        .FinishAsync(open.RecordId, JobState.Abandoned, note: null, cancellationToken).ConfigureAwait(false);
                    this.alarms.Raise(AlarmSeverity.Warning, AbandonedResourceKey, open.JobId, AlarmCodes.RecordAbandoned);
                    break;

                case CycleCompletionDecision.NeedsOperator:
                    this.alarms.Raise(AlarmSeverity.Information, NeedsOperatorResourceKey, open.JobId, AlarmCodes.RecordNeedsManualFinish);
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 记账失败不影响机床；报一声，记录还能在记录页手动收尾。
            this.alarms.Raise(AlarmSeverity.Warning, FailedResourceKey, ex.Message, AlarmCodes.HandoverNotArchived);
        }
        finally
        {
            this.serial.Release();
        }
    }

    private void OnSnapshot(object? sender, MachineStateSnapshot snapshot)
    {
        CycleCompletionDecision decision = Observe(snapshot);
        if (decision != CycleCompletionDecision.None)
        {
            _ = ApplyAsync(decision, CancellationToken.None);
        }
    }

    /// <summary>当前挂着的记录：最近一条，且还没收尾。与测量采集服务认"当前作业"的办法一致。</summary>
    private async Task<GrindingRecord?> FindOpenRecordAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        IReadOnlyList<GrindingRecord> recent = await this.records
            .QueryAsync(now.AddDays(-2.0), now.AddDays(1.0), 1, cancellationToken)
            .ConfigureAwait(false);
        return recent.Count > 0 && recent[0].FinishedAtUtc is null ? recent[0] : null;
    }
}
