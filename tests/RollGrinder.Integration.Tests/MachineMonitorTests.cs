using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;
using Xunit;

namespace RollGrinder.Integration.Tests;

public sealed class MachineMonitorTests
{
    private static readonly MachineDescription Machine = new(
        1,
        "RG-T",
        "test",
        new ControllerDescription("SinumerikOne", 1),
        new[] { new AxisDescription("X", MachineAxisRoles.InfeedRadius, true, AxisClosedLoopKind.FullClosed) },
        Array.Empty<MeasurementChannelDescription>(),
        new Dictionary<string, bool>(),
        new Dictionary<string, double>(),
        new WorkpieceLimits(100.0, 5000.0, 100.0, 1000.0, 1000.0),
            new Dictionary<string, int>());

    private static readonly HmiSettings Settings = new(1, "zh-CN", 100, 8, 101, 300, 365, 500, 0.7, 5, 5.0, UserRole.Operator);

    private sealed class ScriptedGateway : IMachineGateway
    {
        private readonly Queue<Func<MachineStateSnapshot>> responses = new();

        public GatewayConnectionState ConnectionState { get; private set; } = GatewayConnectionState.Disconnected;

        public int ConnectCount { get; private set; }

        public void EnqueueSnapshot(double channelState) =>
            this.responses.Enqueue(() => new MachineStateSnapshot(
                DateTimeOffset.UnixEpoch,
                GatewayConnectionState.Connected,
                new[] { new TagValue(MachineTagKeys.ChannelState, TagDataType.Double, channelState, DateTimeOffset.UnixEpoch) }));

        public void EnqueueFailure(string message) =>
            this.responses.Enqueue(() => throw new GatewayException(message));

        private string? connectFailure;
        private TimeSpan connectDelay;

        public void FailNextConnect(string message) => this.connectFailure = message;

