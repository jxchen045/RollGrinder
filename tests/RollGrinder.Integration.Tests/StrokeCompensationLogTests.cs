using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Measurement;
using RollGrinder.Services.Monitoring;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>补偿子视图的收敛过程：NC 每修正一次（行程版本加一）记一行，新的一支辊（版本回落）重新记。</summary>
public sealed class StrokeCompensationLogTests
{
    private sealed class IdleMonitor : IMachineMonitor
    {
        public MachineStateSnapshot Current => MachineStateSnapshot.Empty(DateTimeOffset.UnixEpoch);

        public event EventHandler<MachineStateSnapshot>? SnapshotUpdated
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static MachineStateSnapshot Snapshot(int version, double offsetMm) => new(
        DateTimeOffset.UnixEpoch.AddSeconds(version),
        GatewayConnectionState.Connected,
        new[]
        {
            new TagValue(MachineTagKeys.CompensationStrokeVersion, TagDataType.Int32, version, DateTimeOffset.UnixEpoch),
            new TagValue(MachineTagKeys.CompensationRealtimeOffsetMm, TagDataType.Double, offsetMm, DateTimeOffset.UnixEpoch),
        });

    [Fact]
    public void Each_new_stroke_version_is_recorded_once_and_a_new_roll_starts_over()
    {
        var log = new StrokeCompensationLog(new IdleMonitor());

        log.Observe(Snapshot(0, 0.0));
        log.Observe(Snapshot(1, 0.004));
        log.Observe(Snapshot(1, 0.004));
        log.Observe(Snapshot(2, 0.002));
        log.Snapshot().Select(c => c.Version).Should().Equal(1, 2);
        log.Snapshot()[1].OffsetMm.Should().Be(0.002);

        log.Observe(Snapshot(1, 0.005));
        log.Snapshot().Select(c => c.Version).Should().Equal(new[] { 1 }, "版本回落就是新的一支辊");
    }

    [Fact]
    public void Nothing_is_recorded_when_the_version_is_not_mapped()
    {
        var log = new StrokeCompensationLog(new IdleMonitor());
        log.Observe(MachineStateSnapshot.Empty(DateTimeOffset.UnixEpoch));
        log.Snapshot().Should().BeEmpty();
    }
}
