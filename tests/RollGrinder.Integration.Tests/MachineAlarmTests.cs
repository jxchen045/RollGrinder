using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 把机床自己的报警搬进上位机的报警表。
///
/// 只搬不判：号、文本、严重与否全按机床给的来。守的是"该报的报了、
/// 重复的不刷屏、断线不装作故障好了"。
/// </summary>
public sealed class MachineAlarmTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

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

    private sealed class RecordingAlarms : IAlarmSink
    {
        public List<(AlarmSeverity Severity, string Key, string? Detail, int Code)> Entries { get; } = new();

        public void Raise(AlarmSeverity severity, string messageResourceKey, string? detail = null, int code = AlarmCodes.Unspecified) =>
            Entries.Add((severity, messageResourceKey, detail, code));

        public void RaiseException(Exception exception) => throw new InvalidOperationException("unexpected");
    }

    private static MachineStateSnapshot Alarms(
        GatewayConnectionState connection, params (int Number, string? Text)[] alarms)
    {
        var values = new List<TagValue>
        {
            new(MachineTagKeys.MachineAlarmCount, TagDataType.Int32, alarms.Length, Now),
        };

        for (int i = 0; i < alarms.Length; i++)
        {
            values.Add(new TagValue(
                MachineTagKeys.MachineAlarmNumberAt(i), TagDataType.Int32, alarms[i].Number, Now));
            if (alarms[i].Text is string text)
            {
                values.Add(new TagValue(
                    MachineTagKeys.MachineAlarmTextAt(i), TagDataType.String, text, Now));
            }
        }

        return new MachineStateSnapshot(Now, connection, values);
    }

    private static MachineStateSnapshot Alarms(params (int Number, string? Text)[] alarms) =>
        Alarms(GatewayConnectionState.Connected, alarms);

    private static (PushMonitor Monitor, RecordingAlarms Log) Start()
    {
        var monitor = new PushMonitor();
        var log = new RecordingAlarms();
        new MachineAlarmWatcher(monitor, log).StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (monitor, log);
    }

    [Fact]
    public void A_machine_alarm_shows_up_with_the_machines_own_number()
    {
        // 上位机的号段当初特意挪出机床的两段，为的就是并排显示时一眼分得清是谁的。
        (PushMonitor monitor, RecordingAlarms log) = Start();

        monitor.Push(Alarms((700045, "冷却液液位低")));

        log.Entries.Should().ContainSingle();
        log.Entries[0].Code.Should().Be(700045, "机床报警沿用机床给的号");
        log.Entries[0].Key.Should().Be(MachineAlarmWatcher.MachineAlarmResourceKey);
        log.Entries[0].Detail.Should().Be("冷却液液位低");
    }

    [Fact]
    public void The_severity_comes_from_the_number_range_not_from_us()
    {
        // 说明书把 700040 划为严重与一般的分界，这不是上位机的判断。
        MachineAlarmWatcher.SeverityOf(700040).Should().Be(AlarmSeverity.Warning);
        MachineAlarmWatcher.SeverityOf(700039).Should().Be(AlarmSeverity.Error);
        MachineAlarmWatcher.SeverityOf(510001).Should().Be(AlarmSeverity.Error, "PLC 严重故障");
        MachineAlarmWatcher.SeverityOf(20001).Should().Be(AlarmSeverity.Error, "NC 报警");
    }

    [Fact]
    public void The_same_alarm_is_reported_once_not_every_poll()
    {
        // 挂着的报警每一拍都读得到；每拍报一条会把报警表冲成一片同样的字。
        (PushMonitor monitor, RecordingAlarms log) = Start();

        monitor.Push(Alarms((700045, "冷却液液位低")));
        monitor.Push(Alarms((700045, "冷却液液位低")));
        monitor.Push(Alarms((700045, "冷却液液位低")));

        log.Entries.Should().ContainSingle();
    }

    [Fact]
    public void A_second_alarm_joining_the_first_is_reported_too()
    {
        (PushMonitor monitor, RecordingAlarms log) = Start();

        monitor.Push(Alarms((700045, "甲")));
        monitor.Push(Alarms((700045, "甲"), (510001, "乙")));

        log.Entries.Select(entry => entry.Code).Should().Equal(700045, 510001);
    }

    [Fact]
    public void A_cleared_alarm_leaves_a_note()
    {
        // 现场要能看出"刚才那条什么时候被复位的"。
        (PushMonitor monitor, RecordingAlarms log) = Start();

        monitor.Push(Alarms((700045, "冷却液液位低")));
        monitor.Push(Alarms());

        log.Entries.Should().HaveCount(2);
        log.Entries[1].Key.Should().Be(MachineAlarmWatcher.MachineAlarmClearedResourceKey);
        log.Entries[1].Code.Should().Be(700045);
        log.Entries[1].Severity.Should().Be(AlarmSeverity.Information);
    }

    [Fact]
    public void An_alarm_that_comes_back_is_reported_again()
    {
        (PushMonitor monitor, RecordingAlarms log) = Start();

        monitor.Push(Alarms((700045, "甲")));
        monitor.Push(Alarms());
        monitor.Push(Alarms((700045, "甲")));

        log.Entries.Select(entry => entry.Key).Should().Equal(
            MachineAlarmWatcher.MachineAlarmResourceKey,
            MachineAlarmWatcher.MachineAlarmClearedResourceKey,
            MachineAlarmWatcher.MachineAlarmResourceKey);
    }

    [Fact]
    public void Losing_the_connection_does_not_pretend_the_faults_went_away()
    {
        // 断线本身已经有一条报警；把机床报警一并抹掉会让人以为故障自己好了。
        (PushMonitor monitor, RecordingAlarms log) = Start();

        monitor.Push(Alarms((700045, "甲")));
        monitor.Push(Alarms(GatewayConnectionState.Disconnected));

        log.Entries.Should().ContainSingle("断线那一拍不该报出'已复位'");
    }

    [Fact]
    public void An_unmapped_alarm_channel_simply_shows_nothing()
    {
        // tagmap 没映射机床报警：报警表里就只有上位机自己的，不装作读过。
        (PushMonitor monitor, RecordingAlarms log) = Start();

        monitor.Push(new MachineStateSnapshot(
            Now, GatewayConnectionState.Connected, Array.Empty<TagValue>()));

        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public void More_alarms_than_we_track_are_cut_off_at_the_slot_count()
    {
        // 报警一来常常是一串。够看清"最先炸的是哪一条"就行，
        // 再多该去看 Operate 的报警画面——那才是机床报警的正主。
        (PushMonitor monitor, RecordingAlarms log) = Start();

        (int, string?)[] many = Enumerable.Range(1, MachineTagKeys.MachineAlarmSlots + 4)
            .Select(i => (700000 + i, (string?)null))
            .ToArray();

        monitor.Push(Alarms(many));

        log.Entries.Should().HaveCount(MachineTagKeys.MachineAlarmSlots);
    }

    [Fact]
    public void The_machine_alarm_channel_is_in_the_sample_tag_map_and_the_monitoring_list()
    {
        MachineDescription machine = new(
            1, "M-1", "test", new ControllerDescription("SinumerikOne", 1),
            Array.Empty<AxisDescription>(), Array.Empty<MeasurementChannelDescription>(),
            new Dictionary<string, bool>(), new Dictionary<string, double>(),
            new WorkpieceLimits(100.0, 3000.0, 100.0, 9000.0, 200000.0),
            new Dictionary<string, int>());

        IReadOnlyList<string> monitored = MachineTagKeys.MonitoringKeys(machine);

        monitored.Should().Contain(MachineTagKeys.MachineAlarmCount);
        monitored.Should().Contain(MachineTagKeys.MachineAlarmNumberAt(0));
        monitored.Should().Contain(MachineTagKeys.MachineAlarmTextAt(MachineTagKeys.MachineAlarmSlots - 1));
    }
}
