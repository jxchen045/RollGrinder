using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Manual;
using RollGrinder.Services.Monitoring;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 手动页按钮矩阵：27 个动作的命令形式、门禁与写入顺序。
/// 这些按钮直接动机床上的大件，所以规则要有测试守着。
/// </summary>
public sealed class ManualCommandTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    /// <summary>记录写了什么的假网关。手动动作的正确性就在这串写入里。</summary>
    private sealed class RecordingGateway : IMachineGateway
    {
        private readonly bool failWrites;

        public RecordingGateway(bool failWrites = false)
        {
            this.failWrites = failWrites;
        }

        public List<(string LogicalName, bool Value)> Writes { get; } = new();

        public GatewayConnectionState ConnectionState => GatewayConnectionState.Connected;

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<MachineStateSnapshot> ReadStateAsync(
            IReadOnlyList<string> logicalNames, CancellationToken cancellationToken) =>
            Task.FromResult(MachineStateSnapshot.Empty(Now));

        public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken) =>
            Task.FromResult(new TagValue(logicalName, TagDataType.Boolean, false, Now));

        public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken)
        {
            if (this.failWrites)
            {
                throw new GatewayException("write refused by the test");
            }

            Writes.Add((logicalName, value.Raw is bool flag && flag));
            return Task.CompletedTask;
        }

        public Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>只提供一份固定快照的监视器。</summary>
    private sealed class StaticMonitor : IMachineMonitor
    {
        public StaticMonitor(MachineStateSnapshot snapshot)
        {
            Current = snapshot;
        }

        public MachineStateSnapshot Current { get; }

        public event EventHandler<MachineStateSnapshot>? SnapshotUpdated;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            SnapshotUpdated?.Invoke(this, Current);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>按给定逻辑名建一份最小 tagmap。</summary>
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
            new(key, "sim://" + key, TagDataType.Boolean, TagAccess.ReadWrite);
    }

    private static MachineStateSnapshot SnapshotWith(NcChannelState channelState, params (string Key, bool Value)[] flags)
    {
        var values = new List<TagValue>
        {
            new(MachineTagKeys.ChannelState, TagDataType.Int32, (int)channelState, Now),
        };

        foreach ((string key, bool value) in flags)
        {
            values.Add(new TagValue(key, TagDataType.Boolean, value, Now));
        }

        return new MachineStateSnapshot(Now, GatewayConnectionState.Connected, values);
    }

    private static HmiSettings Settings(int pulseMs = 1) => new(
        1, "zh-CN", 200, 8, 201, 600, 365, 200, 0.6, 5, UserRole.Operator, pulseMs);

    private static ManualCommandService CreateService(
        RecordingGateway gateway,
        MachineStateSnapshot snapshot,
        IEnumerable<string>? mappedKeys = null,
        int pulseMs = 1) =>
        new(
            gateway,
            new StaticMonitor(snapshot),
            new KeyedTagMap(mappedKeys ?? ManualCommandCatalog.All
                .Where(command => command.Kind != ManualCommandKind.Local)
                .Select(command => MachineTagKeys.ManualCommand(command.Key))),
            Settings(pulseMs),
            TimeProvider.System);

    private static ManualCommandDescriptor Find(string key) =>
        ManualCommandCatalog.All.Single(command => string.Equals(command.Key, key, StringComparison.Ordinal));

    [Fact]
    public void The_catalogue_matches_the_machines_io()
    {
        // 按 MK84160 电气原理图核对后的三组**按钮**：8 + 4 + 15 = 27。
        // 尾架那一组从 6 减到 4——图纸上只有前进/后退，没有夹紧/放松；
        // 其他那一组从 12 加到 15——头架拆成正转/反转，软着陆拆成两侧各一对升降。
        ManualCommandCatalog.MeasuringArm.Should().HaveCount(8);
        ManualCommandCatalog.Tailstock.Should().HaveCount(4);
        ManualCommandCatalog.Other.Should().HaveCount(15);
        // 外加五个辅助循环。它们不进按钮矩阵——是"跑一段程序"而不是"动一下某个
        // 机构"，所以挂在功能键上，但走的是同一套脉冲与门禁，也归 All 管。
        ManualCommandCatalog.Cycles.Should().HaveCount(5);
        ManualCommandCatalog.All.Should().HaveCount(32);

        ManualCommandCatalog.All.Select(command => command.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_catalogue_has_no_action_the_machine_cannot_do()
    {
        // 这四个键对应的 DO 在原理图上根本不存在（尾架夹紧/放松），
        // 或者把两个不同的动作混成了一个（软着陆分头架侧与尾架侧、头架分正反转）。
        // 留一条测试把它们钉死，免得哪天照着旧设计稿又加回来。
        string[] keys = ManualCommandCatalog.All.Select(command => command.Key).ToArray();

        keys.Should().NotContain("tailstock.clamp", "图纸上没有尾架夹紧这个 DO");
        keys.Should().NotContain("tailstock.release", "图纸上没有尾架放松这个 DO");
        keys.Should().NotContain("headstock.run", "头架是正转/反转，不是启动/停止");
        keys.Should().NotContain("softLanding.up", "软着陆分头架侧与尾架侧，不是一个开关");
        keys.Should().NotContain("softLanding.down", "软着陆分头架侧与尾架侧，不是一个开关");
    }

    [Fact]
    public void The_measuring_arms_are_named_by_the_side_they_are_on()
    {
        // 原理图里标的是"外测量臂 / 内测量臂"，测头 A 在外臂、B 在内臂。
        // 按钮上只写 A/B，现场得猜是哪一侧。
        string[] keys = ManualCommandCatalog.MeasuringArm.Select(command => command.Key).ToArray();

        keys.Should().Contain(new[] { "outerArm.lower", "outerArm.raise", "innerArm.lower", "innerArm.raise" });
        keys.Should().NotContain(key => key.StartsWith("probeA.", StringComparison.Ordinal));
        keys.Should().NotContain(key => key.StartsWith("probeB.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_two_headstock_directions_declare_each_other_as_exclusive()
    {
        ManualCommandDescriptor forward = Find("headstock.forward");
        ManualCommandDescriptor reverse = Find("headstock.reverse");

        forward.Kind.Should().Be(ManualCommandKind.Toggle);
        reverse.Kind.Should().Be(ManualCommandKind.Toggle);
        forward.MutuallyExclusiveWith.Should().Be(reverse.Key);
        reverse.MutuallyExclusiveWith.Should().Be(forward.Key);
    }

    [Fact]
    public async Task Starting_one_headstock_direction_clears_the_other_first()
    {
        // 先清对方再置本方：中途被打断只会落到"两位都 false"，也就是停机。
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(gateway, SnapshotWith(NcChannelState.Reset));

        ManualCommandResult result = await service.ExecuteAsync(
            Find("headstock.forward"), desiredState: true, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        gateway.Writes.Should().Equal(
            (MachineTagKeys.ManualCommand("headstock.reverse"), false),
            (MachineTagKeys.ManualCommand("headstock.forward"), true));
    }

    [Fact]
    public async Task Stopping_a_headstock_direction_does_not_touch_the_other()
    {
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(gateway, SnapshotWith(NcChannelState.Reset));

        await service.ExecuteAsync(Find("headstock.forward"), desiredState: false, CancellationToken.None);

        gateway.Writes.Should().Equal((MachineTagKeys.ManualCommand("headstock.forward"), false));
    }

    [Fact]
    public void No_action_is_a_hold_to_run_jog()
    {
        // 点动属于实时控制回路，要有硬件使能托底，归机床面板与 PLC。
        // 上位机被强制结束时按住的键就松不开了——最高原则不允许这种按钮存在。
        ManualCommandCatalog.All.Should().OnlyContain(command =>
            command.Kind == ManualCommandKind.Pulse
            || command.Kind == ManualCommandKind.Toggle
            || command.Kind == ManualCommandKind.Local);
    }

    [Fact]
    public void Only_coolant_and_sampling_are_allowed_while_a_program_is_loaded()
    {
        string[] allowed = ManualCommandCatalog.All
            .Where(command => !command.RequiresIdleChannel)
            .Select(command => command.Key)
            .ToArray();

        allowed.Should().BeEquivalentTo(new[] { "coolant", "measurement.sample" });
    }

    [Fact]
    public void Dangerous_actions_ask_twice()
    {
        string[] confirmed = ManualCommandCatalog.All
            .Where(command => command.RequiresConfirmation)
            .Select(command => command.Key)
            .ToArray();

        confirmed.Should().BeEquivalentTo(new[]
        {
            // 会让辊子失去支承、或者会把辊子落到托瓦上的动作。
            "quill.retract", "tailstock.backward", "driver.retract",
            "softLanding.headstock.down", "softLanding.tailstock.down", "axes.home",

            // 会切削或让各轴走全行程的循环。辊对中只是测量，不必按两下。
            "cycle.manualGrinding", "cycle.calibrateDatum", "cycle.wheelDress", "cycle.referencePoint",
        });
    }

    [Fact]
    public async Task A_pulse_action_writes_true_then_false()
    {
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(gateway, SnapshotWith(NcChannelState.Reset));

        ManualCommandResult result = await service.ExecuteAsync(
            Find("quill.extend"), desiredState: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        gateway.Writes.Should().Equal(
            (MachineTagKeys.ManualCommand("quill.extend"), true),
            (MachineTagKeys.ManualCommand("quill.extend"), false));
    }

    [Fact]
    public async Task A_toggle_action_flips_whatever_the_machine_reports()
    {
        string stateKey = MachineTagKeys.ManualCommandState("coolant");
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(
            gateway, SnapshotWith(NcChannelState.Reset, (stateKey, true)));

        service.ReadState(Find("coolant")).Should().BeTrue();

        await service.ExecuteAsync(Find("coolant"), desiredState: null, CancellationToken.None);

        gateway.Writes.Should().ContainSingle()
            .Which.Should().Be((MachineTagKeys.ManualCommand("coolant"), false), "开着的时候按一下应当关掉");
    }

    [Fact]
    public async Task A_toggle_action_honours_an_explicit_state()
    {
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(gateway, SnapshotWith(NcChannelState.Reset));

        await service.ExecuteAsync(Find("wheel.run"), desiredState: true, CancellationToken.None);

        gateway.Writes.Should().ContainSingle()
            .Which.Should().Be((MachineTagKeys.ManualCommand("wheel.run"), true));
    }

    [Fact]
    public async Task An_unmapped_action_is_refused_and_writes_nothing()
    {
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(
            gateway, SnapshotWith(NcChannelState.Reset), mappedKeys: Array.Empty<string>());

        ManualCommandDescriptor command = Find("quill.extend");
        service.IsMapped(command).Should().BeFalse();

        ManualCommandResult result = await service.ExecuteAsync(command, null, CancellationToken.None);

        result.Outcome.Should().Be(ManualCommandOutcome.NotMapped);
        result.ReasonResourceKey.Should().Be(ManualCommandService.NotMappedResourceKey);
        gateway.Writes.Should().BeEmpty("没映射就一个字节也不该写");
    }

    [Theory]
    [InlineData(NcChannelState.Running)]
    [InlineData(NcChannelState.Interrupted)]
    public async Task Actions_that_need_an_idle_channel_are_refused_while_a_program_is_loaded(NcChannelState state)
    {
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(gateway, SnapshotWith(state));

        ManualCommandResult result = await service.ExecuteAsync(
            Find("tailstock.backward"), null, CancellationToken.None);

        result.Outcome.Should().Be(ManualCommandOutcome.ChannelBusy);
        gateway.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Coolant_still_works_while_grinding()
    {
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(gateway, SnapshotWith(NcChannelState.Running));

        ManualCommandResult result = await service.ExecuteAsync(Find("coolant"), true, CancellationToken.None);

        result.Succeeded.Should().BeTrue("磨削当中开关冷却水是正当操作");
        gateway.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unknown_channel_state_counts_as_busy()
    {
        // 读不到通道状态时不知道机床在干什么，那就别乱动它。
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(
            gateway, new MachineStateSnapshot(Now, GatewayConnectionState.Connected, Array.Empty<TagValue>()));

        ManualCommandResult result = await service.ExecuteAsync(Find("axes.home"), null, CancellationToken.None);

        result.Outcome.Should().Be(ManualCommandOutcome.ChannelBusy);
        gateway.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disconnected_machine_refuses_every_action()
    {
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(
            gateway,
            new MachineStateSnapshot(Now, GatewayConnectionState.Disconnected, Array.Empty<TagValue>()));

        ManualCommandResult result = await service.ExecuteAsync(Find("coolant"), true, CancellationToken.None);

        result.Outcome.Should().Be(ManualCommandOutcome.Disconnected);
        gateway.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_write_is_reported_rather_than_swallowed()
    {
        var gateway = new RecordingGateway(failWrites: true);
        ManualCommandService service = CreateService(gateway, SnapshotWith(NcChannelState.Reset));

        ManualCommandResult result = await service.ExecuteAsync(Find("quill.extend"), null, CancellationToken.None);

        result.Outcome.Should().Be(ManualCommandOutcome.WriteFailed);
        result.ReasonResourceKey.Should().Be(ManualCommandService.WriteFailedResourceKey);
    }

    [Fact]
    public async Task The_local_action_never_touches_the_machine()
    {
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(gateway, SnapshotWith(NcChannelState.Running));

        ManualCommandDescriptor sample = Find("measurement.sample");
        sample.Kind.Should().Be(ManualCommandKind.Local);
        service.IsMapped(sample).Should().BeTrue("本地动作不需要 tagmap 里的命令位");

        ManualCommandResult result = await service.ExecuteAsync(sample, null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        gateway.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Read_state_is_null_for_pulse_actions()
    {
        var gateway = new RecordingGateway();
        ManualCommandService service = CreateService(gateway, SnapshotWith(NcChannelState.Reset));

        service.ReadState(Find("quill.extend")).Should().BeNull();
        service.ReadState(Find("coolant")).Should().BeNull("没有回读值时界面显示两道杠，不假装是关着的");
    }

    [Fact]
    public void The_monitored_toggle_state_keys_match_the_catalogue()
    {
        // Contracts 不引用 Services，所以那三个回读键是手抄的——这里盯着两边别走散。
        MachineTagKeys.ManualToggleStateKeys.Should().BeEquivalentTo(
            ManualCommandCatalog.Toggles.Select(command => MachineTagKeys.ManualCommandState(command.Key)));
    }

    [Fact]
    public void Every_action_is_mapped_in_the_sample_tag_map()
    {
        string path = Path.Combine(RepositoryLayout.Root, "config", "tagmap.sample.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        HashSet<string> keys = document.RootElement.GetProperty("tags")
            .EnumerateArray()
            .Select(tag => tag.GetProperty("key").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (ManualCommandDescriptor command in ManualCommandCatalog.All)
        {
            if (command.Kind == ManualCommandKind.Local)
            {
                continue;
            }

            keys.Should().Contain(
                MachineTagKeys.ManualCommand(command.Key), $"动作 {command.Key} 需要一个命令位");
        }

        foreach (string stateKey in MachineTagKeys.ManualToggleStateKeys)
        {
            keys.Should().Contain(stateKey, "保持型动作需要状态回读");
        }
    }
}
