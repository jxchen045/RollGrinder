using System;
using System.IO;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Sim;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>仿真加速：只加快"经过了多久"，墙上时钟不动；倍率越界直接拒绝。</summary>
public sealed class SimulationSpeedTests
{
    [Fact]
    public void Elapsed_time_is_scaled_but_wall_clock_is_not()
    {
        var clock = new ManualClock();
        var accelerated = new AcceleratedTimeProvider(clock, factor: 20);

        long start = accelerated.GetTimestamp();
        clock.Advance(TimeSpan.FromSeconds(3));
        long end = accelerated.GetTimestamp();

        accelerated.GetElapsedTime(start, end).Should().Be(TimeSpan.FromSeconds(60));
        accelerated.GetUtcNow().Should().Be(clock.GetUtcNow(), "记录里的时间必须是真实时间");
    }

    [Theory]
    [InlineData("20", 20.0)]
    [InlineData("1", 1.0)]
    [InlineData("100", 100.0)]
    public void Sim_speed_is_parsed(string value, double expected)
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim", "--sim-speed", value }, Path.GetTempPath());

        options.SimulationSpeed.Should().Be(expected);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("101")]
    [InlineData("fast")]
    public void Sim_speed_out_of_range_is_rejected(string value)
    {
        Action act = () => AppOptions.Parse(new[] { "--sim-speed", value }, Path.GetTempPath());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Default_speed_is_real_time()
    {
        AppOptions.Parse(Array.Empty<string>(), Path.GetTempPath()).SimulationSpeed.Should().Be(1.0);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks = 1_000_000;
        private DateTimeOffset now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => this.ticks;

        public override DateTimeOffset GetUtcNow() => this.now;

        public void Advance(TimeSpan delta)
        {
            this.ticks += delta.Ticks;
            this.now += delta;
        }
    }
}