        public void BlockNextConnect(TimeSpan delay) => this.connectDelay = delay;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCount++;
            if (this.connectDelay > TimeSpan.Zero)
            {
                TimeSpan delay = this.connectDelay;
                this.connectDelay = TimeSpan.Zero;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (this.connectFailure is not null)
            {
                string message = this.connectFailure;
                this.connectFailure = null;
                ConnectionState = GatewayConnectionState.Faulted;
                throw new GatewayException(message);
            }

            ConnectionState = GatewayConnectionState.Connected;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            ConnectionState = GatewayConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task<MachineStateSnapshot> ReadStateAsync(IReadOnlyList<string> logicalNames, CancellationToken cancellationToken) =>
            Task.FromResult(this.responses.Count > 0
                ? this.responses.Dequeue()()
                : MachineStateSnapshot.Empty(DateTimeOffset.UnixEpoch));

        public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (MachineMonitor Monitor, ScriptedGateway Gateway, AlarmLog Alarms) Create()
    {
        var gateway = new ScriptedGateway();
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var alarms = new AlarmLog(Settings.AlarmHistoryLimit, time);
        return (new MachineMonitor(gateway, Machine, Settings, alarms, time), gateway, alarms);
    }

    [Fact]
    public void Monitor_starts_from_an_empty_disconnected_snapshot()
    {
        (MachineMonitor monitor, _, _) = Create();

        monitor.Current.ConnectionState.Should().Be(GatewayConnectionState.Disconnected);
        monitor.Current.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Poll_publishes_an_immutable_snapshot()
    {
        (MachineMonitor monitor, ScriptedGateway gateway, _) = Create();
        gateway.EnqueueSnapshot(2.0);

        MachineStateSnapshot? published = null;
        monitor.SnapshotUpdated += (_, snapshot) => published = snapshot;

        await monitor.PollOnceAsync(CancellationToken.None);

        monitor.Current.GetNumberOrNull(MachineTagKeys.ChannelState).Should().Be(2.0);
        published.Should().BeSameAs(monitor.Current);
    }

    [Fact]
    public async Task A_failing_poll_becomes_an_alarm_instead_of_throwing()
    {
        (MachineMonitor monitor, ScriptedGateway gateway, AlarmLog alarms) = Create();
        gateway.EnqueueFailure("connection reset");

        await monitor.Invoking(m => m.PollOnceAsync(CancellationToken.None)).Should().NotThrowAsync();

        monitor.Current.ConnectionState.Should().Be(GatewayConnectionState.Faulted);
        alarms.Snapshot().Should().ContainSingle(entry =>
            entry.MessageResourceKey == MachineMonitor.ConnectionLostResourceKey
            && entry.Severity == AlarmSeverity.Error
            && entry.Detail == "connection reset");
    }

    [Fact]
    public async Task Repeated_failures_raise_a_single_alarm()
    {
        (MachineMonitor monitor, ScriptedGateway gateway, AlarmLog alarms) = Create();
        gateway.EnqueueFailure("down");
        gateway.EnqueueFailure("still down");

        await monitor.PollOnceAsync(CancellationToken.None);
        await monitor.PollOnceAsync(CancellationToken.None);

        alarms.Snapshot().Should().HaveCount(1, "报警条目不应被同一次断链刷屏");
    }

    [Fact]
    public async Task Recovery_is_reported_once_the_gateway_answers_again()
    {
        (MachineMonitor monitor, ScriptedGateway gateway, AlarmLog alarms) = Create();
        gateway.EnqueueFailure("down");
        gateway.EnqueueSnapshot(0.0);

        await monitor.PollOnceAsync(CancellationToken.None);
        await monitor.PollOnceAsync(CancellationToken.None);

        alarms.Snapshot().Should().Contain(entry =>
            entry.MessageResourceKey == MachineMonitor.ConnectionRestoredResourceKey);
    }

    [Fact]
    public async Task A_failed_poll_is_followed_by_a_reconnect_attempt()
    {
        (MachineMonitor monitor, ScriptedGateway gateway, _) = Create();
        gateway.EnqueueFailure("session closed");
        gateway.EnqueueSnapshot(2.0);

        await monitor.PollOnceAsync(CancellationToken.None);
        int connectsAfterFailure = gateway.ConnectCount;
        await monitor.PollOnceAsync(CancellationToken.None);

        gateway.ConnectCount.Should().Be(connectsAfterFailure + 1, "断链后必须自己重连，不能等人重启上位机");
        monitor.Current.GetNumberOrNull(MachineTagKeys.ChannelState).Should().Be(2.0);
    }

    [Fact]
    public async Task A_healthy_poll_does_not_reconnect_every_tick()
    {
        (MachineMonitor monitor, ScriptedGateway gateway, _) = Create();
        gateway.EnqueueSnapshot(2.0);
        gateway.EnqueueSnapshot(2.0);

        await monitor.PollOnceAsync(CancellationToken.None);
        await monitor.PollOnceAsync(CancellationToken.None);

        gateway.ConnectCount.Should().Be(1, "首拍连一次，之后没断就不该反复重连");
    }

    [Fact]
    public async Task A_reconnect_that_also_fails_stays_an_alarm_rather_than_an_exception()
    {
        (MachineMonitor monitor, ScriptedGateway gateway, AlarmLog alarms) = Create();
        gateway.EnqueueFailure("down");
        gateway.FailNextConnect("still down");
        gateway.EnqueueFailure("down again");

        await monitor.PollOnceAsync(CancellationToken.None);
        await monitor.Invoking(m => m.PollOnceAsync(CancellationToken.None)).Should().NotThrowAsync();

        monitor.Current.ConnectionState.Should().Be(GatewayConnectionState.Faulted);
        alarms.Snapshot().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Start_does_not_wait_for_the_machine_to_answer()
    {
        // 机床不可达时一次握手可能十几秒：界面不能等它。
        (MachineMonitor monitor, ScriptedGateway gateway, _) = Create();
        gateway.BlockNextConnect(TimeSpan.FromSeconds(30));

        Task start = monitor.StartAsync(CancellationToken.None);

        (await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(2)))).Should().BeSameAs(start,
            "启动不得等在连接上");
        await monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_first_poll_connects_and_stop_disconnects()
    {
        (MachineMonitor monitor, ScriptedGateway gateway, _) = Create();
        gateway.EnqueueSnapshot(2.0);

        await monitor.StartAsync(CancellationToken.None);
        await monitor.PollOnceAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);

        gateway.ConnectCount.Should().Be(1);
        gateway.ConnectionState.Should().Be(GatewayConnectionState.Disconnected);
    }
}

public sealed class AlarmLogTests
{
    [Fact]
    public void Entries_are_newest_first_and_capped()
    {
        var log = new AlarmLog(limit: 3, new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        for (int i = 0; i < 5; i++)
        {
            log.Raise(AlarmSeverity.Warning, "Alarm_Test", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        IReadOnlyList<AlarmEntry> entries = log.Snapshot();
        entries.Should().HaveCount(3);
        entries[0].Detail.Should().Be("4");
        entries[^1].Detail.Should().Be("2");
    }

    [Fact]
    public void Exceptions_map_to_their_layer_specific_resource_key()
    {
        var log = new AlarmLog(limit: 10, new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        log.RaiseException(new GatewayException("gateway"));
        log.RaiseException(new RollGrinder.Core.DomainException("domain"));
        log.RaiseException(new InvalidOperationException("other"));

        log.Snapshot().Should().SatisfyRespectively(
            newest => newest.MessageResourceKey.Should().Be(AlarmLog.UnexpectedFailureResourceKey),
            middle => middle.MessageResourceKey.Should().Be(AlarmLog.DomainFailureResourceKey),
            oldest => oldest.MessageResourceKey.Should().Be(AlarmLog.GatewayFailureResourceKey));
    }

    [Fact]
    public void Changed_is_raised_for_every_entry()
    {
        var log = new AlarmLog(limit: 10, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        int changes = 0;
        log.Changed += (_, _) => changes++;

        log.Raise(AlarmSeverity.Information, "Alarm_Test");
        log.Clear();

        changes.Should().Be(2);
    }
}
