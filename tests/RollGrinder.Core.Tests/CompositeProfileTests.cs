using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 辊形由多段叠加：主辊形铺满全长，端部锥度只作用在两端。
/// </summary>
public sealed class CompositeProfileTests
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    private static RollProfileTypeRegistry Registry => new(new IRollProfileType[]
    {
        new CylindricalProfileType(), new TaperProfileType(), new CrownProfileType(), new CvcProfileType(),
    });

    private static ParameterSet Crown(double diameterMicrometer) =>
        new CrownProfileType().Schema.CreateDefaults()
            .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(diameterMicrometer));

    private static ParameterSet Taper(double diameterMicrometer) =>
        new TaperProfileType().Schema.CreateDefaults()
            .With(TaperProfileType.TaperDiameterMicrometerKey, ParameterValue.FromNumber(diameterMicrometer));

    [Fact]
    public void A_single_segment_behaves_like_the_bare_profile_type()
    {
        CompositeRollProfile composite = CompositeRollProfile.Single(ProfileTypeKeys.Crown, Geometry, Crown(120.0));

        RollProfile composed = composite.Compose(Geometry, Registry, 101);
        RollProfile direct = new CrownProfileType().CreateProfile(Geometry, Crown(120.0), 101);

        UnitConversion.RadiusMmToDiameterMicrometer(composed.RadiusOffsetAtMm(1000.0))
            .Should().BeApproximately(
                UnitConversion.RadiusMmToDiameterMicrometer(direct.RadiusOffsetAtMm(1000.0)), 1e-6);
    }

    [Fact]
    public void A_segment_contributes_nothing_outside_its_range()
    {
        var composite = CompositeRollProfile.Superimposed(new[]
        {
            RollProfileSegment.Create(1, ProfileTypeKeys.Cylindrical, 0.0, 2000.0, ParameterSet.Empty),
            RollProfileSegment.Create(2, ProfileTypeKeys.Taper, 1850.0, 2000.0, Taper(-200.0)),
        });

        RollProfile composed = composite.Compose(Geometry, Registry, 201);

        composed.RadiusOffsetAtMm(1000.0).Should().BeApproximately(0.0, 1e-9, "锥度段不该影响辊身中部");
        UnitConversion.RadiusMmToDiameterMicrometer(composed.RadiusOffsetAtMm(2000.0))
            .Should().BeApproximately(-200.0, 1.0, "尾端应落到设定的锥度值");
    }

    [Fact]
    public void Segments_add_up_where_they_overlap()
    {
        var composite = CompositeRollProfile.Superimposed(new[]
        {
            RollProfileSegment.Create(1, ProfileTypeKeys.Crown, 0.0, 2000.0, Crown(100.0)),
            RollProfileSegment.Create(2, ProfileTypeKeys.Crown, 0.0, 2000.0, Crown(40.0)),
        });

        RollProfile composed = composite.Compose(Geometry, Registry, 101);

        UnitConversion.RadiusMmToDiameterMicrometer(composed.RadiusOffsetAtMm(1000.0))
            .Should().BeApproximately(140.0, 1e-3);
    }

    [Fact]
    public void A_mirrored_segment_runs_the_other_way()
    {
        var straight = CompositeRollProfile.Superimposed(new[]
        {
            RollProfileSegment.Create(1, ProfileTypeKeys.Taper, 0.0, 2000.0, Taper(200.0)),
        });
        var mirrored = CompositeRollProfile.Superimposed(new[]
        {
            RollProfileSegment.Create(1, ProfileTypeKeys.Taper, 0.0, 2000.0, Taper(200.0), isMirrored: true),
        });

        RollProfile a = straight.Compose(Geometry, Registry, 101);
        RollProfile b = mirrored.Compose(Geometry, Registry, 101);

        UnitConversion.RadiusMmToDiameterMicrometer(a.RadiusOffsetAtMm(2000.0)).Should().BeApproximately(200.0, 1e-3);
        UnitConversion.RadiusMmToDiameterMicrometer(b.RadiusOffsetAtMm(0.0)).Should().BeApproximately(200.0, 1e-3);
    }

    [Fact]
    public void Segments_can_be_added_removed_and_reordered()
    {
        CompositeRollProfile composite = CompositeRollProfile
            .Single(ProfileTypeKeys.Cylindrical, Geometry, ParameterSet.Empty)
            .Add(RollProfileSegment.Create(99, ProfileTypeKeys.Taper, 0.0, 150.0, Taper(-100.0)))
            .Add(RollProfileSegment.Create(99, ProfileTypeKeys.Taper, 1850.0, 2000.0, Taper(-100.0)));

        composite.Segments.Select(segment => segment.Order).Should().Equal(1, 2, 3);

        CompositeRollProfile moved = composite.MoveUp(2);
        moved.Segments[0].ProfileTypeKey.Should().Be(ProfileTypeKeys.Taper);

        CompositeRollProfile removed = composite.RemoveAt(2);
        removed.Segments.Should().HaveCount(2);
        removed.Segments.Select(segment => segment.Order).Should().Equal(1, 2);
    }

    [Fact]
    public void An_empty_range_is_rejected()
    {
        FluentActions.Invoking(() =>
                RollProfileSegment.Create(1, ProfileTypeKeys.Taper, 150.0, 150.0, Taper(10.0)))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void A_profile_without_segments_is_rejected()
    {
        FluentActions.Invoking(() => CompositeRollProfile.Superimposed(System.Array.Empty<RollProfileSegment>()))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Replacing_a_segment_keeps_its_place_in_the_stack()
    {
        // 编辑器改完参数或区间就走这一条回写：顺序不能动，
        // 否则改一次凸度，端部那段锥度就跑到主辊形前面去了。
        var crown = new CrownProfileType();
        var taper = new TaperProfileType();
        RollGeometry geometry = RollGeometry.FromDiameter(2000.0, 650.0);

        var composite = CompositeRollProfile.Superimposed(new[]
        {
            RollProfileSegment.Create(1, ProfileTypeKeys.Crown, 0.0, 2000.0, crown.Schema.CreateDefaults()),
            RollProfileSegment.Create(2, ProfileTypeKeys.Taper, 1800.0, 2000.0, taper.Schema.CreateDefaults()),
            RollProfileSegment.Create(3, ProfileTypeKeys.Taper, 0.0, 200.0, taper.Schema.CreateDefaults()),
        });

        CompositeRollProfile updated = composite.Replace(
            2,
            RollProfileSegment.Create(
                2,
                ProfileTypeKeys.Taper,
                1700.0,
                2000.0,
                taper.Schema.CreateDefaults()
                    .With(TaperProfileType.TaperDiameterMicrometerKey, ParameterValue.FromNumber(-80.0)),
                isMirrored: true));

        updated.Segments.Select(segment => segment.Order).Should().Equal(1, 2, 3);
        updated.Segments[0].ProfileTypeKey.Should().Be(ProfileTypeKeys.Crown, "主辊形还在第一位");
        updated.Segments[1].FromMm.Should().Be(1700.0);
        updated.Segments[1].IsMirrored.Should().BeTrue();
        updated.Segments[1].Parameters.GetNumber(TaperProfileType.TaperDiameterMicrometerKey).Should().Be(-80.0);
        updated.Segments[2].Should().Be(composite.Segments[2], "没碰的那一段一个字节都不该变");

        // 原来那条不动：领域对象不可变。
        composite.Segments[1].FromMm.Should().Be(1800.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Replacing_a_segment_that_does_not_exist_is_refused(int order)
    {
        var crown = new CrownProfileType();
        RollGeometry geometry = RollGeometry.FromDiameter(2000.0, 650.0);
        CompositeRollProfile composite = CompositeRollProfile.Single(
            ProfileTypeKeys.Crown, geometry, crown.Schema.CreateDefaults());

        composite.Invoking(profile => profile.Replace(
                order,
                RollProfileSegment.Create(order, ProfileTypeKeys.Crown, 0.0, 100.0, crown.Schema.CreateDefaults())))
            .Should().Throw<DomainException>();
    }
}
