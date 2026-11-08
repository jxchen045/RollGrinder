using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Monitoring;
using RollGrinder.Services.NcInterface;

namespace RollGrinder.Services.Records;

/// <summary>
/// 磨削过程记录（设计说明第 6 节）：下发成功后跟着这一份作业，按监视快照把
/// 道次表（NC 锁存、上位机读走的那些行）存进记录、倍率按道次汇总（平均 / 最低 / 最高 / 不在 100 % 的占比，不记逐条变化）、
/// 越界与通信事件逐条记、NC 确认下发、运行中改参数从第几道起生效。
///
/// **只记录，不干预机床**：读不到、断了，机床照常磨完，这里只记缺号与事件。
/// </summary>
public interface IRunRecorder
{
    /// <summary>正在记的磨削记录；没有为 null。</summary>
    string? ActiveRecordId { get; }

    /// <summary>下发成功：开始跟这一份作业（上一份没写完的先写完）。</summary>
    Task BeginAsync(string recordId, string jobId, int? downloadSequence, CancellationToken cancellationToken);

    /// <summary>按一份监视快照记（监视服务每拍调用；测试直接调）。</summary>
    Task PollAsync(MachineStateSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>把还在累计的倍率摘要写进库。</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>当前作业已累计的倍率摘要（含正在磨的这一道，自动页用）。</summary>
    IReadOnlyList<OverrideSummaryRow> LiveOverrides();
}

/// <inheritdoc cref="IRunRecorder"/>
public sealed class RunRecorder : IRunRecorder
{
    /// <summary>倍率低于这个算越界（设计说明第 6 节）。</summary>
    public const double LowLimitPercent = 50.0;

    /// <summary>倍率高于这个算越界。</summary>
    public const double HighLimitPercent = 110.0;

    /// <summary>两拍之间超过这么久不算进倍率时间（断线、暂停监视）。</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(2);

    /// <summary>过程曲线降采样间隔（设计说明第 6 节：曲线降采样后存，运行时的高频采样不进记录）。</summary>
    public static readonly TimeSpan TraceInterval = TimeSpan.FromSeconds(2);

    /// <summary>倍率摘要多久写一次库（道次结束时也写）。</summary>
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5);

    private static readonly (string Kind, string Tag)[] OverrideTags =
    {
        (OverrideKinds.Feed, MachineTagKeys.FeedOverridePercent),
        (OverrideKinds.Spindle, MachineTagKeys.SpindleOverridePercent),
        (OverrideKinds.Wheel, MachineTagKeys.WheelOverridePercent),
    };

    private readonly IPassDataCollector collector;
    private readonly IRunLogRepository runLog;
    private readonly IJobRepository jobs;
    private readonly IGrindingRecordRepository records;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateGate = new();

    private string? recordId;
    private string? jobId;
    private int? downloadSequence;
    private bool accepted;
    private bool resumeTried;
    private DateTimeOffset beginAtUtc;
    private int? lastApplied;
    private DateTimeOffset? lastSampleUtc;
    private DateTimeOffset lastFlushUtc;
    private (int Step, int Pass)? currentPass;
    private bool staleReported;
    private readonly HashSet<int> savedSequences = new();
    private readonly HashSet<int> knownMissing = new();
    private readonly Dictionary<(int Step, int Pass, string Kind), Accumulator> accumulators = new();
    private readonly HashSet<(int Step, int Pass, string Kind)> dirty = new();
    private readonly Dictionary<string, string?> excursion = new(StringComparer.Ordinal);
    private readonly List<TracePoint> pendingTrace = new();
    private readonly string? carriageZKey;
    private DateTimeOffset? lastTraceUtc;

    public RunRecorder(
        IMachineMonitor? monitor,
        IPassDataCollector collector,
        IRunLogRepository runLog,
        IJobRepository jobs,
        IGrindingRecordRepository records,
        TimeProvider timeProvider,
        MachineDescription? machine = null)
    {
        this.carriageZKey = machine?.Axes.FirstOrDefault(axis => axis.IsPresent && axis.Role == MachineAxisRoles.Carriage) is { } carriage
            ? MachineTagKeys.AxisActualPositionMm(carriage.Name)
            : null;
        this.collector = collector ?? throw new ArgumentNullException(nameof(collector));
        this.runLog = runLog ?? throw new ArgumentNullException(nameof(runLog));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (monitor is not null)
        {
            monitor.SnapshotUpdated += (_, snapshot) => _ = PollGuardedAsync(snapshot);
        }
    }

    public string? ActiveRecordId
    {
        get
        {
            lock (this.stateGate)
            {
                return this.recordId;
            }
        }
    }

