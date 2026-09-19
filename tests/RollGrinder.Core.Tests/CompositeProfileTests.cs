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
        var composite = new CompositeRollProfile(new[]
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
        var composite = new CompositeRollProfile(new[]
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
        var straight = new CompositeRollProfile(new[]
        {
            RollProfileSegment.Create(1, ProfileTypeKeys.Taper, 0.0, 2000.0, Taper(200.0)),
        });
        var mirrored = new CompositeRollProfile(new[]
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
        FluentActions.Invoking(() => new CompositeRollProfile(System.Array.Empty<RollProfileSegment>()))
            .Should().Throw<DomainException>();
    }
}
