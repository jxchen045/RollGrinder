using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Sim;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 仿真辊面。只有辊面真的带着误差、磨削真的把它磨掉、机床真的留下一份可重复的
/// 系统性偏差，磨前磨后曲线与补偿收敛才有东西可验。
/// </summary>
public sealed class RollSurfaceModelTests
{
    private const double BodyLengthMm = 2000.0;
    private const double NominalRadiusMm = 325.0;

    private static RollSurfaceModel Fresh(int seed = 20260921) =>
        new(BodyLengthMm, NominalRadiusMm, seed);

    [Fact]
    public void The_same_seed_gives_the_same_roll_every_time()
    {
        // 读取顺序不确定（界面按需要读哪个点读哪个点），所以噪声必须由位置算，
        // 不能拉一个随机数序列——不然同一个点两次读数不一样，测试就没法断言。
        RollSurfaceModel first = Fresh();
        RollSurfaceModel second = Fresh();

        foreach (double z in new[] { 0.0, 137.0, 999.9, 2000.0 })
        {
            second.MeanRadiusAtMm(z).Should().Be(first.MeanRadiusAtMm(z));
            first.MeanRadiusAtMm(z).Should().Be(first.MeanRadiusAtMm(z), "同一个点读两次必须一样");
        }
    }

    [Fact]
    public void A_different_seed_gives_a_different_roll()
    {
        Fresh(1).MeanRadiusAtMm(500.0).Should().NotBe(Fresh(2).MeanRadiusAtMm(500.0));
    }

    [Fact]
    public void An_as_received_roll_carries_both_stock_and_error()
    {
        RollSurfaceModel surface = Fresh();

        surface.RemainingStockRadiusMm.Should().Be(RollSurfaceModel.InitialStockRadiusMm);
        surface.AsReceivedFraction.Should().Be(1.0);

        // 中段来料误差明显不为零——不是一根漂亮的圆柱。
        Math.Abs(surface.AsReceivedErrorAtMm(BodyLengthMm / 2.0)).Should().BeGreaterThan(0.005);
    }

