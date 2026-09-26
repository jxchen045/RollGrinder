using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 阶段 1 顺接辊形：Z 原点是磨削起点（头架侧辊身端面），向尾架为正；
/// 第一段给起点，其余段只给长度，首尾相接，不会重叠或断开。
/// </summary>
public sealed class SequentialProfileTests
{
    private const double Body = 2000.0;
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(Body, 650.0);

    private static RollProfileTypeRegistry Registry => new(new IRollProfileType[]
    {
        new CylindricalProfileType(), new TaperProfileType(), new CrownProfileType(), new CvcProfileType(),
        new PointTableProfileType(),
    });

    private static SequentialSegment Crown(double lengthMm, double micrometer) => new(
        ProfileTypeKeys.Crown,
        lengthMm,
        new CrownProfileType().Schema.CreateDefaults()
            .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(micrometer)));

    /// <summary>锥度 = 端部减薄：端面比相邻段低 <paramref name="micrometer"/>，朝向由所在半边定；镜像标志对它无效。</summary>
    private static SequentialSegment Taper(double lengthMm, double micrometer, bool mirrored = false) => new(
        ProfileTypeKeys.Taper,
        lengthMm,
        new TaperProfileType().Schema.CreateDefaults()
            .With(TaperProfileType.TaperDiameterMicrometerKey, ParameterValue.FromNumber(micrometer)),
        mirrored);

    /// <summary>点表段：两点连直线。</summary>
    private static SequentialSegment Line(double lengthMm, double startMicrometer, double endMicrometer, bool mirrored = false) => new(
        ProfileTypeKeys.PointTable,
        lengthMm,
        PointTableProfileType.DefaultsFor(lengthMm).With(
            PointTableProfileType.PointsKey,
            ParameterValue.FromPoints(new[] { new TablePoint(0.0, startMicrometer), new TablePoint(lengthMm, endMicrometer) })),
        mirrored);

    private static SequentialSegment Cvc(double lengthMm) => new(
        ProfileTypeKeys.Cvc,
        lengthMm,
        new CvcProfileType().Schema.CreateDefaults()
            .With(CvcProfileType.A1DiameterMicrometerKey, ParameterValue.FromNumber(200.0)));

    /// <summary>修改稿 5.2 的例子：锥度 150 + 凸度 1700 + 锥度 150。</summary>
    private static CompositeRollProfile DesignExample() => CompositeRollProfile.Sequential(
        0.0, new[] { Taper(150.0, 50.0), Crown(1700.0, 300.0), Taper(150.0, 50.0) });

    private static double Micrometer(RollProfile profile, double zMm) =>
        UnitConversion.RadiusMmToDiameterMicrometer(profile.RadiusOffsetAtMm(zMm));

    [Fact]
    public void Segments_are_chained_from_the_start_z()
    {
        CompositeRollProfile profile = DesignExample();

        profile.Layout.Should().Be(ProfileLayout.Sequential);
        profile.Segments.Select(s => (s.FromMm, s.ToMm)).Should().Equal((0.0, 150.0), (150.0, 1850.0), (1850.0, 2000.0));
        profile.WithStartZ(10.0).Segments.Select(s => s.FromMm).Should().Equal(10.0, 160.0, 1860.0);
    }

    [Fact]
    public void Each_point_takes_the_value_of_the_one_segment_it_lies_in()
    {
        RollProfile composed = DesignExample().Compose(Geometry, Registry, 401);

        Micrometer(composed, 0.0).Should().BeApproximately(-50.0, 1e-6, "头架端锥度在端面最低");
        Micrometer(composed, 150.0).Should().BeApproximately(0.0, 1e-6);
        Micrometer(composed, 1000.0).Should().BeApproximately(300.0, 1e-6, "凸度在它那一段的中点最高");
        Micrometer(composed, 2000.0).Should().BeApproximately(-50.0, 1e-6, "尾架端锥度在端面最低");
    }

    [Fact]
    public void A_taper_is_an_end_relief_that_faces_the_end_of_its_half_whatever_the_mirror_flag()
    {
        CompositeRollProfile flagged = CompositeRollProfile.Sequential(
            0.0, new[] { Taper(150.0, 50.0, mirrored: true), Crown(1700.0, 300.0), Taper(150.0, 50.0, mirrored: true) });
        RollProfile composed = flagged.Compose(Geometry, Registry, 401);

        Micrometer(composed, 0.0).Should().BeApproximately(-50.0, 1e-6, "镜像标志对锥度无效：头架端照样在端面最低");
        Micrometer(composed, 75.0).Should().BeApproximately(-25.0, 1e-6, "线性回到交界处");
        Micrometer(composed, 2000.0).Should().BeApproximately(-50.0, 1e-6);
        ProfileLayoutCheck.Check(flagged, Body, Registry).Should().BeEmpty("端部减薄在交界处是 0，正好接上凸度");

        // 一段锥度铺满全长：中点正好在正中，算头架半边，端面在头架侧。
        RollProfile whole = CompositeRollProfile.Sequential(0.0, new[] { Taper(Body, 80.0) }).Compose(Geometry, Registry, 401);
        Micrometer(whole, 0.0).Should().BeApproximately(-80.0, 1e-6);
        Micrometer(whole, Body).Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public void Removing_moving_or_resizing_a_segment_rechains_the_rest()
    {
        CompositeRollProfile profile = DesignExample();

        profile.RemoveAt(1).Segments.Select(s => (s.FromMm, s.ToMm)).Should().Equal((0.0, 1700.0), (1700.0, 1850.0));
        profile.MoveDown(1).Segments.Select(s => s.ProfileTypeKey)
            .Should().Equal(ProfileTypeKeys.Crown, ProfileTypeKeys.Taper, ProfileTypeKeys.Taper);
        profile.MoveDown(1).Segments[1].FromMm.Should().Be(1700.0);

        RollProfileSegment longer = profile.Segments[1] with { ToMm = profile.Segments[1].FromMm + 1800.0 };
        profile.Replace(2, longer).Segments[2].Should().Match<RollProfileSegment>(s => s.FromMm == 1950.0 && s.ToMm == 2100.0);

        profile.InsertAfter(1, Taper(20.0, 0.0)).Segments.Select(s => s.FromMm).Should().Equal(0.0, 150.0, 170.0, 1870.0);
    }

    [Fact]
    public void A_sequential_profile_that_does_not_join_up_is_refused()
    {
        FluentActions.Invoking(() => new CompositeRollProfile(
                new[]
                {
                    RollProfileSegment.Create(1, ProfileTypeKeys.Cylindrical, 0.0, 1000.0, ParameterSet.Empty),
                    RollProfileSegment.Create(2, ProfileTypeKeys.Cylindrical, 900.0, 2000.0, ParameterSet.Empty),
                },
                ProfileLayout.Sequential))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void The_design_example_passes_the_check()
    {
        ProfileLayoutCheck.Check(DesignExample(), Body, Registry).Should().BeEmpty();
    }

    [Fact]
    public void Segment_lengths_that_do_not_add_up_to_the_design_length_are_errors()
    {
        IReadOnlyList<ProfileIssue> tooShort = ProfileLayoutCheck.Check(
            CompositeRollProfile.Sequential(0.0, new[] { Crown(1900.0, 100.0) }), Body, Registry);
        tooShort.Should().ContainSingle(i => i.Kind == ProfileIssueKind.NotCovered && i.FromMm == 1900.0 && i.ToMm == 2000.0);

        IReadOnlyList<ProfileIssue> tooLong = ProfileLayoutCheck.Check(
            CompositeRollProfile.Sequential(0.0, new[] { Crown(1900.0, 100.0), Taper(200.0, 0.0) }), Body, Registry);
        tooLong.Should().ContainSingle(i => i.Kind == ProfileIssueKind.OutsideBody && i.SegmentOrder == 2);
    }

    [Fact]
    public void A_step_between_two_segments_is_an_error_with_its_size()
    {
        // 尾架端用点表从 −50 起步，而凸度在段终点是 0——交界处跳 50 µm。
        CompositeRollProfile profile = CompositeRollProfile.Sequential(
            0.0, new[] { Crown(1850.0, 300.0), Line(150.0, -50.0, -80.0) });

        ProfileIssue jump = ProfileLayoutCheck.Check(profile, Body, Registry).Should()
            .ContainSingle(i => i.Kind == ProfileIssueKind.BoundaryJump).Subject;
        jump.IsError.Should().BeTrue();
        jump.FromMm.Should().Be(1850.0);
        jump.JumpMicrometer.Should().BeApproximately(-50.0, 1e-6);
        (jump.OtherSegmentOrder, jump.SegmentOrder).Should().Be((1, 2));
    }

    [Fact]
    public void Symmetric_editing_mirrors_the_headstock_side_around_a_centre_segment()
    {
        SymmetryResult result = ProfileSymmetry.Expand(
            Body, 0.0, new[] { Taper(150.0, 50.0), Crown(1700.0, 300.0) }, Registry);

        result.Failure.Should().Be(SymmetryFailure.None);
        result.Profile!.Segments.Select(s => (s.ProfileTypeKey, s.FromMm, s.ToMm, s.IsMirrored)).Should().Equal(
            (ProfileTypeKeys.Taper, 0.0, 150.0, false),
            (ProfileTypeKeys.Crown, 150.0, 1850.0, false),
            (ProfileTypeKeys.Taper, 1850.0, 2000.0, false));
        result.Profile.Should().BeEquivalentTo(DesignExample(), "展开的结果就是两个独立的段，和手工编的一样");
    }

    [Fact]
    public void Symmetric_editing_without_a_centre_segment_mirrors_everything()
    {
        SymmetryResult result = ProfileSymmetry.Expand(
            Body, 0.0, new[] { Taper(150.0, 50.0), Line(850.0, 0.0, 100.0) }, Registry);

        // 点表段镜像一份（标志翻过来）；锥度不设镜像标志，靠位置朝向尾架端面。
        result.Profile!.Segments.Select(s => (s.ProfileTypeKey, s.FromMm, s.IsMirrored)).Should().Equal(
            (ProfileTypeKeys.Taper, 0.0, false),
            (ProfileTypeKeys.PointTable, 150.0, false),
            (ProfileTypeKeys.PointTable, 1000.0, true),
            (ProfileTypeKeys.Taper, 1850.0, false));
        RollProfile composed = result.Profile.Compose(Geometry, Registry, 401);
        Micrometer(composed, 1000.0).Should().BeApproximately(100.0, 1e-6);
        Micrometer(composed, 1850.0).Should().BeApproximately(0.0, 1e-6);
        Micrometer(composed, 2000.0).Should().BeApproximately(-50.0, 1e-6);
    }

    [Fact]
    public void Symmetric_editing_explains_why_it_cannot_expand()
    {
        ProfileSymmetry.Expand(Body, 0.0, new[] { Taper(150.0, 50.0), Crown(1000.0, 300.0) }, Registry)
            .Should().Match<SymmetryResult>(r => r.Failure == SymmetryFailure.DoesNotReachCenter && r.Profile == null);

        ProfileSymmetry.Expand(Body, 0.0, new[] { Taper(150.0, 50.0), Taper(1700.0, 10.0) }, Registry)
            .Failure.Should().Be(SymmetryFailure.CenterNotSelfSymmetric, "跨中点的锥度本身不对称");

        ProfileSymmetry.Expand(Body, 0.0, new[] { Cvc(1000.0) }, Registry)
            .Failure.Should().Be(SymmetryFailure.UnsupportedType, "CVC 本身不对称，对称开关置灰");
        ProfileSymmetry.IsSupported(new[] { ProfileTypeKeys.Crown, ProfileTypeKeys.Cvc }, Registry).Should().BeFalse();
    }

    [Fact]
    public void A_symmetric_profile_folds_back_to_its_headstock_side_and_an_asymmetric_one_does_not()
    {
        IReadOnlyList<SequentialSegment>? half = ProfileSymmetry.TryFold(DesignExample(), Body, Registry);
        half.Should().NotBeNull();
        half!.Select(s => s.ProfileTypeKey).Should().Equal(ProfileTypeKeys.Taper, ProfileTypeKeys.Crown);

        CompositeRollProfile lopsided = CompositeRollProfile.Sequential(
            0.0, new[] { Taper(150.0, 50.0), Crown(1700.0, 300.0), Taper(150.0, 40.0) });
        ProfileSymmetry.TryFold(lopsided, Body, Registry).Should().BeNull("两端锥度不一样");
    }

    [Fact]
    public void Old_superimposed_profiles_still_compose_the_old_way()
    {
        var old = CompositeRollProfile.Superimposed(new[]
        {
            RollProfileSegment.Create(1, ProfileTypeKeys.Crown, 0.0, 2000.0,
                new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(100.0))),
            RollProfileSegment.Create(2, ProfileTypeKeys.Crown, 0.0, 2000.0,
                new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(40.0))),
        });

        Micrometer(old.Compose(Geometry, Registry, 101), 1000.0).Should().BeApproximately(140.0, 1e-3);
    }

    [Fact]
    public void A_taper_in_an_old_superimposed_profile_keeps_its_old_curve()
    {
        // 旧作业快照、磨削记录里的锥度仍是"从段起点到终点线性变化、正为终点大"，不能跟着新含义变。
        var old = CompositeRollProfile.Superimposed(new[]
        {
            RollProfileSegment.Create(1, ProfileTypeKeys.Taper, 1850.0, 2000.0,
                new TaperProfileType().Schema.CreateDefaults().With(TaperProfileType.TaperDiameterMicrometerKey, ParameterValue.FromNumber(-50.0))),
        });
        RollProfile composed = old.Compose(Geometry, Registry, 401);

        Micrometer(composed, 1850.0).Should().BeApproximately(0.0, 1e-6);
        Micrometer(composed, 2000.0).Should().BeApproximately(-50.0, 1e-6);
        Micrometer(composed, 100.0).Should().BeApproximately(0.0, 1e-6);
    }
}
