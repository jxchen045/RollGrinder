using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Monitoring;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 沿辊身收集圆度、偏心与电流。
///
/// 这三条曲线上位机不参与计算：数是测量系统与驱动报上来的，
/// 服务只负责"拖板走到哪、报了多少"。所以测试盯的也是这件事。
/// </summary>
public sealed class SurfaceTraceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private const string CarriageAxis = "Z";

    /// <summary>可以手动推快照的监视器。</summary>
    private sealed class PushMonitor : IMachineMonitor
    {
        public MachineStateSnapshot Current { get; private set; } = MachineStateSnapshot.Empty(Now);

        public event EventHandler<MachineStateSnapshot>? SnapshotUpdated;

        public void Push(MachineStateSnapshot snapshot)
        {
            Current = snapshot;
            SnapshotUpdated?.Invoke(this, snapshot);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class KeyedTagMap : ITagMap
    {
        private readonly HashSet<string> keys;

        public KeyedTagMap(IEnumerable<string> keys)
        {
            this.keys = keys.ToHashSet(StringComparer.Ordinal);
            Tags = this.keys.Select(Describe).ToArray();
        }

        public IReadOnlyList<TagDescriptor> Tags { get; }

        public bool TryResolve(string logicalName, out TagDescriptor? descriptor)
        {
            descriptor = this.keys.Contains(logicalName) ? Describe(logicalName) : null;
            return descriptor is not null;
        }

        public TagDescriptor Resolve(string logicalName) =>
            TryResolve(logicalName, out TagDescriptor? descriptor) && descriptor is not null
                ? descriptor
                : throw new GatewayException($"Tag '{logicalName}' is not mapped.");

        private static TagDescriptor Describe(string key) =>
            new(key, "sim://" + key, TagDataType.Double, TagAccess.Read);
    }

    private static readonly string[] AllTraceKeys =
    {
        MachineTagKeys.MeasureRoundnessMicrometer,
        MachineTagKeys.MeasureEccentricityMicrometer,
        MachineTagKeys.GrindingCurrentA,
    };

    private static MachineDescription Machine() => new(
        1, "M-1", "test", new ControllerDescription("SinumerikOne", 1),
        new[] { new AxisDescription(CarriageAxis, MachineAxisRoles.Carriage, true, AxisClosedLoopKind.FullClosed) },
        Array.Empty<MeasurementChannelDescription>(),
        new Dictionary<string, bool>(),
        new Dictionary<string, double>(),
        new WorkpieceLimits(100.0, 3000.0, 100.0, 9000.0, 200000.0),
        new Dictionary<string, int>());

    private static HmiSettings Settings(int sampleCount = 11) => new(
        1, "zh-CN", 200, 8, sampleCount, 600, 365, 200, 0.6, 5, UserRole.Operator, 1);

    private static MachineStateSnapshot At(
        double carriagePositionMm,
        double? roundnessMicrometer = null,
        double? eccentricityMicrometer = null,
        double? currentA = null,
        GatewayConnectionState connection = GatewayConnectionState.Connected)
    {
        var values = new List<TagValue>
        {
            new(MachineTagKeys.AxisActualPositionMm(CarriageAxis), TagDataType.Double, carriagePositionMm, Now),
        };

        if (roundnessMicrometer is double roundness)
        {
            values.Add(new TagValue(MachineTagKeys.MeasureRoundnessMicrometer, TagDataType.Double, roundness, Now));
        }

        if (eccentricityMicrometer is double eccentricity)
        {
            values.Add(new TagValue(
                MachineTagKeys.MeasureEccentricityMicrometer, TagDataType.Double, eccentricity, Now));
        }

        if (currentA is double current)
        {
            values.Add(new TagValue(MachineTagKeys.GrindingCurrentA, TagDataType.Double, current, Now));
        }

        return new MachineStateSnapshot(Now, connection, values);
    }

    private static (PushMonitor Monitor, SurfaceTraceService Service) Create(
        IEnumerable<string>? mappedKeys = null, int sampleCount = 11)
    {
        var monitor = new PushMonitor();
        var service = new SurfaceTraceService(
            monitor, new KeyedTagMap(mappedKeys ?? AllTraceKeys), Machine(), Settings(sampleCount));
        return (monitor, service);
    }

    [Fact]
    public void Nothing_is_collected_until_a_roll_is_loaded()
    {
        // 不知道辊身多长就没法划格，这时候收进来的点也说不清落在哪里。
        (PushMonitor monitor, SurfaceTraceService service) = Create();

        monitor.Push(At(500.0, roundnessMicrometer: 8.0));

        service.Trace(SurfaceTraceKind.Roundness).Should().BeEmpty();
    }

    [Fact]
    public void Each_tick_records_what_the_machine_reported_at_that_position()
    {
        (PushMonitor monitor, SurfaceTraceService service) = Create();
        service.Reset(1000.0);

        monitor.Push(At(0.0, roundnessMicrometer: 8.0, eccentricityMicrometer: 12.0, currentA: 40.0));
        monitor.Push(At(1000.0, roundnessMicrometer: 6.0, eccentricityMicrometer: 14.0, currentA: 44.0));

        service.Trace(SurfaceTraceKind.Roundness).Should().Equal(
            new SurfaceTracePoint(0.0, 8.0), new SurfaceTracePoint(1000.0, 6.0));
        service.Trace(SurfaceTraceKind.Eccentricity).Should().Equal(
            new SurfaceTracePoint(0.0, 12.0), new SurfaceTracePoint(1000.0, 14.0));
        service.Trace(SurfaceTraceKind.GrindingCurrent).Should().Equal(
            new SurfaceTracePoint(0.0, 40.0), new SurfaceTracePoint(1000.0, 44.0));
    }

    [Fact]
    public void A_second_pass_over_the_same_place_overwrites_the_first()
    {
        // 看的是这一趟磨成什么样，不是历史平均——旧值留着只会让曲线滞后。
        (PushMonitor monitor, SurfaceTraceService service) = Create();
        service.Reset(1000.0);

        monitor.Push(At(500.0, roundnessMicrometer: 9.0));
        monitor.Push(At(500.0, roundnessMicrometer: 4.0));

        service.Trace(SurfaceTraceKind.Roundness).Should().Equal(new SurfaceTracePoint(500.0, 4.0));
    }

    [Fact]
    public void Positions_outside_the_roll_body_are_not_points_on_the_roll()
    {
        // 拖板退到换辊位时报的数不是辊面上的点，记进去曲线两端会翘。
        (PushMonitor monitor, SurfaceTraceService service) = Create();
        service.Reset(1000.0);

        monitor.Push(At(-50.0, roundnessMicrometer: 99.0));
        monitor.Push(At(1200.0, roundnessMicrometer: 99.0));

        service.Trace(SurfaceTraceKind.Roundness).Should().BeEmpty();
    }

    [Fact]
    public void A_disconnected_snapshot_is_ignored()
    {
        (PushMonitor monitor, SurfaceTraceService service) = Create();
        service.Reset(1000.0);

        monitor.Push(At(500.0, roundnessMicrometer: 9.0, connection: GatewayConnectionState.Disconnected));

        service.Trace(SurfaceTraceKind.Roundness).Should().BeEmpty();
    }

    [Fact]
    public void A_new_roll_starts_from_an_empty_trace()
    {
        // 上一支辊收来的圆度与这一支无关，留着就是骗人。
        (PushMonitor monitor, SurfaceTraceService service) = Create();
        service.Reset(1000.0);
        monitor.Push(At(500.0, roundnessMicrometer: 9.0));

        service.Reset(1000.0);

        service.Trace(SurfaceTraceKind.Roundness).Should().BeEmpty();
    }

    [Fact]
    public void An_unmapped_channel_is_reported_as_unavailable()
    {
        // "没登记这个通道"与"还没走过"是两回事，界面要分开说。
        (PushMonitor monitor, SurfaceTraceService service) = Create(
            new[] { MachineTagKeys.GrindingCurrentA });
        service.Reset(1000.0);

        service.IsAvailable(SurfaceTraceKind.Roundness).Should().BeFalse();
        service.IsAvailable(SurfaceTraceKind.GrindingCurrent).Should().BeTrue();
    }

    [Fact]
    public void A_channel_the_machine_never_reports_leaves_its_trace_empty()
    {
        // 登记了但机床一直不报（通道坏了）：曲线空着，不拿别的量凑数。
        (PushMonitor monitor, SurfaceTraceService service) = Create();
        service.Reset(1000.0);

        monitor.Push(At(500.0, currentA: 40.0));

        service.Trace(SurfaceTraceKind.GrindingCurrent).Should().HaveCount(1);
        service.Trace(SurfaceTraceKind.Roundness).Should().BeEmpty();
    }

    [Fact]
    public void The_trace_comes_back_ordered_along_the_body()
    {
        // 画线的人不该再排一次序；乱序画出来是一团折线。
        (PushMonitor monitor, SurfaceTraceService service) = Create();
        service.Reset(1000.0);

        foreach (double position in new[] { 800.0, 200.0, 1000.0, 0.0, 600.0 })
        {
            monitor.Push(At(position, roundnessMicrometer: position / 100.0));
        }

        service.Trace(SurfaceTraceKind.Roundness)
            .Select(point => point.BodyPositionMm)
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public void The_grid_is_as_fine_as_the_profile_curve()
    {
        // 与辊形曲线同一个采样点数：两条线叠着看时分辨率一致。
        (PushMonitor monitor, SurfaceTraceService service) = Create(sampleCount: 5);
        service.Reset(1000.0);

        // 5 个格子 ⇒ 格心是 0、250、500、750、1000；240 与 260 都落进 250 那一格。
        monitor.Push(At(240.0, roundnessMicrometer: 3.0));
        monitor.Push(At(260.0, roundnessMicrometer: 7.0));

        service.Trace(SurfaceTraceKind.Roundness).Should().Equal(new SurfaceTracePoint(250.0, 7.0));
    }
}
