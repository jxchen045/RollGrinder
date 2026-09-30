using System.Linq;
using FluentAssertions;
using RollGrinder.App.Controls;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>辊形图、偏差图的坐标范围（分辨率适配方案第 4 节）。</summary>
public sealed class ChartRangesTests
{
    [Fact]
    public void Profile_range_depends_only_on_the_target_and_includes_the_baseline()
    {
        double[] crown = Enumerable.Range(0, 181).Select(i => 300.0 * (1 - System.Math.Pow((2.0 * i / 180) - 1, 2))).ToArray();
        (double low, double high) = ChartRanges.Profile(crown);
        low.Should().BeLessThan(0.0);
        high.Should().BeGreaterThan(300.0);
        ChartRanges.Profile(crown.Reverse()).Should().Be((low, high), "同一条辊形在哪一页都是同一个范围");
    }

    [Fact]
    public void A_flat_profile_still_spans_at_least_100_micrometre()
    {
        (double low, double high) = ChartRanges.Profile(new[] { 0.0, 0.5, 0.0 });
        (high - low).Should().BeGreaterThanOrEqualTo(ChartRanges.MinProfileSpanMicrometer);
    }

    [Fact]
    public void Deviation_range_is_symmetric_and_covers_twice_the_tolerance()
    {
        (double low, double high) = ChartRanges.Deviation(5.0, new[] { -3.0, 4.0 });
        low.Should().Be(-high);
        high.Should().BeGreaterThanOrEqualTo(10.0);
        ChartRanges.Deviation(5.0, new[] { 40.0 }).High.Should().BeGreaterThanOrEqualTo(44.0);
    }

    [Theory]
    [InlineData(0.7, 1.0)]
    [InlineData(1.3, 2.0)]
    [InlineData(37.0, 50.0)]
    [InlineData(80.0, 100.0)]
    public void Nice_steps_are_1_2_5(double raw, double expected) => ChartRanges.NiceStep(raw).Should().Be(expected);
}