    public async Task BeginAsync(string recordId, string jobId, int? downloadSequence, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
            Start(recordId, jobId, downloadSequence, this.timeProvider.GetUtcNow());
            await this.runLog.SetNcInfoAsync(recordId, new RecordNcInfo(downloadSequence, null), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task PollAsync(MachineStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PollCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public IReadOnlyList<OverrideSummaryRow> LiveOverrides()
    {
        lock (this.stateGate)
        {
            return this.accumulators
                .OrderBy(pair => pair.Key.Step).ThenBy(pair => pair.Key.Pass).ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal)
                .Select(pair => pair.Value.ToRow(pair.Key.Step, pair.Key.Pass, pair.Key.Kind))
                .ToArray();
        }
    }

    // ───────────── 内部 ─────────────

    private void Start(string record, string job, int? sequence, DateTimeOffset now)
    {
        lock (this.stateGate)
        {
            this.recordId = record;
            this.jobId = job;
            this.downloadSequence = sequence;
            this.accepted = false;
            this.beginAtUtc = now;
            this.lastApplied = null;
            this.lastSampleUtc = null;
            this.lastFlushUtc = now;
            this.currentPass = null;
            this.staleReported = false;
            this.savedSequences.Clear();
            this.knownMissing.Clear();
            this.knownMissing.UnionWith(this.collector.MissingSequences());
            this.accumulators.Clear();
            this.dirty.Clear();
            this.excursion.Clear();
            this.pendingTrace.Clear();
            this.lastTraceUtc = null;
        }
    }

    private async Task PollGuardedAsync(MachineStateSnapshot snapshot)
    {
        if (!await this.gate.WaitAsync(0).ConfigureAwait(false))
        {
            return; // 上一拍还没写完：跳过这一拍，倍率按时间加权，不会因此偏。
        }

        try
        {
            await PollCoreAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // 记录写不进去不影响机床；下一拍再写。
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task PollCoreAsync(MachineStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.ConnectionState != GatewayConnectionState.Connected)
        {
            return;
        }

        bool running = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState) is double channel && (int)channel == (int)NcChannelState.Running;
        if (this.recordId is null)
        {
            if (!running || this.resumeTried)
            {
                return;
            }

            // 上位机重启过：接着最近一条没结束的记录记（下发序号从记录的 NC 信息里取）。
            this.resumeTried = true;
            if (!await TryResumeAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        string record = this.recordId!;
        string job = this.jobId!;
        DateTimeOffset now = snapshot.CapturedAtUtc;

        // NC 确认了这一份下发（循环启动时锁存序号）。
        if (!this.accepted && this.downloadSequence is int expected
            && snapshot.GetNumberOrNull(MachineTagKeys.JobAcceptedSeq) is double acceptedSeq && (int)acceptedSeq == expected)
        {
            this.accepted = true;
            await this.runLog.SetNcInfoAsync(record, new RecordNcInfo(expected, now), cancellationToken).ConfigureAwait(false);
            await this.runLog.AddEventAsync(record, new RunEvent(now, RunEventKinds.NcAccepted, null, null, expected, null), cancellationToken).ConfigureAwait(false);
        }

        int step = (int)(snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentStepOrder) ?? 0.0);

        // job.currentPass 是这一道工序已走完的道次数；正在磨的是下一道（从 1 起，与道次行第 2 格一致）。
        int pass = (int)(snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentPass) ?? 0.0) + 1;

        await SampleOverridesAsync(record, snapshot, running, step, pass, now, cancellationToken).ConfigureAwait(false);
        SampleTrace(snapshot, running, now);
        await TrackEditsAsync(record, job, snapshot, step, pass, now, cancellationToken).ConfigureAwait(false);
        await SavePassRowsAsync(record, job, now, cancellationToken).ConfigureAwait(false);
        await WatchdogAsync(record, cancellationToken).ConfigureAwait(false);

        if (now - this.lastFlushUtc >= FlushEvery)
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
            this.lastFlushUtc = now;
        }
    }

    private async Task<bool> TryResumeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GrindingRecord> recent = await this.records
            .QueryAsync(DateTimeOffset.UnixEpoch, this.timeProvider.GetUtcNow().AddDays(1), 1, cancellationToken)
            .ConfigureAwait(false);
        if (recent.Count == 0 || recent[0].FinishedAtUtc is not null)
        {
            return false;
        }

        RecordNcInfo? info = await this.runLog.GetNcInfoAsync(recent[0].RecordId, cancellationToken).ConfigureAwait(false);
        Start(recent[0].RecordId, recent[0].JobId, info?.DownloadSequence, recent[0].StartedAtUtc);
        lock (this.stateGate)
        {
            this.accepted = info?.AcceptedAtUtc is not null;
            foreach (int saved in this.collector.Rows().Select(row => row.Sequence))
            {
                // 重启前已经存过的不重存（INSERT OR REPLACE 也不怕重复，只是少写一次）。
                this.savedSequences.Add(saved);
            }
        }

        foreach (RecordPassRow row in await this.runLog.ListPassesAsync(recent[0].RecordId, cancellationToken).ConfigureAwait(false))
        {
            this.savedSequences.Add(row.Sequence);
        }

        return true;
    }

