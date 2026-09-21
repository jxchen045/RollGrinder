using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Data.Model;
using RollGrinder.Sim;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 补偿闭环：把真的补偿算法套在带系统性偏差的仿真辊面上跑几轮，误差要真的变小。
///
/// 这是仿真做厚之后最值钱的一条——补偿的符号、增益、限幅，任何一处反了或错了，
/// 误差都会不降反升，而这在没有机床的时候原本是看不出来的。
/// </summary>
public sealed class CompensationConvergenceTests
{
    private const double BodyLengthMm = 2000.0;
    private const double NominalDiameterMm = 650.0;
    private const int SamplePoints = 41;

    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(BodyLengthMm, NominalDiameterMm);

    /// <summary>目标辊形：120 µm 直径量的凸度。</summary>
    private static RollProfile Target() => new CrownProfileType().CreateProfile(
        Geometry,
        new CrownProfileType().Schema.CreateDefaults()
            .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(120.0)),
        SamplePoints);

    /// <summary>
    /// 磨一支辊：按「目标辊形 + 补偿」下发，磨到余量见底，再测一遍。
    /// </summary>
    private static MeasuredProfile GrindAndMeasure(RollProfile target, RollProfile? compensation, int seed)
    {
        var surface = new RollSurfaceModel(BodyLengthMm, Geometry.NominalRadiusMm, seed);

        // 下发的是目标辊形叠上补偿——与 NcJobTranslator 做的事情一样。
        RollProfile commanded = compensation is null ? target : target.Add(compensation);
        for (int i = 0; i < commanded.Points.Count; i++)
        {
            ProfilePoint point = commanded.Points[i];
            surface.SetCommandedPoint(i, point.BodyPositionMm, point.RadiusOffsetMm);
        }

        // 磨到余量见底：剩下的就是机床自己那份可重复的偏差。
        surface.Remove(RollSurfaceModel.InitialStockRadiusMm);

        return new MeasuredProfile(surface
            .SampleMeanProfile(SamplePoints)
            .Select(sample => new MeasurementPoint(sample.BodyPositionMm, sample.RadiusMm)));
    }

    private static double WorstDeviationMicrometer(RollProfile deviation) =>
        deviation.Points.Max(point => Math.Abs(point.RadiusOffsetMm)) * 2000.0;

    [Fact]
    public void Three_rounds_of_compensation_shrink_the_error()
    {
        RollProfile target = Target();
        CompensationSettings settings = CompensationSettings.Create(0.7, 5, 0.05);

        RollProfile? compensation = null;
        var worst = new List<double>();

        for (int round = 0; round < 3; round++)
        {
            MeasuredProfile measured = GrindAndMeasure(target, compensation, seed: 7);
            RollProfile deviation = CompensationCalculator.ComputeDeviation(measured, target, Geometry);

            worst.Add(WorstDeviationMicrometer(deviation));
            compensation = CompensationCalculator.ComputeCompensation(compensation, deviation, settings);
        }

        worst.Should().HaveCount(3);
        worst[1].Should().BeLessThan(worst[0], "补了一轮误差就该小一圈");
        worst[2].Should().BeLessThan(worst[1], "再补一轮还要小");

        // 增益 0.7，两轮之后剩下的大致是 (1−0.7)² ≈ 9%，放宽到三成以内。
        worst[2].Should().BeLessThan(worst[0] * 0.3);
    }

    [Fact]
    public void The_uncompensated_error_is_the_machines_own_droop()
    {
        // 第一轮测出来的误差应当就是系统性偏差——补偿要对付的正是它。
        RollProfile target = Target();
        var reference = new RollSurfaceModel(BodyLengthMm, Geometry.NominalRadiusMm, seed: 7);

        MeasuredProfile measured = GrindAndMeasure(target, null, seed: 7);
        RollProfile deviation = CompensationCalculator.ComputeDeviation(measured, target, Geometry);

        foreach (ProfilePoint point in deviation.Points)
        {
            point.RadiusOffsetMm.Should().BeApproximately(
                reference.SystematicErrorAtMm(point.BodyPositionMm), 1e-9);
        }
    }

    [Fact]
    public void Compensation_pushes_against_the_error_not_with_it()
    {
        // 符号反了的话误差会越补越大，这条把方向钉死。
        RollProfile target = Target();
        CompensationSettings settings = CompensationSettings.Create(0.7, 5, 0.05);

        MeasuredProfile measured = GrindAndMeasure(target, null, seed: 7);
        RollProfile deviation = CompensationCalculator.ComputeDeviation(measured, target, Geometry);
        RollProfile compensation = CompensationCalculator.ComputeCompensation(null, deviation, settings);

        // 取偏差最大的那一点：补偿必须与它反号。
        ProfilePoint worst = deviation.Points
            .OrderByDescending(point => Math.Abs(point.RadiusOffsetMm)).First();
        double correction = compensation.RadiusOffsetAtMm(worst.BodyPositionMm);

        (correction * worst.RadiusOffsetMm).Should().BeLessThan(0.0, "磨少了就该多磨一点，不是更少");
    }

    [Fact]
    public void Two_rolls_off_the_same_machine_need_the_same_correction()
    {
        // 系统性偏差是机床的，不是这一支辊的：换一支来料不同的辊子，
        // 磨完之后要补的那条曲线应当基本一样。这就是补偿能攒下来复用的前提。
        RollProfile target = Target();

        RollProfile first = CompensationCalculator.ComputeDeviation(
            GrindAndMeasure(target, null, seed: 11), target, Geometry);
        RollProfile second = CompensationCalculator.ComputeDeviation(
            GrindAndMeasure(target, null, seed: 29), target, Geometry);

        for (int i = 0; i < first.Points.Count; i++)
        {
            first.Points[i].RadiusOffsetMm.Should().BeApproximately(
                second.Points[i].RadiusOffsetMm, 1e-9, "来料误差磨掉了，剩下的是机床那一份");
        }
    }
}
