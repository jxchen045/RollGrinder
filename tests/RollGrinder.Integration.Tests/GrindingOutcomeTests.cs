using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Data.Model;
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 记录页"磨削结果"那 12 项。
///
/// 全部是算出来的，不另存一份。每一项算不出来就是 null——
/// "没量过"与"量出来是 0"是两回事，填一个 0 会让人以为这支辊量过了。
/// </summary>
public sealed class GrindingOutcomeTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    private static GrindingRecord Record(double? wheelDiameterMm = null, bool finished = true) =>
        new("G-1", "J-1", Started, finished ? Started.AddMinutes(174.0) : null, JobState.Completed, null)
        {
            WheelDiameterMm = wheelDiameterMm,
        };

    /// <summary>一条沿辊身的实测：两端与中间各给一个半径。</summary>
    private static MeasurementRecord Measured(
        MeasurementStage stage, double headRadiusMm, double middleRadiusMm, double tailRadiusMm) =>
        new("m-" + stage, "J-1", Started, "gauge", new MeasuredProfile(new[]
        {
            new MeasurementPoint(0.0, headRadiusMm),
            new MeasurementPoint(1000.0, middleRadiusMm),
            new MeasurementPoint(2000.0, tailRadiusMm),
        }))
        {
            Stage = stage,
        };

    private static RollProfile FlatTarget() => new(new[]
    {
        new ProfilePoint(0.0, 0.0),
        new ProfilePoint(1000.0, 0.0),
        new ProfilePoint(2000.0, 0.0),
    });

    [Fact]
    public void Nothing_measured_means_nothing_reported()
    {
        GrindingOutcome outcome = GrindingOutcome.Create(
            Record(), Geometry, null, null, null, FlatTarget(), 0);

        outcome.PreGrindDiameterHeadMm.Should().BeNull();
        outcome.PostGrindDiameterHeadMm.Should().BeNull();
        outcome.TaperMm.Should().BeNull();
        outcome.RoundnessMicrometer.Should().BeNull();
        outcome.ActualCrownMm.Should().BeNull();
        outcome.WheelDiameterMm.Should().BeNull();
    }

    [Fact]
    public void The_two_ends_come_from_the_two_ends_of_the_measurement()
    {
        // 辊身坐标 0 在操作侧（头架侧），向传动侧（尾座侧）增大。
        GrindingOutcome outcome = GrindingOutcome.Create(
            Record(),
            Geometry,
            Measured(MeasurementStage.PreGrind, 404.656, 404.660, 404.643),
            Measured(MeasurementStage.PostGrind, 404.302, 404.310, 404.299),
            null,
            FlatTarget(),
            0);

        // 半径量 → 直径量。
        outcome.PreGrindDiameterHeadMm.Should().BeApproximately(809.312, 1e-9);
        outcome.PreGrindDiameterTailMm.Should().BeApproximately(809.286, 1e-9);
        outcome.PostGrindDiameterHeadMm.Should().BeApproximately(808.604, 1e-9);
        outcome.PostGrindDiameterTailMm.Should().BeApproximately(808.598, 1e-9);
    }

    [Fact]
    public void The_taper_is_the_post_grind_difference_not_the_incoming_one()
    {
        // 磨前的锥度是来料的事，不是这一次磨出来的结果。
        GrindingOutcome outcome = GrindingOutcome.Create(
            Record(),
            Geometry,
            Measured(MeasurementStage.PreGrind, 405.0, 405.0, 404.0),
            Measured(MeasurementStage.PostGrind, 404.302, 404.310, 404.299),
            null,
            FlatTarget(),
            0);

        outcome.TaperMm.Should().BeApproximately(0.006, 1e-9);
    }

    [Fact]
    public void The_crown_is_how_much_thicker_the_middle_is()
    {
        // 中间半径比两端平均高 0.008 ⇒ 直径量凸度 0.016。
        GrindingOutcome outcome = GrindingOutcome.Create(
            Record(),
            Geometry,
            null,
            Measured(MeasurementStage.PostGrind, 325.000, 325.008, 325.000),
            null,
            FlatTarget(),
            0);

        outcome.ActualCrownMm.Should().BeApproximately(0.016, 1e-9);
    }

    [Fact]
    public void Roundness_and_concentricity_report_the_worst_section_not_the_average()
    {
        // 一支辊合不合格看最差处，平均会把一个坏截面摊没了。
        var roundness = new RoundnessMeasurement("r-1", "J-1", Started, "trace", new[]
        {
            new RoundnessPoint(0.0, 2.0, 12.0),
            new RoundnessPoint(1000.0, 3.3, 18.1),
            new RoundnessPoint(2000.0, 1.5, 9.0),
        });

        GrindingOutcome outcome = GrindingOutcome.Create(
            Record(), Geometry, null, null, roundness, FlatTarget(), 0);

        outcome.RoundnessMicrometer.Should().Be(3.3);
        outcome.ConcentricityMicrometer.Should().Be(18.1);
    }

    [Fact]
    public void The_profile_rms_is_measured_against_the_target_not_against_zero()
    {
        // 实测比目标半径大 5 µm（半径量）⇒ 直径量 10 µm，三点都一样，RMS 就是 10。
        GrindingOutcome outcome = GrindingOutcome.Create(
            Record(),
            Geometry,
            null,
            Measured(MeasurementStage.PostGrind, 325.005, 325.005, 325.005),
            null,
            FlatTarget(),
            0);

        outcome.ProfileRmsMicrometer.Should().BeApproximately(10.0, 1e-6);
    }

    [Fact]
    public void A_job_that_cannot_be_traced_still_reports_what_it_can()
    {
        // 作业没了，辊形误差算不出来；直径与锥度照样报得出来。
        GrindingOutcome outcome = GrindingOutcome.Create(
            Record(wheelDiameterMm: 889.76),
            job: null,
            null,
            Measured(MeasurementStage.PostGrind, 404.302, 404.310, 404.299),
            null,
            targetProfile: null,
            3);

        outcome.ProfileRmsMicrometer.Should().BeNull();
        outcome.TaperMm.Should().NotBeNull();
        outcome.WheelDiameterMm.Should().Be(889.76);
        outcome.CompensationIterations.Should().Be(3);
    }

    [Fact]
    public void An_unfinished_record_has_no_duration_yet()
    {
        GrindingOutcome outcome = GrindingOutcome.Create(
            Record(finished: false), Geometry, null, null, null, FlatTarget(), 0);

        outcome.Duration.Should().BeNull();
    }

    [Fact]
    public void A_finished_record_reports_how_long_it_took()
    {
        GrindingOutcome outcome = GrindingOutcome.Create(
            Record(), Geometry, null, null, null, FlatTarget(), 0);

        outcome.Duration.Should().Be(TimeSpan.FromMinutes(174.0));
    }
}