    /// <summary>倍率按道次累计（时间加权）；越界逐条记；一道走完时整道不在 100 % 的记一条。</summary>
    private async Task SampleOverridesAsync(
        string record, MachineStateSnapshot snapshot, bool running, int step, int pass, DateTimeOffset now, CancellationToken cancellationToken)
    {
        TimeSpan dt = this.lastSampleUtc is DateTimeOffset last ? now - last : TimeSpan.Zero;
        this.lastSampleUtc = now;
        if (dt < TimeSpan.Zero || dt > MaxGap)
        {
            dt = TimeSpan.Zero;
        }

        if (!running || step <= 0)
        {
            return;
        }

        if (this.currentPass is { } previous && previous != (step, pass))
        {
            await ClosePassAsync(record, previous, now, cancellationToken).ConfigureAwait(false);
        }

        this.currentPass = (step, pass);
        foreach ((string kind, string tag) in OverrideTags)
        {
            if (snapshot.GetNumberOrNull(tag) is not double value || !double.IsFinite(value))
            {
                continue;
            }

            lock (this.stateGate)
            {
                var key = (step, pass, kind);
                if (!this.accumulators.TryGetValue(key, out Accumulator? accumulator))
                {
                    accumulator = new Accumulator();
                    this.accumulators[key] = accumulator;
                }

                accumulator.Add(value, dt.TotalSeconds);
                this.dirty.Add(key);
            }

            string? state = value < LowLimitPercent ? RunEventKinds.OverrideLow : value > HighLimitPercent ? RunEventKinds.OverrideHigh : null;
            this.excursion.TryGetValue(kind, out string? before);
            if (state is not null && state != before)
            {
                await this.runLog.AddEventAsync(record, new RunEvent(now, state, step, pass, value, kind), cancellationToken).ConfigureAwait(false);
            }

            this.excursion[kind] = state;
        }
    }