    [Fact]
    public void Grinding_eats_the_stock_and_the_as_received_error_together()
    {
        RollSurfaceModel surface = Fresh();

        surface.Remove(RollSurfaceModel.InitialStockRadiusMm / 2.0);

        surface.RemainingStockRadiusMm.Should().BeApproximately(RollSurfaceModel.InitialStockRadiusMm / 2.0, 1e-9);
        surface.AsReceivedFraction.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Once_the_stock_is_gone_what_is_left_is_the_machines_own_error()
    {
        // 这是整个仿真的重点：磨完之后测出来的误差 ≈ 系统性偏差，
        // 补偿要对付的就是它。
        RollSurfaceModel surface = Fresh();
        surface.Remove(RollSurfaceModel.InitialStockRadiusMm * 2.0);

        surface.RemainingStockRadiusMm.Should().Be(0.0);
        surface.AsReceivedFraction.Should().Be(0.0);

        foreach (double z in new[] { 0.0, 500.0, 1000.0, 1500.0, 2000.0 })
        {
            surface.MeanRadiusAtMm(z).Should().BeApproximately(
                NominalRadiusMm + surface.SystematicErrorAtMm(z), 1e-9);
        }
    }

    [Fact]
    public void The_systematic_error_is_repeatable_and_shaped_like_a_droop()
    {
        RollSurfaceModel surface = Fresh();

        // 靠尾架端下垂：末端比起点低。
        surface.SystematicErrorAtMm(BodyLengthMm).Should().BeLessThan(surface.SystematicErrorAtMm(0.0));

        // 与磨了多少无关——这正是"可重复"的意思。
        double before = surface.SystematicErrorAtMm(1000.0);
        surface.Remove(0.2);
        surface.SystematicErrorAtMm(1000.0).Should().Be(before);
    }

    [Fact]
    public void The_commanded_profile_shows_up_in_the_surface()
    {
        // 下发的辊形（已含补偿）是磨削的目标：辊面得照着它走，
        // 不然补偿叠进去也看不到效果。
        RollSurfaceModel surface = Fresh();
        surface.SetCommandedPoint(0, 0.0, 0.0);
        surface.SetCommandedPoint(1, 1000.0, 0.060);
        surface.SetCommandedPoint(2, 2000.0, 0.0);

        surface.CommandedOffsetAtMm(1000.0).Should().BeApproximately(0.060, 1e-9);
        surface.CommandedOffsetAtMm(500.0).Should().BeApproximately(0.030, 1e-9, "点之间线性插值");
        surface.CommandedOffsetAtMm(-50.0).Should().Be(0.0, "端点之外取端点值");
        surface.CommandedOffsetAtMm(9999.0).Should().Be(0.0);
    }

    [Fact]
    public void Roundness_has_the_lobe_count_we_modelled()
    {
        // 三瓣形：绕一圈应当出现三个波峰。
        RollSurfaceModel surface = Fresh();
        const double z = 1000.0;

        int peaks = 0;
        const int samples = 360;
        for (int i = 0; i < samples; i++)
        {
            double previous = surface.RoundnessAtMm(z, 2.0 * Math.PI * (i - 1) / samples);
            double current = surface.RoundnessAtMm(z, 2.0 * Math.PI * i / samples);
            double next = surface.RoundnessAtMm(z, 2.0 * Math.PI * (i + 1) / samples);

            if (current > previous && current >= next)
            {
                peaks++;
            }
        }

        peaks.Should().Be(RollSurfaceModel.RoundnessLobes);
    }

    [Fact]
    public void Eccentricity_goes_round_once_per_revolution_and_does_not_grind_away()
    {
        // 装夹偏心跟着回转中心走，不是辊面形状，磨不掉。
        RollSurfaceModel surface = Fresh();
        const double z = 1000.0;

        double atZero = surface.EccentricityAtMm(z, 0.0);
        double atHalf = surface.EccentricityAtMm(z, Math.PI);

        atZero.Should().BeApproximately(-atHalf, 1e-9, "半圈之后正好反号");
        Math.Abs(atZero).Should().BeGreaterThan(0.001);

        surface.Remove(RollSurfaceModel.InitialStockRadiusMm * 2.0);
        surface.EccentricityAtMm(z, 0.0).Should().Be(atZero, "磨完照样在");
    }

    [Fact]
    public void Roundness_gets_better_as_the_roll_is_ground()
    {
        RollSurfaceModel surface = Fresh();
        const double z = 1000.0;

        double before = Math.Abs(surface.RoundnessAtMm(z, 0.0));
        surface.Remove(RollSurfaceModel.InitialStockRadiusMm);
        double after = Math.Abs(surface.RoundnessAtMm(z, 0.0));

        after.Should().BeLessThan(before);
    }

    [Fact]
    public void The_grinding_current_tracks_the_load()
    {
        RollSurfaceModel surface = Fresh();

        double idle = surface.GrindingCurrentA(0.0, 0.0, isCutting: false);
        double cutting = surface.GrindingCurrentA(0.03, 1000.0, isCutting: true);

        idle.Should().BeApproximately(6.0, 1e-9, "空载打底");
        cutting.Should().BeGreaterThan(idle);

        // 余量磨掉之后同样的进给电流下来一截。
        surface.Remove(RollSurfaceModel.InitialStockRadiusMm);
        surface.GrindingCurrentA(0.03, 1000.0, isCutting: true).Should().BeLessThan(cutting);
    }

    [Fact]
    public void The_sampled_profile_covers_the_whole_body()
    {
        IReadOnlyList<(double BodyPositionMm, double RadiusMm)> points = Fresh().SampleMeanProfile(21);

        points.Should().HaveCount(21);
        points[0].BodyPositionMm.Should().Be(0.0);
        points[^1].BodyPositionMm.Should().Be(BodyLengthMm);
        points.Select(point => point.BodyPositionMm).Should().BeInAscendingOrder();
    }
}
