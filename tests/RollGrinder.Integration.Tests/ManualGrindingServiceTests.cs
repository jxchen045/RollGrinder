using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Manual;
using RollGrinder.Services.Monitoring;
using RollGrinder.Sim;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 手动磨削（界面最终稿 5.1）：给定与倍率、拖板往复、定位、方式请求。
/// 上位机只写"请求"，PLC 按上升沿动作、命令位自复位——这里核对写出去的那一串，以及仿真机床怎么接。
/// </summary>
public sealed class ManualGrindingServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private sealed class RecordingGateway : IMachineGateway
    {
        public List<(string Key, object? Raw)> Writes { get; } = new();

        public GatewayConnectionState ConnectionState => GatewayConnectionState.Connected;

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<MachineStateSnapshot> ReadStateAsync(IReadOnlyList<string> logicalNames, CancellationToken cancellationToken) =>
            Task.FromResult(MachineStateSnapshot.Empty(Now));

        public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken) =>
            Task.FromResult(new TagValue(logicalName, TagDataType.Boolean, false, Now));

        public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken)
        {
            Writes.Add((logicalName, value.Raw));
            return Task.CompletedTask;
        }

        public Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticMonitor : IMachineMonitor
    {
        public StaticMonitor(MachineStateSnapshot snapshot) => Current = snapshot;

        public MachineStateSnapshot Current { get; }

        public event EventHandler<MachineStateSnapshot>? SnapshotUpdated;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            SnapshotUpdated?.Invoke(this, Current);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class KeyedTagMap : ITagMap
    {
        private readonly Dictionary<string, TagDataType> keys;

        public KeyedTagMap(IEnumerable<(string Key, TagDataType Type)> keys) =>
            this.keys = keys.ToDictionary(k => k.Key, k => k.Type, StringComparer.Ordinal);

        public IReadOnlyList<TagDescriptor> Tags => this.keys.Select(pair => Describe(pair.Key, pair.Value)).ToArray();

        public bool TryResolve(string logicalName, out TagDescriptor? descriptor)
        {
            descriptor = this.keys.TryGetValue(logicalName, out TagDataType type) ? Describe(logicalName, type) : null;
            return descriptor is not null;
        }

        public TagDescriptor Resolve(string logicalName) =>
            TryResolve(logicalName, out TagDescriptor? descriptor) && descriptor is not null
                ? descriptor
                : throw new GatewayException($"Tag '{logicalName}' is not mapped.");

        private static TagDescriptor Describe(string key, TagDataType type) => new(key, "sim://" + key, type, TagAccess.ReadWrite);
    }

    private static readonly (string Key, TagDataType Type)[] AllTags =
    {
        (MachineTagKeys.ManualWheelSurfaceSpeedSetpoint, TagDataType.Double),
        (MachineTagKeys.OverrideFeedPercent, TagDataType.Int32),
        (MachineTagKeys.ManualCarriageSpeed, TagDataType.Double),
        (MachineTagKeys.ManualCarriageStrokeStart, TagDataType.Double),
        (MachineTagKeys.ManualCarriageStrokeEnd, TagDataType.Double),
        (MachineTagKeys.ManualCarriageStart, TagDataType.Boolean),
        (MachineTagKeys.ManualCarriageStop, TagDataType.Boolean),
        (MachineTagKeys.ManualHeadstockAssist, TagDataType.Boolean),
        (MachineTagKeys.ManualPositionAxis, TagDataType.Int32),
        (MachineTagKeys.ManualPositionTarget, TagDataType.Double),
        (MachineTagKeys.ManualPositionSpeed, TagDataType.Double),
        (MachineTagKeys.ManualPositionStart, TagDataType.Boolean),
        (MachineTagKeys.ModeRequest, TagDataType.Int32),
    };

    private static MachineStateSnapshot Idle(NcChannelState state = NcChannelState.Reset) => new(
        Now,
        GatewayConnectionState.Connected,
        new[] { new TagValue(MachineTagKeys.ChannelState, TagDataType.Int32, (int)state, Now) });

    private static ManualGrindingService Create(RecordingGateway gateway, MachineStateSnapshot snapshot, IEnumerable<(string, TagDataType)>? tags = null) =>
        new(
            gateway,
            new StaticMonitor(snapshot),
            new KeyedTagMap(tags ?? AllTags),
            new HmiSettings(1, "zh-CN", 200, 8, 201, 600, 365, 200, 0.6, 5, UserRole.Operator, 1),
            TimeProvider.System);

    [Fact]
    public async Task A_setpoint_is_written_with_the_mapped_data_type()
    {
        var gateway = new RecordingGateway();
        ManualGrindingService service = Create(gateway, Idle());

        (await service.WriteValueAsync(MachineTagKeys.ManualWheelSurfaceSpeedSetpoint, 38.0, CancellationToken.None)).Succeeded.Should().BeTrue();
        (await service.WriteValueAsync(MachineTagKeys.OverrideFeedPercent, 84.6, CancellationToken.None)).Succeeded.Should().BeTrue();

        gateway.Writes.Should().Equal(
            (MachineTagKeys.ManualWheelSurfaceSpeedSetpoint, (object?)38.0),
            (MachineTagKeys.OverrideFeedPercent, (object?)85));
    }

    [Fact]
    public async Task Not_a_number_is_refused_before_anything_is_written()
    {
        var gateway = new RecordingGateway();
        ManualGrindingService service = Create(gateway, Idle());

        ManualCommandResult result = await service.WriteValueAsync(MachineTagKeys.OverrideFeedPercent, double.NaN, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ReasonResourceKey.Should().Be(ManualGrindingService.OutOfRangeResourceKey);
        gateway.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Carriage_traverse_writes_speed_and_stroke_then_pulses_start()
    {
        var gateway = new RecordingGateway();
        ManualGrindingService service = Create(gateway, Idle());

        (await service.StartReciprocationAsync(1200.0, 100.0, 1900.0, CancellationToken.None)).Succeeded.Should().BeTrue();

        gateway.Writes.Should().Equal(
            (MachineTagKeys.ManualCarriageSpeed, (object?)1200.0),
            (MachineTagKeys.ManualCarriageStrokeStart, (object?)100.0),
            (MachineTagKeys.ManualCarriageStrokeEnd, (object?)1900.0),
            (MachineTagKeys.ManualCarriageStart, (object?)true),
            (MachineTagKeys.ManualCarriageStart, (object?)false));
    }

    [Fact]
    public async Task Traverse_and_positioning_need_an_idle_channel_but_stop_never_does()
    {
        var gateway = new RecordingGateway();
        ManualGrindingService service = Create(gateway, Idle(NcChannelState.Running));

        (await service.StartReciprocationAsync(1200.0, 100.0, 1900.0, CancellationToken.None)).Outcome.Should().Be(ManualCommandOutcome.ChannelBusy);
        (await service.StartPositioningAsync(PositioningAxis.CarriageZ, 500.0, 2000.0, CancellationToken.None)).Outcome.Should().Be(ManualCommandOutcome.ChannelBusy);
        (await service.StopReciprocationAsync(CancellationToken.None)).Succeeded.Should().BeTrue("停止任何时候都要发得出去");
        gateway.Writes.Select(w => w.Key).Should().OnlyContain(key => key == MachineTagKeys.ManualCarriageStop);
    }

    [Fact]
    public async Task An_invalid_stroke_is_refused()
    {
        var gateway = new RecordingGateway();
        ManualGrindingService service = Create(gateway, Idle());

        (await service.StartReciprocationAsync(1200.0, 900.0, 100.0, CancellationToken.None)).Succeeded.Should().BeFalse();
        (await service.StartReciprocationAsync(0.0, 100.0, 900.0, CancellationToken.None)).Succeeded.Should().BeFalse();
        gateway.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Positioning_writes_axis_target_speed_then_pulses_start()
    {
        var gateway = new RecordingGateway();
        ManualGrindingService service = Create(gateway, Idle());

        (await service.StartPositioningAsync(PositioningAxis.MeasuringCarriageX1, 180.0, 2000.0, CancellationToken.None)).Succeeded.Should().BeTrue();

        gateway.Writes.Should().Equal(
            (MachineTagKeys.ManualPositionAxis, (object?)2),
            (MachineTagKeys.ManualPositionTarget, (object?)180.0),
            (MachineTagKeys.ManualPositionSpeed, (object?)2000.0),
            (MachineTagKeys.ManualPositionStart, (object?)true),
            (MachineTagKeys.ManualPositionStart, (object?)false));
    }

    [Fact]
    public async Task A_missing_tag_is_reported_by_name_and_nothing_is_written()
    {
        var gateway = new RecordingGateway();
        ManualGrindingService service = Create(gateway, Idle(), AllTags.Where(t => t.Key != MachineTagKeys.ManualCarriageStrokeEnd));

        service.FirstUnmapped(MachineTagKeys.ManualCarriageStart, MachineTagKeys.ManualCarriageStrokeEnd)
            .Should().Be(MachineTagKeys.ManualCarriageStrokeEnd);
        (await service.StartReciprocationAsync(1200.0, 100.0, 1900.0, CancellationToken.None)).Outcome.Should().Be(ManualCommandOutcome.NotMapped);
        gateway.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mode_request_is_a_plain_number()
    {
        var gateway = new RecordingGateway();
        ManualGrindingService service = Create(gateway, Idle(NcChannelState.Running));

        (await service.RequestModeAsync(MachineModeRequest.Auto, CancellationToken.None)).Succeeded.Should().BeTrue();
        gateway.Writes.Should().Equal((MachineTagKeys.ModeRequest, (object?)2));
    }

    private static async Task<SimulatedMachine> SimAsync()
    {
        using var workspace = new TempWorkspace();
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);
        return new SimulatedMachine(await new JsonMachineConfigProvider(options).GetMachineAsync(CancellationToken.None));
    }

    private static TagValue Value(string key, object raw) =>
        new(key, raw is bool ? TagDataType.Boolean : TagDataType.Double, raw, Now);

    [Fact]
    public async Task The_simulator_traverses_between_the_stroke_ends_until_stopped()
    {
        SimulatedMachine sim = await SimAsync();
        sim.Write(MachineTagKeys.ManualCarriageSpeed, Value(MachineTagKeys.ManualCarriageSpeed, 6000.0));
        sim.Write(MachineTagKeys.ManualCarriageStrokeStart, Value(MachineTagKeys.ManualCarriageStrokeStart, 100.0));
        sim.Write(MachineTagKeys.ManualCarriageStrokeEnd, Value(MachineTagKeys.ManualCarriageStrokeEnd, 400.0));
        sim.Write(MachineTagKeys.ManualCarriageStart, Value(MachineTagKeys.ManualCarriageStart, true));
        sim.Write(MachineTagKeys.ManualCarriageStart, Value(MachineTagKeys.ManualCarriageStart, false));

        sim.Read(MachineTagKeys.ManualCarriageRunning).Should().Be(true);
        var seen = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            sim.Advance(TimeSpan.FromMilliseconds(250));
            seen.Add((double)sim.Read(MachineTagKeys.AxisActualPositionMm("Z"))!);
        }

        seen.Should().OnlyContain(z => z >= 0.0 && z <= 400.0);
        seen.Max().Should().Be(400.0, "往复要走到行程终点再回头");
        seen.Skip(10).Min().Should().Be(100.0, "回头以后走到行程起点");

        sim.Write(MachineTagKeys.ManualCarriageStop, Value(MachineTagKeys.ManualCarriageStop, true));
        sim.Read(MachineTagKeys.ManualCarriageRunning).Should().Be(false);
        double stoppedAt = (double)sim.Read(MachineTagKeys.AxisActualPositionMm("Z"))!;
        sim.Advance(TimeSpan.FromSeconds(1));
        ((double)sim.Read(MachineTagKeys.AxisActualPositionMm("Z"))!).Should().Be(stoppedAt);
    }

    [Fact]
    public async Task The_simulator_positions_the_measuring_carriage_and_reports_done()
    {
        SimulatedMachine sim = await SimAsync();
        sim.Write(MachineTagKeys.ManualPositionAxis, Value(MachineTagKeys.ManualPositionAxis, 2.0));
        sim.Write(MachineTagKeys.ManualPositionTarget, Value(MachineTagKeys.ManualPositionTarget, 180.0));
        sim.Write(MachineTagKeys.ManualPositionSpeed, Value(MachineTagKeys.ManualPositionSpeed, 3000.0));
        sim.Write(MachineTagKeys.ManualPositionStart, Value(MachineTagKeys.ManualPositionStart, true));

        sim.Read(MachineTagKeys.ManualPositionState).Should().Be(1);
        for (int i = 0; i < 20; i++)
        {
            sim.Advance(TimeSpan.FromMilliseconds(250));
        }

        sim.Read(MachineTagKeys.ManualPositionState).Should().Be(2);
        sim.Read(MachineTagKeys.AxisActualPositionMm("X1")).Should().Be(180.0);
    }

    [Fact]
    public async Task The_simulator_follows_the_mode_request_and_reports_a_safe_machine()
    {
        SimulatedMachine sim = await SimAsync();
        sim.Read(MachineTagKeys.OperatingMode).Should().Be(0);
        sim.Write(MachineTagKeys.ModeRequest, Value(MachineTagKeys.ModeRequest, 2.0));
        sim.Read(MachineTagKeys.OperatingMode).Should().Be(2);

        sim.Read(MachineTagKeys.EmergencyStop).Should().Be(false);
        sim.Read(MachineTagKeys.MachineOn).Should().Be(true);
        sim.Read(MachineTagKeys.Referenced).Should().Be(true);
        sim.Read(MachineTagKeys.OverrideFeedPercent).Should().Be(100);
    }

    [Fact]
    public async Task The_simulated_wheel_turns_at_the_surface_speed_setpoint()
    {
        SimulatedMachine sim = await SimAsync();
        sim.Read(MachineTagKeys.WheelSpeedRpm).Should().Be(0.0);

        sim.Write(MachineTagKeys.ManualWheelSurfaceSpeedSetpoint, Value(MachineTagKeys.ManualWheelSurfaceSpeedSetpoint, 35.0));
        sim.Write(MachineTagKeys.ManualCommand("wheel.run"), Value(MachineTagKeys.ManualCommand("wheel.run"), true));

        double rpm = (double)sim.Read(MachineTagKeys.WheelSpeedRpm)!;
        double diameter = (double)sim.Read(MachineTagKeys.WheelDiameterMm)!;
        (Math.PI * diameter * rpm / 60000.0).Should().BeApproximately(35.0, 1e-9);
    }
}
