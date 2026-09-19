using FluentAssertions;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

public sealed class UnitConversionTests
{
    [Fact]
    public void Radius_and_diameter_round_trip()
    {
        UnitConversion.RadiusMmToDiameterMm(325.0).Should().Be(650.0);
        UnitConversion.DiameterMmToRadiusMm(650.0).Should().Be(325.0);
    }

    [Fact]
    public void Millimetre_and_micrometre_round_trip()
    {
        UnitConversion.MmToMicrometer(0.025).Should().BeApproximately(25.0, 1e-9);
        UnitConversion.MicrometerToMm(25.0).Should().BeApproximately(0.025, 1e-12);
    }

    [Fact]
    public void Ui_crown_of_hundred_micrometre_diameter_is_fifty_micrometre_radius()
    {
        double radiusMm = UnitConversion.DiameterMicrometerToRadiusMm(100.0);

        radiusMm.Should().BeApproximately(0.05, 1e-12);
        UnitConversion.RadiusMmToDiameterMicrometer(radiusMm).Should().BeApproximately(100.0, 1e-9);
    }
}
