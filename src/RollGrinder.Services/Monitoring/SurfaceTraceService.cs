using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Services.Monitoring;

/// <summary>沿辊身收集的一类量。</summary>
public enum SurfaceTraceKind
{
    /// <summary>圆度（µm，峰谷值）。</summary>
    Roundness = 0,

    /// <summary>偏心量（µm）。</summary>
    Eccentricity = 1,

    /// <summary>磨削电流（A）。</summary>
    GrindingCurrent = 2,

    /// <summary>
    /// 测得的直径（mm）。
    ///
    /// 与前三个不同：这一条不是画曲线用的，是**测量工序扫过之后攒成一次测量**
    /// 存进库里的原料。测量臂沿辊身走一趟，这条轨迹就是那一趟的读数。
    /// </summary>
    Diameter = 3,
}

/// <summary>轨迹上的一个点。</summary>
/// <param name="BodyPositionMm">辊身坐标（mm）。</param>
/// <param name="Value">该位置上的取值，单位由 <see cref="SurfaceTraceKind"/> 决定。</param>
public readonly record struct SurfaceTracePoint(double BodyPositionMm, double Value);

/// <summary>
/// 沿辊身收集机床报上来的量，攒成曲线。
///
/// 圆度与偏心量是**测量系统自己算好的**——上位机看不到原始的 r(θ)，
/// 只是在拖板走过时把它报的数按位置记下来。电流同理。
/// 所以这里没有任何算法，只有"在哪个位置、报了多少"。
///
/// tagmap 里没登记对应的变量时，轨迹恒为空，界面照实说"通道未配置"，
/// 不画一条编出来的线。
/// </summary>
public interface ISurfaceTraceService
{
    /// <summary>取某一类量的轨迹快照，按辊身坐标从小到大。</summary>
    IReadOnlyList<SurfaceTracePoint> Trace(SurfaceTraceKind kind);

    /// <summary>这一类量在 tagmap 里登记了没有。</summary>
    bool IsAvailable(SurfaceTraceKind kind);

    /// <summary>换了一支辊：清空并按新的辊身长度重新划格。</summary>
    void Reset(double bodyLengthMm);

    /// <summary>
    /// 只清掉某一类的轨迹，辊身长度不变。
    ///
    /// 直径轨迹用得到：一次测量扫完存进库之后就该清空，
    /// 不然下一次测量会把上一趟没走到的格子当成这一趟的数。
    /// </summary>
    void Clear(SurfaceTraceKind kind);
}

/// <inheritdoc cref="ISurfaceTraceService"/>
public sealed class SurfaceTraceService : ISurfaceTraceService
{
    private static readonly SurfaceTraceKind[] AllKinds =
    {
        SurfaceTraceKind.Roundness, SurfaceTraceKind.Eccentricity,
        SurfaceTraceKind.GrindingCurrent, SurfaceTraceKind.Diameter,
    };

    private readonly ITagMap tagMap;
    private readonly MachineDescription machine;
    private readonly int binCount;
    private readonly object gate = new();

    /// <summary>每一类量一排格子，一格存最近一次经过时报的值；null 表示这格还没走到过。</summary>
    private readonly Dictionary<SurfaceTraceKind, double?[]> bins = new();

    private double bodyLengthMm;

    public SurfaceTraceService(IMachineMonitor monitor, ITagMap tagMap, MachineDescription machine, HmiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(settings);
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));

        // 与辊形曲线用同一个采样点数：两条线叠在一起看时分辨率一致。
        this.binCount = Math.Max(2, settings.ProfileSampleCount);
        foreach (SurfaceTraceKind kind in AllKinds)
        {
            this.bins[kind] = new double?[this.binCount];
        }

        monitor.SnapshotUpdated += (_, snapshot) => Observe(snapshot);
    }

    /// <summary>某一类量对应的逻辑变量名。</summary>
    public static string TagKeyOf(SurfaceTraceKind kind) => kind switch
    {
        SurfaceTraceKind.Roundness => MachineTagKeys.MeasureRoundnessMicrometer,
        SurfaceTraceKind.Eccentricity => MachineTagKeys.MeasureEccentricityMicrometer,
        SurfaceTraceKind.GrindingCurrent => MachineTagKeys.GrindingCurrentA,
        SurfaceTraceKind.Diameter => MachineTagKeys.MeasuredDiameterMm,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public bool IsAvailable(SurfaceTraceKind kind) => this.tagMap.TryResolve(TagKeyOf(kind), out _);

    public IReadOnlyList<SurfaceTracePoint> Trace(SurfaceTraceKind kind)
    {
        lock (this.gate)
        {
            if (this.bodyLengthMm <= 0.0)
            {
                return Array.Empty<SurfaceTracePoint>();
            }

            double?[] values = this.bins[kind];
            var points = new List<SurfaceTracePoint>(values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] is double value)
                {
                    points.Add(new SurfaceTracePoint(PositionOfBin(i), value));
                }
            }

            return points;
        }
    }

    public void Clear(SurfaceTraceKind kind)
    {
        lock (this.gate)
        {
            Array.Clear(this.bins[kind]);
        }
    }

    public void Reset(double bodyLengthMm)
    {
        lock (this.gate)
        {
            this.bodyLengthMm = bodyLengthMm;
            foreach (SurfaceTraceKind kind in AllKinds)
            {
                Array.Clear(this.bins[kind]);
            }
        }
    }

    /// <summary>
    /// 收下一拍：拖板停在哪一格，就把这一拍报上来的几个数记到那一格。
    /// 同一格再走一次就覆盖——看的是这一趟磨成什么样，不是历史平均。
    /// </summary>
    private void Observe(MachineStateSnapshot snapshot)
    {
        if (snapshot is null || snapshot.ConnectionState != GatewayConnectionState.Connected)
        {
            return;
        }

        double? position = CarriagePosition(snapshot);
        if (position is null)
        {
            return;
        }

        lock (this.gate)
        {
            if (this.bodyLengthMm <= 0.0)
            {
                return;
            }

            int bin = BinOf(position.Value);
            if (bin < 0)
            {
                // 拖板跑到辊身之外（退到换辊位之类）：那不是辊面上的点，不记。
                return;
            }

            foreach (SurfaceTraceKind kind in AllKinds)
            {
                if (snapshot.GetNumberOrNull(TagKeyOf(kind)) is double value)
                {
                    this.bins[kind][bin] = value;
                }
            }
        }
    }

    private double? CarriagePosition(MachineStateSnapshot snapshot)
    {
        foreach (AxisDescription axis in this.machine.Axes)
        {
            if (axis.IsPresent && string.Equals(axis.Role, MachineAxisRoles.Carriage, StringComparison.Ordinal))
            {
                return snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(axis.Name));
            }
        }

        return null;
    }

    private int BinOf(double bodyPositionMm)
    {
        if (bodyPositionMm < 0.0 || bodyPositionMm > this.bodyLengthMm)
        {
            return -1;
        }

        int bin = (int)Math.Round(bodyPositionMm / this.bodyLengthMm * (this.binCount - 1));
        return Math.Clamp(bin, 0, this.binCount - 1);
    }

    private double PositionOfBin(int bin) => this.bodyLengthMm * bin / (this.binCount - 1);
}
