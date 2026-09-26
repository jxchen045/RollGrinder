using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>辊形设计长度与辊身长度对不上时：按比例拉伸，或以辊身中心对齐不拉伸（修改稿 5.1）。</summary>
public sealed class ProfileFittingTests
{
    private static RollProfileTypeRegistry Registry => new(new IRollProfileType[]
    {
        new CylindricalProfileType(), new TaperProfileType(), new CrownProfileType(), new CvcProfileType(),
        new PointTableProfileType(),
    });

    /// <summary>设计长度 2000：锥度 150（端部减薄 50）+ 凸度 1700（300 µm）+ 锥度 150。</summary>
    private static CompositeRollProfile Design() => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Taper, 150.0,
            new TaperProfileType().Schema.CreateDefaults().With(TaperProfileType.TaperDiameterMicrometerKey, ParameterValue.FromNumber(50.0))),
        new SequentialSegment(ProfileTypeKeys.Crown, 1700.0,
            new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(300.0))),
        new SequentialSegment(ProfileTypeKeys.Taper, 150.0,
            new TaperProfileType().Schema.CreateDefaults().With(TaperProfileType.TaperDiameterMicrometerKey, ParameterValue.FromNumber(50.0))),
    });

    private static double Micrometer(CompositeRollProfile profile, double bodyLengthMm, double zMm) =>
        UnitConversion.RadiusMmToDiameterMicrometer(
            profile.Compose(RollGeometry.FromDiameter(bodyLengthMm, 600.0), Registry, 401).RadiusOffsetAtMm(zMm));

    [Fact]
    public void Equal_lengths_need_no_fitting()
    {
        CompositeRollProfile design = Design();
        ProfileFitting.Fit(design, 2000.0, 2000.3, ProfileFitMode.Stretch, Registry, 101).Should().BeSameAs(design);
    }

    [Fact]
    public void Stretching_scales_every_segment_and_keeps_the_shape()
    {
        CompositeRollProfile fitted = ProfileFitting.Fit(Design(), 2000.0, 2400.0, ProfileFitMode.Stretch, Registry, 101);

        fitted.Segments.Select(s => (s.FromMm, s.ToMm)).Should().Equal((0.0, 180.0), (180.0, 2220.0), (2220.0, 2400.0));
        Micrometer(fitted, 2400.0, 1200.0).Should().BeApproximately(300.0, 1e-6, "凸度仍在正中最高");
        Micrometer(fitted, 2400.0, 0.0).Should().BeApproximately(-50.0, 1e-6);
        ProfileLayoutCheck.Check(fitted, 2400.0, Registry).Should().BeEmpty();
    }

    [Fact]
    public void Stretching_moves_point_table_points_with_the_segment()
    {
        ParameterSet table = PointTableProfileType.DefaultsFor(1000.0).With(
            PointTableProfileType.PointsKey,
            ParameterValue.FromPoints(new[] { new TablePoint(0.0, 0.0), new TablePoint(500.0, 20.0), new TablePoint(1000.0, 0.0) }));
        CompositeRollProfile design = CompositeRollProfile.Sequential(0.0, new[] { new SequentialSegment(ProfileTypeKeys.PointTable, 1000.0, table) });

        CompositeRollProfile fitted = ProfileFitting.Fit(design, 1000.0, 2000.0, ProfileFitMode.Stretch, Registry, 101);

        fitted.Segments[0].Parameters.Get(PointTableProfileType.PointsKey).Points.Select(p => p.X).Should().Equal(0.0, 1000.0, 2000.0);
        ProfileLayoutCheck.Check(fitted, 2000.0, Registry).Should().BeEmpty();
    }

    [Fact]
    public void Centre_alignment_on_a_longer_roll_keeps_the_curve_and_holds_the_end_values()
    {
        CompositeRollProfile fitted = ProfileFitting.Fit(Design(), 2000.0, 2400.0, ProfileFitMode.CenterAlign, Registry, 401);

        Micrometer(fitted, 2400.0, 1200.0).Should().BeApproximately(300.0, 0.5, "设计曲线的中点落在辊身中点");
        Micrometer(fitted, 2400.0, 200.0).Should().BeApproximately(-50.0, 0.5, "设计曲线从 Z 200 起");
        Micrometer(fitted, 2400.0, 50.0).Should().BeApproximately(-50.0, 0.5, "多出来的一截按端点的值延伸，不留台阶");
        ProfileLayoutCheck.Check(fitted, 2400.0, Registry).Should().BeEmpty();
    }

    [Fact]
    public void Centre_alignment_on_a_shorter_roll_crops_both_ends()
    {
        CompositeRollProfile fitted = ProfileFitting.Fit(Design(), 2000.0, 1600.0, ProfileFitMode.CenterAlign, Registry, 401);

        Micrometer(fitted, 1600.0, 800.0).Should().BeApproximately(300.0, 0.5);
        double expectedEnd = UnitConversion.RadiusMmToDiameterMicrometer(
            Design().Compose(RollGeometry.FromDiameter(2000.0, 600.0), Registry, 401).RadiusOffsetAtMm(200.0));
        Micrometer(fitted, 1600.0, 0.0).Should().BeApproximately(expectedEnd, 0.5, "辊身端面对应设计曲线的 Z 200");
    }
}
