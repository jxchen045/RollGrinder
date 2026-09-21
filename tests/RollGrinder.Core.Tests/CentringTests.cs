using System;
using FluentAssertions;
using RollGrinder.Core;
using RollGrinder.Core.Centring;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 对中：两端各记一组读数，比的是两端之差。
///
/// 量的是装夹不是辊形，所以这里守的是"哪个差调得掉、哪个调不掉、往哪边调"。
/// </summary>
public sealed class CentringTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static CentringReading Reading(
        RollEnd end,
        double probeAMm,
        double probeBMm,
        double diameterMm = 2000.0,
        double carriageMm = 0.0) =>
        new(end, probeAMm, probeBMm, diameterMm, carriageMm, 0.0, Now);

    [Fact]
    public void The_mounting_deviation_is_half_the_gap_between_the_probes()
    {
        // 两个测头夹着辊面：辊心偏了，一个多进、另一个少进同样多。
        Reading(RollEnd.Head, 0.010, -0.010).MountingDeviationRadiusMm.Should().BeApproximately(0.010, 1e-12);
        Reading(RollEnd.Head, 0.004, 0.004).MountingDeviationRadiusMm.Should().Be(0.0, "两边一样就是正的");
    }

    [Fact]
    public void A_roll_mounted_straight_passes()
    {
        CentringComparison comparison = CentringComparison.Compare(
            Reading(RollEnd.Head, 0.005, -0.005),
            Reading(RollEnd.Tail, 0.005, -0.005),
            toleranceMicrometer: 10.0);

        comparison.DeviationDifferenceMicrometer.Should().BeApproximately(0.0, 1e-9);
        comparison.IsWithinTolerance.Should().BeTrue();
        comparison.Adjustment.Should().Be(CentringAdjustment.None);
    }

    [Fact]
    public void The_side_that_sits_high_is_the_side_to_adjust()
    {
        // 头架侧偏差比尾架侧大 ⇒ 头架侧偏高 ⇒ 往里调头架侧。
        CentringComparison comparison = CentringComparison.Compare(
            Reading(RollEnd.Head, 0.020, -0.020),
            Reading(RollEnd.Tail, 0.000, 0.000),
            toleranceMicrometer: 10.0);

        // 半径量 0.020 mm ⇒ 直径量 40 µm。
        comparison.DeviationDifferenceMicrometer.Should().BeApproximately(40.0, 1e-9);
        comparison.Adjustment.Should().Be(CentringAdjustment.HeadInward);

        CentringComparison other = CentringComparison.Compare(
            Reading(RollEnd.Head, 0.000, 0.000),
            Reading(RollEnd.Tail, 0.020, -0.020),
            toleranceMicrometer: 10.0);

        other.Adjustment.Should().Be(CentringAdjustment.TailInward);
    }

    [Fact]
    public void Taper_in_the_roll_itself_is_reported_separately()
    {
        // 两端直径不同是辊本身的锥度：调中心架调不掉它。
        // 混进对中判据里，人会白调半天还调不好。
        CentringComparison comparison = CentringComparison.Compare(
            Reading(RollEnd.Head, 0.0, 0.0, diameterMm: 2000.100),
            Reading(RollEnd.Tail, 0.0, 0.0, diameterMm: 2000.000),
            toleranceMicrometer: 10.0);

        comparison.DiameterDifferenceMicrometer.Should().BeApproximately(100.0, 1e-6);
        comparison.IsWithinTolerance.Should().BeTrue("锥度不是对中不合格");
        comparison.Adjustment.Should().Be(CentringAdjustment.None);
    }

    [Fact]
    public void Exactly_on_the_tolerance_still_passes()
    {
        // 卡在公差上算合格：否则现场会为了一个 0.0 µm 的余量反复调。
        CentringComparison comparison = CentringComparison.Compare(
            Reading(RollEnd.Head, 0.005, 0.0),
            Reading(RollEnd.Tail, 0.0, 0.0),
            toleranceMicrometer: 5.0);

        comparison.DeviationDifferenceMicrometer.Should().BeApproximately(5.0, 1e-9);
        comparison.IsWithinTolerance.Should().BeTrue();
    }

    [Fact]
    public void Two_readings_from_the_same_end_are_not_a_comparison()
    {
        // 记了两次头架侧就按"记过了"算的话，算出来的差恒为零，
        // 一台没对正的机床会显示合格。
        Action compare = () => CentringComparison.Compare(
            Reading(RollEnd.Head, 0.0, 0.0),
            Reading(RollEnd.Head, 0.0, 0.0),
            toleranceMicrometer: 10.0);

        compare.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_tolerance_of_zero_is_refused()
    {
        // 公差为 0 意味着永远不合格；那是配置写错了，不是机床的问题。
        Action compare = () => CentringComparison.Compare(
            Reading(RollEnd.Head, 0.0, 0.0),
            Reading(RollEnd.Tail, 0.0, 0.0),
            toleranceMicrometer: 0.0);

        compare.Should().Throw<DomainException>();
    }
}