    private async Task ClosePassAsync(string record, (int Step, int Pass) finished, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach ((string kind, _) in OverrideTags)
        {
            Accumulator? accumulator;
            lock (this.stateGate)
            {
                this.accumulators.TryGetValue((finished.Step, finished.Pass, kind), out accumulator);
            }

            if (accumulator is { Seconds: > 0.0 } && accumulator.OffNominalShare >= 0.999)
            {
                await this.runLog.AddEventAsync(
                    record, new RunEvent(now, RunEventKinds.OverrideWholePass, finished.Step, finished.Pass, accumulator.Mean, kind),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>过程曲线：加工中每 <see cref="TraceInterval"/> 取一点（电流、在线直径、拖板 Z）。</summary>
    private void SampleTrace(MachineStateSnapshot snapshot, bool running, DateTimeOffset now)
    {
        if (!running || (this.lastTraceUtc is DateTimeOffset last && now - last < TraceInterval))
        {
            return;
        }

        this.lastTraceUtc = now;
        double seconds = Math.Round((now - this.beginAtUtc).TotalSeconds, 1);
        foreach ((string kind, string? key) in new[]
        {
            (TraceKinds.Current, MachineTagKeys.GrindingCurrentA),
            (TraceKinds.Diameter, MachineTagKeys.MeasuredDiameterMm),
            (TraceKinds.CarriageZ, this.carriageZKey),
        })
        {
            if (key is not null && snapshot.GetNumberOrNull(key) is double value && double.IsFinite(value))
            {
                lock (this.stateGate)
                {
                    this.pendingTrace.Add(new TracePoint(kind, seconds, value));
                }
            }
        }
    }

    /// <summary>已采用的改参数序号追上了：运行中调整记下从第几道起生效（接口约定第 3 节"记录写入生效道次"）。</summary>
    private async Task TrackEditsAsync(
        string record, string job, MachineStateSnapshot snapshot, int step, int pass, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (snapshot.GetNumberOrNull(MachineTagKeys.JobEditAppliedSeq) is not double appliedValue)
        {
            return;
        }

        int applied = (int)appliedValue;
        if (this.lastApplied is not int before)
        {
            this.lastApplied = applied; // 第一拍只作基准：下发时已对齐到 NC 已采用的序号。
            return;
        }

        if (applied <= before)
        {
            return;
        }

        this.lastApplied = applied;
        int marked = await this.jobs.MarkAdjustmentsEffectiveAsync(job, applied, step, pass, cancellationToken).ConfigureAwait(false);
        if (marked > 0)
        {
            await this.runLog.AddEventAsync(
                record, new RunEvent(now, RunEventKinds.EditApplied, step, pass, applied, null), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>道次行存进记录；缺号记事件（"道次表不完整，缺第 x–y 道"）。</summary>
    private async Task SavePassRowsAsync(string record, string job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RecordPassRow[] fresh = this.collector.Rows()
            .Where(row => row.ReadAtUtc >= this.beginAtUtc && !this.savedSequences.Contains(row.Sequence))
            .Select(ToRecord)
            .ToArray();
        if (fresh.Length > 0)
        {
            await this.runLog.AppendPassesAsync(record, fresh, cancellationToken).ConfigureAwait(false);
            foreach (RecordPassRow row in fresh)
            {
                this.savedSequences.Add(row.Sequence);

                // 第 15 格：这一道开始时已采用的序号——断线后也能补出精确的生效道次。
                if (row.EditAppliedSeq is int applied)
                {
                    await this.jobs.MarkAdjustmentsEffectiveAsync(job, applied, row.StepOrder, row.Pass, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        int[] missing = this.collector.MissingSequences().Where(sequence => !this.knownMissing.Contains(sequence)).ToArray();
        if (missing.Length > 0)
        {
            this.knownMissing.UnionWith(missing);
            await this.runLog.AddEventAsync(
                record, new RunEvent(now, RunEventKinds.PassMissing, null, null, missing.Length, Ranges(missing)), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WatchdogAsync(string record, CancellationToken cancellationToken)
    {
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        bool stale = this.collector.IsStale(now);
        if (stale && !this.staleReported)
        {
            await this.runLog.AddEventAsync(record, new RunEvent(now, RunEventKinds.PassDataStale, null, null, null, null), cancellationToken)
                .ConfigureAwait(false);
        }

        this.staleReported = stale;
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        string? record;
        OverrideSummaryRow[] rows;
        TracePoint[] trace;
        lock (this.stateGate)
        {
            record = this.recordId;
            rows = this.dirty
                .Where(this.accumulators.ContainsKey)
                .Select(key => this.accumulators[key].ToRow(key.Step, key.Pass, key.Kind))
                .Where(row => row.Seconds > 0.0)
                .ToArray();
            this.dirty.Clear();
            trace = this.pendingTrace.ToArray();
            this.pendingTrace.Clear();
        }

        if (record is not null && rows.Length > 0)
        {
            await this.runLog.SaveOverridesAsync(record, rows, cancellationToken).ConfigureAwait(false);
        }

        if (record is not null && trace.Length > 0)
        {
            await this.runLog.AppendTraceAsync(record, trace, cancellationToken).ConfigureAwait(false);
        }
    }

    private static RecordPassRow ToRecord(PassRowRecord row) => new(
        row.Sequence, row.StepOrder, row.Pass,
        row.Left.ZMm, row.Left.DwellSeconds, row.Left.FeedOverridePercent, row.Left.XMm,
        row.Right.ZMm, row.Right.DwellSeconds, row.Right.FeedOverridePercent, row.Right.XMm,
        row.CompensationMicrometer, row.StrokeVersion, row.DrfOffsetMm, row.MeanCurrentA, row.EditAppliedSeq, row.ReadAtUtc);

    /// <summary>"3–5, 9"。</summary>
    public static string Ranges(IEnumerable<int> sequences)
    {
        int[] sorted = sequences.Distinct().OrderBy(value => value).ToArray();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Length;)
        {
            int start = sorted[i];
            int end = start;
            while (i + 1 < sorted.Length && sorted[i + 1] == end + 1)
            {
                end = sorted[++i];
            }

            parts.Add(start == end
                ? start.ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{start}–{end}"));
            i++;
        }

        return string.Join(", ", parts);
    }

    /// <summary>一道里一个倍率的时间加权累计。</summary>
    private sealed class Accumulator
    {
        private double weighted;
        private double offSeconds;
        private double last = double.NaN;

        public double Seconds { get; private set; }

        public double Min { get; private set; } = double.PositiveInfinity;

        public double Max { get; private set; } = double.NegativeInfinity;

        public double Mean => Seconds > 0.0 ? this.weighted / Seconds : double.IsNaN(this.last) ? 100.0 : this.last;

        public double OffNominalShare => Seconds > 0.0 ? this.offSeconds / Seconds : 0.0;

        public void Add(double value, double seconds)
        {
            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
            this.last = value;
            if (seconds <= 0.0)
            {
                return;
            }

            Seconds += seconds;
            this.weighted += value * seconds;
            if (Math.Abs(value - 100.0) > 0.5)
            {
                this.offSeconds += seconds;
            }
        }

        public OverrideSummaryRow ToRow(int step, int pass, string kind) => new(
            step, pass, kind, Mean,
            double.IsFinite(Min) ? Min : Mean,
            double.IsFinite(Max) ? Max : Mean,
            OffNominalShare, Seconds);
    }
}
