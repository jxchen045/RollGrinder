using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Measurement;

/// <summary>
/// 测量工序走完就把那一趟的读数存成一次测量。
///
/// 为什么要自动存：磨前直径、锥度、实际凸度这些指标，靠的是"磨前量的那一次"
/// 与"磨后量的那一次"分别存着。先前只有手动页按"采点"才落库，自动磨削里
/// NC 自己走的那几趟测量一次都没留下——记录页上那些格子因此永远是空的。
///
/// **上位机不指挥测量。** 测量臂什么时候放下、走到哪里，都是 NC 按工序做的；
/// 这里只是看着工序号：测量工序在跑时轨迹自己在攒，工序一换就把攒到的存下来。
/// 上位机被强制结束，机床照样把这支辊量完磨完，只是这一次没人替它记账。
/// </summary>
public sealed class MeasurementCaptureService : IHostedService
{
    /// <summary>自动存下来的测量的来源标记。</summary>
    public const string AutomaticSource = "auto";

    /// <summary>存不下来时报出来的资源键。</summary>
    public const string NotArchivedResourceKey = "Alarm_MeasurementNotArchived";

    private readonly IMachineMonitor monitor;
    private readonly ISurfaceTraceService traces;
    private readonly IMeasurementRepository measurements;
    private readonly IJobRepository jobs;
    private readonly IGrindingRecordRepository records;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly IAlarmSink alarms;
    private readonly TimeProvider timeProvider;

    private readonly object gate = new();

    /// <summary>上一拍 NC 在跑第几道；0 表示没在跑。</summary>
    private int lastStepOrder;

    /// <summary>那一道是不是测量工序。工序一换，就靠它决定要不要把轨迹存下来。</summary>
    private bool lastStepMeasures;

    public MeasurementCaptureService(
        IMachineMonitor monitor,
        ISurfaceTraceService traces,
        IMeasurementRepository measurements,
        IJobRepository jobs,
        IGrindingRecordRepository records,
        GrindingStepTypeRegistry stepTypes,
        IAlarmSink alarms,
        TimeProvider timeProvider)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.traces = traces ?? throw new ArgumentNullException(nameof(traces));
        this.measurements = measurements ?? throw new ArgumentNullException(nameof(measurements));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
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

    /// <summary>
    /// 这一次测量算磨前、磨中还是磨后。
    ///
    /// 判据是**这道测量工序前后还有没有要切削的工序**：
    /// 前面一道切削都没有 ⇒ 来料测量；后面一道切削都没有 ⇒ 成品测量；
    /// 夹在中间 ⇒ 中间测量，那是给行程间补偿用的。
    /// </summary>
    public static MeasurementStage StageOf(IReadOnlyList<GrindingStepPlan> plans, int stepOrder)
    {
        ArgumentNullException.ThrowIfNull(plans);

        bool cuttingBefore = false;
        bool cuttingAfter = false;
        for (int i = 0; i < plans.Count; i++)
        {
            if (!plans[i].IsCutting)
            {
                continue;
            }

            if (i + 1 < stepOrder)
            {
                cuttingBefore = true;
            }
            else if (i + 1 > stepOrder)
            {
                cuttingAfter = true;
            }
        }

        return (cuttingBefore, cuttingAfter) switch
        {
            (false, _) => MeasurementStage.PreGrind,
            (true, false) => MeasurementStage.PostGrind,
            _ => MeasurementStage.InProcess,
        };
    }

    private void OnSnapshot(object? sender, MachineStateSnapshot snapshot) =>
        _ = ObserveAsync(snapshot);

    private async Task ObserveAsync(MachineStateSnapshot snapshot)
    {
        if (snapshot is null || snapshot.ConnectionState != GatewayConnectionState.Connected)
        {
            return;
        }

        int order = (int)(snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentStepOrder) ?? 0.0);

        int finishedOrder;
        lock (this.gate)
        {
            if (order == this.lastStepOrder)
            {
                return;
            }

            finishedOrder = this.lastStepMeasures ? this.lastStepOrder : 0;
            this.lastStepOrder = order;

            // 这一拍先记下新那一道是不是测量工序；它跑完时才用得着。
            // 存不下来也照记——记账失败不该让下一次也跟着丢。
            this.lastStepMeasures = false;
        }

        try
        {
            GrindingJob? job = await LoadRunningJobAsync().ConfigureAwait(false);
            if (job is null)
            {
                return;
            }

            IReadOnlyList<GrindingStepPlan> plans = Plan(job);

            lock (this.gate)
            {
                this.lastStepMeasures = order >= 1 && order <= plans.Count && plans[order - 1].RequiresMeasurement;
            }

            if (finishedOrder >= 1 && finishedOrder <= plans.Count)
            {
                await ArchiveAsync(job, plans, finishedOrder).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException or GatewayException)
        {
            this.alarms.Raise(
                AlarmSeverity.Warning, NotArchivedResourceKey, ex.Message, AlarmCodes.HandoverNotArchived);
        }
    }

    /// <summary>把测量工序那一趟攒到的直径轨迹存成一次测量。</summary>
    private async Task ArchiveAsync(
        GrindingJob job, IReadOnlyList<GrindingStepPlan> plans, int stepOrder)
    {
        IReadOnlyList<SurfaceTracePoint> trace = this.traces.Trace(SurfaceTraceKind.Diameter);

        // 一两个点不成一条辊形。测量臂没真走过就别留一条误导人的记录。
        if (trace.Count < 2)
        {
            return;
        }

        var points = new List<MeasurementPoint>(trace.Count);
        foreach (SurfaceTracePoint point in trace)
        {
            points.Add(new MeasurementPoint(
                point.BodyPositionMm, UnitConversion.DiameterMmToRadiusMm(point.Value)));
        }

        await this.measurements.AddAsync(
            new MeasurementRecord(
                Guid.NewGuid().ToString("N"),
                job.JobId,
                this.timeProvider.GetUtcNow(),
                AutomaticSource,
                new MeasuredProfile(points))
            {
                Stage = StageOf(plans, stepOrder),
            },
            CancellationToken.None).ConfigureAwait(false);

        // 存完清空：不清的话，下一趟没走到的格子会带着上一趟的数混进去。
        this.traces.Clear(SurfaceTraceKind.Diameter);
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

    private IReadOnlyList<GrindingStepPlan> Plan(GrindingJob job)
    {
        var plans = new List<GrindingStepPlan>(job.Steps.Count);
        foreach (GrindingJobStep step in job.Steps)
        {
            plans.Add(this.stepTypes.Get(step.StepTypeKey).CreatePlan(job.Geometry, step.Parameters));
        }

        return plans;
    }
}
