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
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Monitoring;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 磨削当中的工序跳转与提前结束。
///
/// 上位机只是按一下按钮：写目标号、脉冲命令位，剩下的由 NC 在安全点做。
/// 这里守的是"按下去到底发了什么、什么时候不该发"。
/// </summary>
public sealed class StepFlowControlTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    /// <summary>记录写了什么（含写入顺序）的假网关。</summary>
    private sealed class RecordingGateway : IMachineGateway
    {
        private readonly bool failWrites;

        public RecordingGateway(bool failWrites = false)
        {
            this.failWrites = failWrites;
        }

        public List<(string LogicalName, object? Value)> Writes { get; } = new();

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

            Writes.Add((logicalName, value.Raw));
            return Task.CompletedTask;
        }

        public Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken) =>
            Task.CompletedTask;

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

    private sealed class RecordingAlarms : IAlarmSink
    {
        public List<(AlarmSeverity Severity, string Key, int Code)> Entries { get; } = new();

        public void Raise(AlarmSeverity severity, string messageResourceKey, string? detail = null, int code = AlarmCodes.Unspecified) =>
            Entries.Add((severity, messageResourceKey, code));

        public void RaiseException(Exception exception) =>
            Entries.Add((AlarmSeverity.Error, exception.GetType().Name, AlarmCodes.UnexpectedFailure));
    }

    private static readonly string[] AllControlKeys =
    {
        MachineTagKeys.JobControlTargetStepOrder,
        MachineTagKeys.JobControlJumpToStep,
        MachineTagKeys.JobControlEndStepEarly,
        MachineTagKeys.JobControlCycleStart,
        MachineTagKeys.JobControlFeedHold,
    };

    private static HmiSettings Settings() => new(
        1, "zh-CN", 200, 8, 201, 600, 365, 200, 0.6, 5, UserRole.Operator, 1);

    private static MachineStateSnapshot SnapshotAtStep(
        int currentStepOrder, GatewayConnectionState connection = GatewayConnectionState.Connected) =>
        new(
            Now,
            connection,
            new[] { new TagValue(MachineTagKeys.JobCurrentStepOrder, TagDataType.Int32, currentStepOrder, Now) });

    /// <summary>三道工序的作业，够试"往前/往回/越界"。</summary>
    private static GrindingJob CreateJob() => GrindingJob.Create(
        "J-1",
        "R-1",
        RollGeometry.FromDiameter(2000.0, 650.0),
        ProfileTypeKeys.Cylindrical,
        new CylindricalProfileType().Schema.CreateDefaults(),
        new[]
        {
            new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
            new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
            new GrindingJobStep(3, StepTypeKeys.SparkOut, new SparkOutStepType().Schema.CreateDefaults()),
        });

    private static StepFlowControlService CreateService(
        RecordingGateway gateway,
        MachineStateSnapshot snapshot,
        RecordingAlarms alarms,
        IEnumerable<string>? mappedKeys = null) =>
        new(
            gateway,
            new StaticMonitor(snapshot),
            new KeyedTagMap(mappedKeys ?? AllControlKeys),
            alarms,
            Settings(),
            TimeProvider.System);

    [Fact]
    public async Task A_jump_writes_the_target_first_then_pulses_the_command_bit()
    {
        // 顺序很要紧：反过来的话 NC 可能读到上一次的目标号。
        var gateway = new RecordingGateway();
        var alarms = new RecordingAlarms();
        StepFlowControlService service = CreateService(gateway, SnapshotAtStep(1), alarms);

        StepFlowResult result = await service.JumpToStepAsync(CreateJob(), 3, "wang", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        gateway.Writes.Should().Equal(new (string, object?)[]
        {
            (MachineTagKeys.JobControlTargetStepOrder, 3),
            (MachineTagKeys.JobControlJumpToStep, true),
            (MachineTagKeys.JobControlJumpToStep, false),
        });
    }

    [Fact]
    public async Task An_early_end_is_a_pulse_that_clears_itself()
    {
        // 脉冲写 true 再写 false：PLC 按上升沿触发，上位机被杀也不会卡住一个按住的按钮。
        var gateway = new RecordingGateway();
        StepFlowControlService service = CreateService(gateway, SnapshotAtStep(2), new RecordingAlarms());

        StepFlowResult result = await service.EndStepEarlyAsync(CreateJob(), "wang", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        gateway.Writes.Should().Equal(new (string, object?)[]
        {
            (MachineTagKeys.JobControlEndStepEarly, true),
            (MachineTagKeys.JobControlEndStepEarly, false),
        });
    }

    [Fact]
    public async Task Jumping_backwards_is_refused()
    {
        // 往回跳意味着已磨完的工序要重来，余量对不上、记录也说不清这支辊是怎么磨的。
        var gateway = new RecordingGateway();
        StepFlowControlService service = CreateService(gateway, SnapshotAtStep(2), new RecordingAlarms());

        StepFlowResult result = await service.JumpToStepAsync(CreateJob(), 1, "wang", CancellationToken.None);

        result.Refusal.Should().Be(StepFlowRefusal.Backwards);
        gateway.Writes.Should().BeEmpty("被挡下来的命令一个字节都不该写出去");
    }

    [Fact]
    public async Task Jumping_to_the_running_step_is_refused_too()
    {
        // 按了没反应比按了被拒更让人犯嘀咕，所以这一下也明确挡掉。
        StepFlowControlService service = CreateService(
            new RecordingGateway(), SnapshotAtStep(2), new RecordingAlarms());

        StepFlowResult result = await service.JumpToStepAsync(CreateJob(), 2, "wang", CancellationToken.None);

        result.Refusal.Should().Be(StepFlowRefusal.Backwards);
    }

    [Fact]
    public async Task Jumping_past_the_last_step_is_refused()
    {
        StepFlowControlService service = CreateService(
            new RecordingGateway(), SnapshotAtStep(1), new RecordingAlarms());

        StepFlowResult result = await service.JumpToStepAsync(CreateJob(), 4, "wang", CancellationToken.None);

        result.Refusal.Should().Be(StepFlowRefusal.OutOfRange);
    }

    [Fact]
    public void Nothing_is_offered_while_the_machine_is_not_running_a_job()
    {
        // 没在跑就没有"当前工序"可言，这时候该改的是作业本身。
        StepFlowControlService service = CreateService(
            new RecordingGateway(), SnapshotAtStep(0), new RecordingAlarms());

        service.CanEndStepEarly(CreateJob()).Refusal.Should().Be(StepFlowRefusal.NotRunning);
        service.CanJumpTo(CreateJob(), 2).Refusal.Should().Be(StepFlowRefusal.NotRunning);
    }

    [Fact]
    public void A_disconnected_machine_refuses_before_anything_else()
    {
        StepFlowControlService service = CreateService(
            new RecordingGateway(),
            SnapshotAtStep(1, GatewayConnectionState.Disconnected),
            new RecordingAlarms());

        service.CanJumpTo(CreateJob(), 2).Refusal.Should().Be(StepFlowRefusal.Disconnected);
    }

    [Fact]
    public void An_unmapped_control_bit_disables_the_whole_feature()
    {
        // 缺一位就整组不给用：只跳转不能提前结束这种半吊子状态比压暗更难解释。
        var service = CreateService(
            new RecordingGateway(),
            SnapshotAtStep(1),
            new RecordingAlarms(),
            new[] { MachineTagKeys.JobControlTargetStepOrder, MachineTagKeys.JobControlJumpToStep });

        service.IsMapped.Should().BeFalse();
        service.CanJumpTo(CreateJob(), 2).Refusal.Should().Be(StepFlowRefusal.NotMapped);
    }

    [Fact]
    public async Task A_failed_write_is_reported_not_swallowed()
    {
        var alarms = new RecordingAlarms();
        StepFlowControlService service = CreateService(new RecordingGateway(failWrites: true), SnapshotAtStep(1), alarms);

        StepFlowResult result = await service.JumpToStepAsync(CreateJob(), 2, "wang", CancellationToken.None);

        result.Refusal.Should().Be(StepFlowRefusal.WriteFailed);
        alarms.Entries.Should().ContainSingle(entry => entry.Severity == AlarmSeverity.Error);
    }

    [Fact]
    public async Task Who_pressed_it_and_where_it_went_is_recorded()
    {
        // 少磨一道是追溯时要说清楚的事，所以两个动作都留一条提示级报警。
        var alarms = new RecordingAlarms();
        StepFlowControlService service = CreateService(new RecordingGateway(), SnapshotAtStep(1), alarms);

        await service.JumpToStepAsync(CreateJob(), 2, "wang", CancellationToken.None);
        await service.EndStepEarlyAsync(CreateJob(), "wang", CancellationToken.None);

        alarms.Entries.Select(entry => entry.Code).Should()
            .Equal(AlarmCodes.StepJumped, AlarmCodes.StepEndedEarly);
        alarms.Entries.Should().OnlyContain(entry => entry.Severity == AlarmSeverity.Information);
    }

    [Fact]
    public async Task Cycle_start_is_a_request_not_a_command()
    {
        // 上位机不在任何一条使能链里：这一下只是把"操作工想开始了"告诉 PLC，
        // 能不能动由它的互锁说了算。所以这里只守"确实脉冲了一下"。
        var gateway = new RecordingGateway();
        StepFlowControlService service = CreateService(
            gateway,
            SnapshotAtStep(0),
            new RecordingAlarms(),
            new[] { MachineTagKeys.JobControlCycleStart, MachineTagKeys.JobControlFeedHold });

        StepFlowResult result = await service.RequestCycleStartAsync("wang", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        gateway.Writes.Should().Equal(new (string, object?)[]
        {
            (MachineTagKeys.JobControlCycleStart, true),
            (MachineTagKeys.JobControlCycleStart, false),
        });
    }

    [Fact]
    public async Task Feed_hold_works_even_when_no_job_is_running()
    {
        // 想保持的时候更不该被"读不到工序号"挡住。
        var gateway = new RecordingGateway();
        StepFlowControlService service = CreateService(
            gateway,
            SnapshotAtStep(0),
            new RecordingAlarms(),
            new[] { MachineTagKeys.JobControlCycleStart, MachineTagKeys.JobControlFeedHold });

        StepFlowResult result = await service.RequestFeedHoldAsync("wang", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        gateway.Writes.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unmapped_cycle_bit_refuses_instead_of_pretending()
    {
        var gateway = new RecordingGateway();
        StepFlowControlService service = CreateService(
            gateway, SnapshotAtStep(1), new RecordingAlarms(), Array.Empty<string>());

        service.CanRequestCycleControl.Should().BeFalse();
        (await service.RequestCycleStartAsync("wang", CancellationToken.None))
            .Refusal.Should().Be(StepFlowRefusal.NotMapped);
        gateway.Writes.Should().BeEmpty();
    }

    [Fact]
    public void The_control_bits_are_all_in_the_sample_tag_map()
    {
        // 样例 tagmap 缺一项，现场拿去改的那一份也会跟着缺。
        string path = Path.Combine(RepositoryLayout.Root, "config", "tagmap.sample.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        HashSet<string> keys = document.RootElement.GetProperty("tags")
            .EnumerateArray()
            .Select(tag => tag.GetProperty("key").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        keys.Should().Contain(AllControlKeys);
    }
}
