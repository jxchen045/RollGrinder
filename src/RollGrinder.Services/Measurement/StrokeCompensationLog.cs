using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Measurement;

/// <summary>NC 的一次行程间修正：第几版、何时、修正量（半径量 mm）。</summary>
/// <param name="Version">NC 报的行程版本号，每修正一次加一。</param>
/// <param name="ObservedAtUtc">上位机看到它的时刻。</param>
/// <param name="OffsetMm">NC 报的实时修正量（半径量 mm）；读不到为空。</param>
public sealed record StrokeCorrection(int Version, DateTimeOffset ObservedAtUtc, double? OffsetMm);

/// <summary>
/// 行程间补偿的收敛过程（修改稿 5.5 补偿子视图）：NC 每修正一次，行程版本号加一，
/// 这里记下那一刻的修正量。只是**看**——修正是 NC 自己做的，上位机不在这个回路里，
/// 上位机关掉了 NC 照样修。记在内存里，新程序开始（版本号回落）就重新记。
/// </summary>
public interface IStrokeCompensationLog
{
    /// <summary>当前这支辊到目前为止的修正，按版本号从小到大。</summary>
    IReadOnlyList<StrokeCorrection> Snapshot();

    /// <summary>喂一拍快照。监视服务每取一次数调用一次；测试也直接调。</summary>
    void Observe(MachineStateSnapshot snapshot);
}

/// <inheritdoc cref="IStrokeCompensationLog"/>
public sealed class StrokeCompensationLog : IStrokeCompensationLog
{
    /// <summary>最多记多少次：一支辊几百个行程足够了，再多就是忘了清。</summary>
    public const int Capacity = 500;

    private readonly object gate = new();
    private readonly List<StrokeCorrection> corrections = new();

    public StrokeCompensationLog(IMachineMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        monitor.SnapshotUpdated += (_, snapshot) => Observe(snapshot);
    }

    public IReadOnlyList<StrokeCorrection> Snapshot()
    {
        lock (this.gate)
        {
            return this.corrections.ToArray();
        }
    }

    public void Observe(MachineStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.GetNumberOrNull(MachineTagKeys.CompensationStrokeVersion) is not double versionValue)
        {
            return;
        }

        int version = (int)versionValue;
        double? offsetMm = snapshot.GetNumberOrNull(MachineTagKeys.CompensationRealtimeOffsetMm);
        lock (this.gate)
        {
            int last = this.corrections.Count == 0 ? int.MinValue : this.corrections[^1].Version;
            if (version < last)
            {
                // 版本号回落：NC 开始了新的一支辊。
                this.corrections.Clear();
                last = int.MinValue;
            }

            if (version <= last || version <= 0)
            {
                return;
            }

            this.corrections.Add(new StrokeCorrection(version, snapshot.CapturedAtUtc, offsetMm));
            if (this.corrections.Count > Capacity)
            {
                this.corrections.RemoveRange(0, this.corrections.Count - Capacity);
            }
        }
    }
}
