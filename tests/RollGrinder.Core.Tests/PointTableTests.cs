using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>点表辊形与四种插值（阶段 1，Q8）。</summary>
public sealed class PointTableTests
{
    private static RollProfileTypeRegistry Registry => new(new IRollProfileType[]
    {
        new CylindricalProfileType(), new TaperProfileType(), new CrownProfileType(), new CvcProfileType(),
        new PointTableProfileType(),
    });

    private static Func<double, double> Build(InterpolationMethod method, double[] xs, double[] ys, double smoothing = 0.0) =>
        Interpolation.Build(method, xs, ys, smoothing);

    [Theory]
    [InlineData(InterpolationMethod.Linear)]
    [InlineData(InterpolationMethod.CubicSpline)]
    [InlineData(InterpolationMethod.ShapePreserving)]
    public void Interpolating_methods_pass_through_every_point(InterpolationMethod method)
    {
        double[] xs = { 0.0, 100.0, 250.0, 400.0, 600.0 };
        double[] ys = { 0.0, 30.0, 45.0, 20.0, -10.0 };
        Func<double, double> f = Build(method, xs, ys);

        for (int i = 0; i < xs.Length; i++)
        {
            f(xs[i]).Should().BeApproximately(ys[i], 1e-9);
        }

        f(-50.0).Should().Be(f(0.0), "超出范围取端点的值");
        f(700.0).Should().BeApproximately(-10.0, 1e-9);
    }

    [Fact]
    public void The_natural_cubic_spline_matches_the_hand_calculation()
    {
        // (0,0) (1,1) (2,0)：内点二阶导 γ₁ = −3，S(0.5) = 0.5 − 0.5 × (0.125 − 0.5) = 0.6875。
        Build(InterpolationMethod.CubicSpline, new[] { 0.0, 1.0, 2.0 }, new[] { 0.0, 1.0, 0.0 })(0.5)
            .Should().BeApproximately(0.6875, 1e-12);
    }

    [Fact]
    public void Shape_preserving_does_not_overshoot_where_the_cubic_spline_does()
    {
        double[] xs = { 0.0, 1.0, 2.0, 3.0 };
        double[] ys = { 0.0, 0.0, 1.0, 1.0 };
        double[] probe = Enumerable.Range(0, 301).Select(i => i / 100.0).ToArray();

        double splineMin = probe.Min(Build(InterpolationMethod.CubicSpline, xs, ys));
        splineMin.Should().BeLessThan(-0.01, "三次样条在台阶前会往下鼓");

        Func<double, double> shape = Build(InterpolationMethod.ShapePreserving, xs, ys);
        probe.Select(shape).Should().OnlyContain(v => v >= -1e-12 && v <= 1.0 + 1e-12);
        probe.Zip(probe.Skip(1), (a, b) => shape(b) - shape(a)).Should().OnlyContain(d => d >= -1e-12, "单调数据插出来仍单调");
    }

    [Fact]
    public void Smoothing_zero_is_the_cubic_spline_and_more_smoothing_flattens_noise()
    {
        double[] xs = Enumerable.Range(0, 21).Select(i => i * 100.0).ToArray();
        double[] ys = xs.Select((x, i) => (0.01 * x) + (i % 2 == 0 ? 3.0 : -3.0)).ToArray();
        // 看辊身中间那一截：两端各两个点距上平滑样条手里的点少，本来就压不平（与独立的稠密解对过数）。
        double[] probe = Enumerable.Range(300, 1401).Select(i => (double)i).ToArray();

        Func<double, double> spline = Build(InterpolationMethod.CubicSpline, xs, ys);
        Build(InterpolationMethod.SmoothingSpline, xs, ys, 0.0)(550.0).Should().BeApproximately(spline(550.0), 1e-9);

        double Wiggle(Func<double, double> f) => probe.Max(x => Math.Abs(f(x) - (0.01 * x)));
        double noisy = Wiggle(spline);
        double half = Wiggle(Build(InterpolationMethod.SmoothingSpline, xs, ys, 0.5));
        double strong = Wiggle(Build(InterpolationMethod.SmoothingSpline, xs, ys, 0.9));
        noisy.Should().BeGreaterThanOrEqualTo(3.0 - 1e-9, "过每个点的样条把 ±3 的锯齿全留着");
        half.Should().BeLessThan(noisy, "平滑度越大越平");
        strong.Should().BeLessThan(half);
        strong.Should().BeLessThan(noisy / 5.0, "平滑样条把锯齿压下去，只留下那条斜线");
    }

