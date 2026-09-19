using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

public sealed class RollProfileTypeTests
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(bodyLengthMm: 2000.0, nominalDiameterMm: 650.0);

    private static RollProfileTypeRegistry CreateRegistry() => new(new IRollProfileType[]
    {
        new CylindricalProfileType(),
        new TaperProfileType(),
        new CrownProfileType(),
        new CvcProfileType(),
    });

    [Fact]
    public void Registry_exposes_every_registered_type()
    {
        CreateRegistry().All.Select(type => type.Key)
            .Should().BeEquivalentTo(new[] { "Crown", "Cvc", "Cylindrical", "Taper" });
    }

    [Fact]
    public void Unknown_profile_type_throws_a_domain_exception()
    {
        RollProfileTypeRegistry registry = CreateRegistry();

        registry.Invoking(r => r.Get("Hyperbolic")).Should().Throw<DomainException>();
    }

    [Fact]
    public void Duplicate_registration_is_rejected()
    {
        FluentActions.Invoking(() => new RollProfileTypeRegistry(new IRollProfileType[]
        {
            new CrownProfileType(),
            new CrownProfileType(),
        })).Should().Throw<DomainException>();
    }

    [Fact]
    public void Cylindrical_profile_is_flat()
    {
        RollProfile profile = new CylindricalProfileType()
            .CreateProfile(Geometry, ParameterSet.Empty, sampleCount: 11);

        profile.Points.Should().OnlyContain(point => point.RadiusOffsetMm == 0.0);
        profile.PeakToValleyDiameterMicrometer.Should().Be(0.0);
    }

    [Fact]
    public void Crown_profile_peaks_at_mid_body_with_the_requested_diameter_crown()
    {
        var profileType = new CrownProfileType();
        ParameterSet parameters = profileType.Schema.CreateDefaults()
            .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(120.0));

        RollProfile profile = profileType.CreateProfile(Geometry, parameters, sampleCount: 101);

        profile.RadiusOffsetAtMm(0.0).Should().BeApproximately(0.0, 1e-9);
        profile.RadiusOffsetAtMm(Geometry.BodyLengthMm).Should().BeApproximately(0.0, 1e-9);
        UnitConversion.RadiusMmToDiameterMicrometer(profile.RadiusOffsetAtMm(Geometry.BodyLengthMm / 2.0))
            .Should().BeApproximately(120.0, 1e-6);
    }

    [Fact]
    public void Negative_crown_produces_a_concave_profile()
    {
        var profileType = new CrownProfileType();
        ParameterSet parameters = profileType.Schema.CreateDefaults()
            .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(-80.0));

        RollProfile profile = profileType.CreateProfile(Geometry, parameters, sampleCount: 51);

        profile.MinRadiusOffsetMm.Should().BeLessThan(0.0);
        profile.MaxRadiusOffsetMm.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Taper_profile_is_linear_from_zero_to_the_requested_value()
    {
        var profileType = new TaperProfileType();
        ParameterSet parameters = profileType.Schema.CreateDefaults()
            .With(TaperProfileType.TaperDiameterMicrometerKey, ParameterValue.FromNumber(200.0));

        RollProfile profile = profileType.CreateProfile(Geometry, parameters, sampleCount: 5);

        profile.RadiusOffsetAtMm(0.0).Should().BeApproximately(0.0, 1e-12);
        UnitConversion.RadiusMmToDiameterMicrometer(profile.RadiusOffsetAtMm(Geometry.BodyLengthMm / 2.0))
            .Should().BeApproximately(100.0, 1e-6);
        UnitConversion.RadiusMmToDiameterMicrometer(profile.RadiusOffsetAtMm(Geometry.BodyLengthMm))
            .Should().BeApproximately(200.0, 1e-6);
    }

    [Fact]
    public void Cvc_profile_evaluates_the_cubic_at_both_ends()
    {
        var profileType = new CvcProfileType();
        ParameterSet parameters = profileType.Schema.CreateDefaults()
            .With(CvcProfileType.A1DiameterMicrometerKey, ParameterValue.FromNumber(60.0))
            .With(CvcProfileType.A3DiameterMicrometerKey, ParameterValue.FromNumber(20.0));

        RollProfile profile = profileType.CreateProfile(Geometry, parameters, sampleCount: 101);

        // u = -1 与 u = +1 处分别为 -(a1 + a3) 与 +(a1 + a3)
        UnitConversion.RadiusMmToDiameterMicrometer(profile.RadiusOffsetAtMm(0.0))
            .Should().BeApproximately(-80.0, 1e-6);
        UnitConversion.RadiusMmToDiameterMicrometer(profile.RadiusOffsetAtMm(Geometry.BodyLengthMm))
            .Should().BeApproximately(80.0, 1e-6);
    }

    [Fact]
    public void Profile_interpolates_between_samples()
    {
        var profileType = new TaperProfileType();
        ParameterSet parameters = profileType.Schema.CreateDefaults()
            .With(TaperProfileType.TaperDiameterMicrometerKey, ParameterValue.FromNumber(100.0));

        RollProfile profile = profileType.CreateProfile(Geometry, parameters, sampleCount: 3);

        UnitConversion.RadiusMmToDiameterMicrometer(profile.RadiusOffsetAtMm(Geometry.BodyLengthMm * 0.25))
            .Should().BeApproximately(25.0, 1e-6);
    }

    [Fact]
    public void Profiles_can_be_added_pointwise()
    {
        RollProfile flat = new CylindricalProfileType().CreateProfile(Geometry, ParameterSet.Empty, 11);
        var crownType = new CrownProfileType();
        RollProfile crown = crownType.CreateProfile(
            Geometry,
            crownType.Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(100.0)),
            11);

        RollProfile sum = flat.Add(crown);

        sum.PeakToValleyDiameterMicrometer.Should().BeApproximately(crown.PeakToValleyDiameterMicrometer, 1e-9);
    }

    [Fact]
    public void Geometry_rejects_non_positive_dimensions()
    {
        FluentActions.Invoking(() => RollGeometry.Create(0.0, 100.0)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => RollGeometry.Create(100.0, -1.0)).Should().Throw<DomainException>();
    }
}
