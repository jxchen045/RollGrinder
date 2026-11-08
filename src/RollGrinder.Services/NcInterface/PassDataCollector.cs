using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.NcInterface;

/// <summary>一次折返时 NC 锁存的实际值。读不到（NaN、未映射）为空。</summary>
/// <param name="ZMm">实际折返 Z（mm）。</param>
/// <param name="DwellSeconds">实际停留时间（s）。</param>
/// <param name="FeedOverridePercent">折返时的进给倍率（%）。</param>
/// <param name="XMm">折返时的实际 X（mm）。</param>
public sealed record PassReversal(double? ZMm, double? DwellSeconds, double? FeedOverridePercent, double? XMm);

/// <summary>道次缓冲里的一行（接口约定 4.2）：一道往复，左右折返各一组。</summary>
/// <param name="Sequence">行序号（作业开始从 1 起）。</param>
/// <param name="StepOrder">工序序号。</param>
/// <param name="Pass">工序内的道次（从 1 起）。</param>
/// <param name="Left">左折返。</param>
/// <param name="Right">右折返。</param>
/// <param name="CompensationMicrometer">本道行程间补偿量（µm，半径）。</param>
/// <param name="StrokeVersion">补偿版本号。</param>
/// <param name="DrfOffsetMm">DRF 偏置（X，mm）；自动时禁用则为 0。</param>
/// <param name="MeanCurrentA">本道平均磨削电流（A）。</param>
/// <param name="ReadAtUtc">上位机读到这一行的时刻（NC 不给时间）。</param>
public sealed record PassRowRecord(
    int Sequence,
    int StepOrder,
    int Pass,
    PassReversal Left,
    PassReversal Right,
    double? CompensationMicrometer,
    int? StrokeVersion,
    double? DrfOffsetMm,
    double? MeanCurrentA,
    DateTimeOffset ReadAtUtc)
{
    /// <summary>这一道开始时 NC 已采用的改参数序号（第 15 格）；NC 不写为 null。</summary>
    public int? EditAppliedSeq { get; init; }
}

/// <summary>道次缓冲两边的进度。</summary>
/// <param name="IsMapped">tagmap 有没有映射道次缓冲。</param>
/// <param name="SlotCount">NC 的槽位数。</param>
/// <param name="WriteSeq">NC 已写的最后序号。</param>
/// <param name="ReadSeq">上位机已存的最后序号。</param>
/// <param name="OverflowCount">NC 报的溢出次数。</param>
/// <param name="MissingCount">本作业缺了几道（被覆盖或读到一半被改写）。</param>
/// <param name="LastUpdateUtc">最后一次收到监视快照的时刻。</param>
public sealed record PassBufferStatus(
    bool IsMapped,
    int SlotCount,
    int WriteSeq,
    int ReadSeq,
    int OverflowCount,
    int MissingCount,
    DateTimeOffset? LastUpdateUtc);

/// <summary>
/// 道次数据读取（接口约定第 4 节）：NC 在折返时把实际值锁存进环形缓冲，**从不等上位机**；
/// 这里每拍看一眼写序号，有新行就按"读序号 → 读数据 → 再读序号"读走，写回读序号。
/// 只用于记录与追溯——读不到、断了、卡了，机床都照常磨完，这里只记缺号、只提示。
/// </summary>
public interface IPassDataCollector
{
    /// <summary>本作业已读到的行，按序号。</summary>
    IReadOnlyList<PassRowRecord> Rows();

    /// <summary>本作业缺的序号（缓冲溢出被覆盖，或读到一半被改写）。</summary>
    IReadOnlyList<int> MissingSequences();

    /// <summary>当前进度。</summary>
    PassBufferStatus Status { get; }

    /// <summary>
    /// 看门狗：机床在加工、但连续 <c>watchdog</c> 秒没有新快照，返回 true。
    /// **只提示**——界面写"记录数据暂缺，机床照常磨"，不干预机床。
    /// </summary>
    bool IsStale(DateTimeOffset now);

