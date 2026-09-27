using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Calibration;

/// <summary>
/// 程序里的"砂轮修整"工序走完，记一条修整记录（砂轮页的修整与更换记录）。
///
/// **上位机不指挥修整。** 这里只是看着 NC 在跑第几道：修整那一道一走完（工序号一换），记一笔。
/// 上位机被强制结束，修整照样做完，只是这一次没人记账。
/// </summary>
public sealed class WheelDressCaptureService : IHostedService
{
    private readonly IMachineMonitor monitor;
    private readonly IJobRepository jobs;
    private readonly IGrindingRecordRepository records;
    private readonly IWheelHistory history;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();

    /// <summary>上一拍 NC 在跑第几道；0 表示没在跑。</summary>
    private int lastStepOrder;

    /// <summary>那一道是不是修整；它跑完时才用得着。</summary>
    private string? lastDressJobId;

    public WheelDressCaptureService(
        IMachineMonitor monitor,
        IJobRepository jobs,
        IGrindingRecordRepository records,
        IWheelHistory history,
        TimeProvider timeProvider)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
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

    private void OnSnapshot(object? sender, MachineStateSnapshot snapshot) => _ = ObserveAsync(snapshot);

    private async Task ObserveAsync(MachineStateSnapshot snapshot)
    {
        if (snapshot is null || snapshot.ConnectionState != GatewayConnectionState.Connected)
        {
            return;
        }

        int order = (int)(snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentStepOrder) ?? 0.0);
        int finishedOrder;
        string? finishedJobId;
        lock (this.gate)
        {
            if (order == this.lastStepOrder)
            {
                return;
            }

            finishedOrder = this.lastStepOrder;
            finishedJobId = this.lastDressJobId;
            this.lastStepOrder = order;
            this.lastDressJobId = null;
        }

        try
        {
            if (finishedJobId is not null)
            {
                await this.history.RecordAsync(
                    WheelEventKind.Dress,
                    WheelEventSource.Program,
                    snapshot.GetNumberOrNull(MachineTagKeys.WheelDiameterMm),
                    string.Empty,
                    string.Create(CultureInfo.InvariantCulture, $"{finishedJobId} #{finishedOrder}"),
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (order < 1)
            {
                return;
            }

            GrindingJob? job = await LoadRunningJobAsync().ConfigureAwait(false);
            if (job is not null && order <= job.Steps.Count
                && string.Equals(job.Steps[order - 1].StepTypeKey, StepTypeKeys.WheelDress, StringComparison.Ordinal))
            {
                lock (this.gate)
                {
                    if (this.lastStepOrder == order)
                    {
                        this.lastDressJobId = job.JobId;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 只是记账：记不下来不报警，更不影响机床。
        }
    }

    /// <summary>当前在磨的是哪一支作业。最近一条还没收尾的记录指向它。</summary>
    private async Task<GrindingJob?> LoadRunningJobAsync()
    {
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        IReadOnlyList<GrindingRecord> recent = await this.records
            .QueryAsync(now.AddDays(-2.0), now.AddDays(1.0), 1, CancellationToken.None)
            .ConfigureAwait(false);
        if (recent.Count == 0)
        {
            return null;
        }

        (GrindingJob Job, JobState State)? stored = await this.jobs
            .GetAsync(recent[0].JobId, CancellationToken.None).ConfigureAwait(false);
        return stored?.Job;
    }
}
