using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

public sealed class CompensationTests
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    private static readonly CompensationSettings Settings =
        CompensationSettings.Create(gain: 1.0, smoothingPoints: 1, maxCorrectionRadiusMm: 0.02);

    private static RollProfile FlatTarget() =>
        new CylindricalProfileType().CreateProfile(Geometry, ParameterSet.Empty, 11);

    private static MeasuredProfile MeasuredWithOffset(double radiusOffsetMm) =>
        new(Enumerable.Range(0, 11).Select(i => new MeasurementPoint(
            Geometry.BodyLengthMm * i / 10.0,
            Geometry.NominalRadiusMm + radiusOffsetMm)));

    [Fact]
    public void Deviation_is_measured_minus_target()
    {
        RollProfile deviation = CompensationCalculator.ComputeDeviation(
            MeasuredWithOffset(0.01), FlatTarget(), Geometry);

        deviation.Points.Should().OnlyContain(point => Math.Abs(point.RadiusOffsetMm - 0.01) < 1e-9);
    }

    [Fact]
    public void A_perfect_roll_produces_no_deviation()
    {
        RollProfile deviation = CompensationCalculator.ComputeDeviation(
            MeasuredWithOffset(0.0), FlatTarget(), Geometry);

        ProfileQuality quality = ProfileQuality.FromDeviation(deviation);
        quality.WorstDeviationDiameterMicrometer.Should().BeApproximately(0.0, 1e-9);
        quality.IsWithinToleranceDiameterMicrometer(5.0).Should().BeTrue();
    }

    [Fact]
    public void Compensation_pushes_against_the_deviation()
    {
        RollProfile deviation = CompensationCalculator.ComputeDeviation(
            MeasuredWithOffset(0.01), FlatTarget(), Geometry);

        RollProfile compensation = CompensationCalculator.ComputeCompensation(null, deviation, Settings);

        compensation.Points.Should().OnlyContain(point => point.RadiusOffsetMm < 0.0,
            "磨少了就要往里再走一点");
        compensation.Points[0].RadiusOffsetMm.Should().BeApproximately(-0.01, 1e-9);
    }

    [Fact]
    public void Gain_scales_how_much_of_the_deviation_is_absorbed()
    {
        RollProfile deviation = CompensationCalculator.ComputeDeviation(
            MeasuredWithOffset(0.01), FlatTarget(), Geometry);

        RollProfile compensation = CompensationCalculator.ComputeCompensation(
            null, deviation, CompensationSettings.Create(0.5, 1, 0.02));

        compensation.Points[0].RadiusOffsetMm.Should().BeApproximately(-0.005, 1e-9);
    }

    [Fact]
    public void Compensation_accumulates_on_top_of_the_previous_one()
    {
        RollProfile deviation = CompensationCalculator.ComputeDeviation(
            MeasuredWithOffset(0.004), FlatTarget(), Geometry);
        RollProfile first = CompensationCalculator.ComputeCompensation(null, deviation, Settings);

        RollProfile second = CompensationCalculator.ComputeCompensation(first, deviation, Settings);

        second.Points[0].RadiusOffsetMm.Should().BeApproximately(-0.008, 1e-9);
    }

    [Fact]
    public void Corrections_are_clamped_to_the_machine_limit()
    {
        RollProfile deviation = CompensationCalculator.ComputeDeviation(
            MeasuredWithOffset(0.5), FlatTarget(), Geometry);

        RollProfile compensation = CompensationCalculator.ComputeCompensation(null, deviation, Settings);

        compensation.Points.Should().OnlyContain(point => point.RadiusOffsetMm >= -0.02 - 1e-12);
    }

    [Fact]
    public void Smoothing_damps_a_single_noisy_point()
    {
        RollProfile target = FlatTarget();
        var noisy = new MeasuredProfile(Enumerable.Range(0, 11).Select(i => new MeasurementPoint(
            Geometry.BodyLengthMm * i / 10.0,
            Geometry.NominalRadiusMm + (i == 5 ? 0.01 : 0.0))));

        RollProfile deviation = CompensationCalculator.ComputeDeviation(noisy, target, Geometry);
        RollProfile smoothed = CompensationCalculator.ComputeCompensation(
            null, deviation, CompensationSettings.Create(1.0, 5, 0.02));
        RollProfile unsmoothed = CompensationCalculator.ComputeCompensation(null, deviation, Settings);

        Math.Abs(smoothed.Points[5].RadiusOffsetMm)
            .Should().BeLessThan(Math.Abs(unsmoothed.Points[5].RadiusOffsetMm));
    }

    [Fact]
    public void Quality_reports_the_peak_to_valley_in_diameter_micrometres()
    {
        RollProfile target = FlatTarget();
        var measured = new MeasuredProfile(Enumerable.Range(0, 11).Select(i => new MeasurementPoint(
            Geometry.BodyLengthMm * i / 10.0,
            Geometry.NominalRadiusMm + (i % 2 == 0 ? 0.001 : -0.001))));

        ProfileQuality quality = ProfileQuality.FromDeviation(
            CompensationCalculator.ComputeDeviation(measured, target, Geometry));

        quality.PeakToValleyDiameterMicrometer.Should().BeApproximately(4.0, 1e-6);
        quality.IsWithinToleranceDiameterMicrometer(1.0).Should().BeFalse();
    }

    [Fact]
    public void A_crowned_target_is_compared_against_its_own_shape()
    {
        var crownType = new CrownProfileType();
        ParameterSet parameters = crownType.Schema.CreateDefaults()
            .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(100.0));
        RollProfile target = crownType.CreateProfile(Geometry, parameters, 11);

        // 实测正好是目标形状：偏差应为零，而不是被当成 50 µm 半径量的凸起。
        var measured = new MeasuredProfile(target.Points.Select(point =>
            new MeasurementPoint(point.BodyPositionMm, Geometry.NominalRadiusMm + point.RadiusOffsetMm)));

        RollProfile deviation = CompensationCalculator.ComputeDeviation(measured, target, Geometry);

        deviation.Points.Should().OnlyContain(point => Math.Abs(point.RadiusOffsetMm) < 1e-9);
    }

    [Fact]
    public void Invalid_settings_are_rejected()
    {
        FluentActions.Invoking(() => CompensationSettings.Create(0.0, 1, 0.02)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => CompensationSettings.Create(0.5, 4, 0.02)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => CompensationSettings.Create(0.5, 3, 0.0)).Should().Throw<DomainException>();
    }
}