    /// <summary>按一份监视快照读新行（监视服务每拍调用；测试直接调）。</summary>
    Task PollAsync(MachineStateSnapshot snapshot, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPassDataCollector"/>
public sealed class PassDataCollector : IPassDataCollector
{
    /// <summary>看门狗默认门限（接口约定 4.4：默认 10 s）。</summary>
    public static readonly TimeSpan DefaultWatchdog = TimeSpan.FromSeconds(10);

    /// <summary>一支辊最多存多少行：几十道一支，留足余量也防忘了清。</summary>
    public const int Capacity = 4000;

    private readonly IMachineGateway gateway;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan watchdog;
    private readonly object gate = new();
    private readonly List<PassRowRecord> rows = new();
    private readonly List<int> missing = new();
    private int busy;
    private int readSeq;
    private PassBufferStatus status = new(false, 0, 0, 0, 0, 0, null);
    private bool running;

    public PassDataCollector(IMachineGateway gateway, IMachineMonitor monitor, TimeProvider timeProvider)
        : this(gateway, monitor, timeProvider, DefaultWatchdog)
    {
    }

    public PassDataCollector(
        IMachineGateway gateway, IMachineMonitor? monitor, TimeProvider timeProvider, TimeSpan watchdog)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.watchdog = watchdog > TimeSpan.Zero ? watchdog : DefaultWatchdog;
        if (monitor is not null)
        {
            monitor.SnapshotUpdated += (_, snapshot) => _ = PollGuardedAsync(snapshot);
        }
    }

    public PassBufferStatus Status
    {
        get
        {
            lock (this.gate)
            {
                return this.status;
            }
        }
    }

    public IReadOnlyList<PassRowRecord> Rows()
    {
        lock (this.gate)
        {
            return this.rows.ToArray();
        }
    }

    public IReadOnlyList<int> MissingSequences()
    {
        lock (this.gate)
        {
            return this.missing.ToArray();
        }
    }

    public bool IsStale(DateTimeOffset now)
    {
        lock (this.gate)
        {
            return this.running
                && this.status.LastUpdateUtc is DateTimeOffset last
                && now - last > this.watchdog;
        }
    }

    public async Task PollAsync(MachineStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool fresh = snapshot.ConnectionState == GatewayConnectionState.Connected;
        lock (this.gate)
        {
            if (fresh)
            {
                this.running = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState) is double channel
                    && (int)channel == (int)NcChannelState.Running;
                this.status = this.status with { LastUpdateUtc = snapshot.CapturedAtUtc };
            }
        }

        if (!fresh || snapshot.GetNumberOrNull(MachineTagKeys.PassWriteSeq) is not double writeValue)
        {
            return;
        }

        int writeSeq = (int)writeValue;
        int slots = Math.Max(1, (int)(snapshot.GetNumberOrNull(MachineTagKeys.PassSlotCount) ?? 16.0));
        int overflow = (int)(snapshot.GetNumberOrNull(MachineTagKeys.PassOverflowCount) ?? 0.0);
        int from;
        lock (this.gate)
        {
            // 写序号回落：NC 开始了新的作业，道次表从头记。
            if (writeSeq < this.readSeq)
            {
                this.rows.Clear();
                this.missing.Clear();
                this.readSeq = 0;
            }

            from = this.readSeq;
            this.status = this.status with
            {
                IsMapped = true,
                SlotCount = slots,
                WriteSeq = writeSeq,
                ReadSeq = this.readSeq,
                OverflowCount = overflow,
                MissingCount = this.missing.Count,
            };
        }

        if (writeSeq <= from)
        {
            return;
        }

        for (int sequence = from + 1; sequence <= writeSeq; sequence++)
        {
            PassRowRecord? row = writeSeq - sequence >= slots
                ? null // 已经被覆盖：这一行再也读不到了，记缺号，不去读别人的数据。
                : await ReadRowAsync(sequence, sequence % slots, cancellationToken).ConfigureAwait(false);

            lock (this.gate)
            {
                if (row is null)
                {
                    this.missing.Add(sequence);
                }
                else
                {
                    this.rows.Add(row);
                    if (this.rows.Count > Capacity)
                    {
                        this.rows.RemoveRange(0, this.rows.Count - Capacity);
                    }
                }

                this.readSeq = sequence;
            }
        }

        int done;
        lock (this.gate)
        {
            done = this.readSeq;
            this.status = this.status with { ReadSeq = done, MissingCount = this.missing.Count };
        }

        // 读序号只供 NC 判断溢出，NC 不因它停顿；写不进去也不影响这边的记录。
        await this.gateway.WriteTagAsync(
            MachineTagKeys.PassReadSeq,
            new TagValue(MachineTagKeys.PassReadSeq, TagDataType.Int32, done, this.timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PollGuardedAsync(MachineStateSnapshot snapshot)
    {
        // 上一拍还没读完就跳过这一拍：下一拍会接着读，不会丢（缓冲在 NC 那边）。
        if (Interlocked.Exchange(ref this.busy, 1) == 1)
        {
            return;
        }

        try
        {
            await PollAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GatewayException)
        {
            // 读不到就等下一拍；道次数据只用于记录，不报警打扰操作工（看门狗会提示）。
        }
        finally
        {
            Volatile.Write(ref this.busy, 0);
        }
    }

    /// <summary>读序号 → 读数据 → 再读序号：两次都等于期望序号才算完整的一行。</summary>
    private async Task<PassRowRecord?> ReadRowAsync(int sequence, int slot, CancellationToken cancellationToken)
    {
        string[] keys = Enumerable.Range(0, MachineTagKeys.PassRowFieldCount)
            .Select(field => MachineTagKeys.PassRowField(slot, field))
            .ToArray();

        MachineStateSnapshot data = await this.gateway.ReadStateAsync(keys, cancellationToken).ConfigureAwait(false);
        TagValue after = await this.gateway.ReadTagAsync(keys[0], cancellationToken).ConfigureAwait(false);

        double? Field(int index)
        {
            double? value = data.GetNumberOrNull(keys[index]);
            return value is double number && double.IsFinite(number) ? number : null;
        }

        int? before = Field(0) is double b ? (int)b : null;
        int? again = after.Raw is IConvertible convertible
            ? (int)convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        if (before != sequence || again != sequence)
        {
            return null;
        }

        return new PassRowRecord(
            sequence,
            (int)(Field(1) ?? 0.0),
            (int)(Field(2) ?? 0.0),
            new PassReversal(Field(3), Field(4), Field(5), Field(6)),
            new PassReversal(Field(7), Field(8), Field(9), Field(10)),
            Field(11),
            Field(12) is double version ? (int)version : null,
            Field(13),
            Field(14),
            this.timeProvider.GetUtcNow())
        {
            EditAppliedSeq = Field(15) is double applied && applied > 0 ? (int)applied : null,
        };
    }
}