    [Fact]
    public void A_point_table_segment_follows_its_points_in_local_z()
    {
        ParameterSet table = PointTableProfileType.DefaultsFor(500.0)
            .With(PointTableProfileType.PointsKey, ParameterValue.FromPoints(new[]
            {
                new TablePoint(0.0, -40.0), new TablePoint(250.0, 0.0), new TablePoint(500.0, 0.0),
            }))
            .With(PointTableProfileType.InterpolationKey, ParameterValue.FromChoice(nameof(InterpolationMethod.Linear)));
        CompositeRollProfile profile = CompositeRollProfile.Sequential(0.0, new[]
        {
            new SequentialSegment(ProfileTypeKeys.Cylindrical, 1500.0, ParameterSet.Empty),
            new SequentialSegment(ProfileTypeKeys.PointTable, 500.0, table, IsMirrored: true),
        });

        RollProfile composed = profile.Compose(RollGeometry.FromDiameter(2000.0, 600.0), Registry, 801);

        UnitConversion.RadiusMmToDiameterMicrometer(composed.RadiusOffsetAtMm(2000.0))
            .Should().BeApproximately(-40.0, 1e-6, "镜像后表里 Z = 0 那一点落到尾架端");
        UnitConversion.RadiusMmToDiameterMicrometer(composed.RadiusOffsetAtMm(1875.0))
            .Should().BeApproximately(-20.0, 1e-6);
        ProfileLayoutCheck.Check(profile, 2000.0, Registry).Should().BeEmpty();
    }

    [Fact]
    public void Point_tables_that_are_too_short_unordered_or_not_spanning_the_segment_are_errors()
    {
        var type = new PointTableProfileType();
        ParameterSet With(params TablePoint[] points) =>
            PointTableProfileType.DefaultsFor(100.0).With(PointTableProfileType.PointsKey, ParameterValue.FromPoints(points));

        type.ValidateShape(With(new TablePoint(0.0, 0.0)), 100.0).Single().Kind.Should().Be(ParameterViolationKind.TooFewPoints);
        type.ValidateShape(With(new TablePoint(0.0, 0.0), new TablePoint(60.0, 1.0), new TablePoint(60.0, 2.0), new TablePoint(100.0, 0.0)), 100.0)
            .Single().Kind.Should().Be(ParameterViolationKind.PointsNotIncreasing);
        type.ValidateShape(With(new TablePoint(0.0, 0.0), new TablePoint(80.0, 0.0)), 100.0)
            .Single().Should().Be(new ParameterViolation(PointTableProfileType.PointsKey, ParameterViolationKind.PointsDoNotCoverSegment, 100.0));
        type.ValidateShape(PointTableProfileType.DefaultsFor(100.0), 100.0).Should().BeEmpty();
    }

    [Fact]
    public void Point_values_round_trip_through_their_invariant_text()
    {
        ParameterValue value = ParameterValue.FromPoints(new[] { new TablePoint(0.0, -12.5), new TablePoint(1234.5, 0.1) });

        ParameterValue back = ParameterValue.Parse(ParameterValueKind.Points, value.ToInvariantString());

        back.Should().Be(value);
        back.Points.Should().Equal(new TablePoint(0.0, -12.5), new TablePoint(1234.5, 0.1));
    }

    [Fact]
    public void A_csv_with_headers_and_absolute_z_is_read_and_moved_to_start_at_zero()
    {
        IReadOnlyList<TablePoint> read = PointTableCsv.Parse(new[]
        {
            "bodyPositionMm,diameterOffsetMicrometer", "# 现场点表", "", "1200, 5", "1000;0", "1100\t3",
        });

        read.Should().HaveCount(3);
        PointTableCsv.StartAtZero(read).Should().Equal(new TablePoint(0.0, 0.0), new TablePoint(100.0, 3.0), new TablePoint(200.0, 5.0));
    }

    [Fact]
    public void An_old_superimposed_profile_becomes_one_point_table_with_the_same_curve()
    {
        var geometry = RollGeometry.FromDiameter(2000.0, 600.0);
        var old = CompositeRollProfile.Superimposed(new[]
        {
            RollProfileSegment.Create(1, ProfileTypeKeys.Crown, 0.0, 2000.0,
                new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(300.0))),
            RollProfileSegment.Create(2, ProfileTypeKeys.Taper, 0.0, 150.0,
                new TaperProfileType().Schema.CreateDefaults().With(TaperProfileType.TaperDiameterMicrometerKey, ParameterValue.FromNumber(-50.0)),
                isMirrored: true),
        });

        CompositeRollProfile converted = LegacyProfileConversion.ToPointTable(old, geometry, Registry, 101);

        converted.Layout.Should().Be(ProfileLayout.Sequential);
        converted.Segments.Should().ContainSingle().Which.ProfileTypeKey.Should().Be(ProfileTypeKeys.PointTable);
        RollProfile before = old.Compose(geometry, Registry, 101);
        RollProfile after = converted.Compose(geometry, Registry, 101);
        after.Points.Select(p => p.RadiusOffsetMm).Should().Equal(
            before.Points.Select(p => p.RadiusOffsetMm), (a, b) => Math.Abs(a - b) < 1e-9, "形状完全不变");
        ProfileLayoutCheck.Check(converted, 2000.0, Registry).Should().BeEmpty();
    }
}
