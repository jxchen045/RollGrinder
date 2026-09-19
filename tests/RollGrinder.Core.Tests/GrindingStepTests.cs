using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

public sealed class GrindingStepTests
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    private static GrindingStepTypeRegistry CreateRegistry() => new(new IGrindingStepType[]
    {
        new RoughGrindingStepType(),
        new FinishGrindingStepType(),
        new SparkOutStepType(),
        new MeasureStepType(),
    });

    [Fact]
    public void Registry_exposes_every_registered_step_type()
    {
        CreateRegistry().All.Select(type => type.Key)
            .Should().BeEquivalentTo(new[] { "Finish", "Measure", "Rough", "SparkOut" });
    }

    [Fact]
    public void Rough_step_splits_the_stock_into_whole_passes()
    {
        var stepType = new RoughGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(500.0))
            .With(StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(60.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.PassCount.Should().Be(9, "500 µm 直径量按每刀 60 µm 需要 9 刀");
        plan.TotalStockDiameterMicrometer.Should().BeApproximately(500.0, 1e-6);
        UnitConversion.RadiusMmToDiameterMicrometer(plan.InfeedPerPassRadiusMm)
            .Should().BeLessThanOrEqualTo(60.0, "最后一刀不得超过设定切深");
    }

    [Fact]
    public void Finish_step_asks_for_a_measurement()
    {
        var stepType = new FinishGrindingStepType();

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, stepType.Schema.CreateDefaults());

        plan.RequiresMeasurement.Should().BeTrue();
        plan.SparkOutPassCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Spark_out_step_does_not_feed_in()
    {
        var stepType = new SparkOutStepType();

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, stepType.Schema.CreateDefaults());

        plan.InfeedPerPassRadiusMm.Should().Be(0.0);
        plan.TotalInfeedRadiusMm.Should().Be(0.0);
    }

    [Fact]
    public void Measure_step_runs_one_pass_without_the_wheel()
    {
        var stepType = new MeasureStepType();

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, stepType.Schema.CreateDefaults());

        plan.PassCount.Should().Be(1);
        plan.WheelSpeedRpm.Should().Be(0.0);
        plan.RequiresMeasurement.Should().BeTrue();
    }

    [Fact]
    public void Zero_stock_still_yields_a_single_pass()
    {
        var stepType = new RoughGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(0.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.PassCount.Should().Be(1);
        plan.InfeedPerPassRadiusMm.Should().Be(0.0);
    }
}

public sealed class GrindingJobValidatorTests
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    private static readonly MachineCapability Capability = new(
        MaxInfeedPerPassRadiusMm: 0.05,
        MaxFeedMmPerMin: 3000.0,
        MaxWorkpieceSpeedRpm: 120.0,
        MaxWheelSpeedRpm: 1200.0,
        MinBodyLengthMm: 300.0,
        MaxBodyLengthMm: 5200.0,
        MinRadiusMm: 75.0,
        MaxRadiusMm: 650.0);

    private static GrindingJobValidator CreateValidator() => new(
        new RollProfileTypeRegistry(new IRollProfileType[] { new CylindricalProfileType(), new CrownProfileType() }),
        new GrindingStepTypeRegistry(new IGrindingStepType[] { new RoughGrindingStepType(), new SparkOutStepType() }));

    private static GrindingJob CreateJob(ParameterSet? roughOverrides = null)
    {
        var roughType = new RoughGrindingStepType();
        ParameterSet rough = roughOverrides is null
            ? roughType.Schema.CreateDefaults()
            : roughType.Schema.CreateDefaults().Merge(roughOverrides);

        return GrindingJob.Create(
            "J-1",
            "R-1",
            Geometry,
            ProfileTypeKeys.Cylindrical,
            ParameterSet.Empty,
            new[]
            {
                new GrindingJobStep(1, StepTypeKeys.Rough, rough),
                new GrindingJobStep(2, StepTypeKeys.SparkOut, new SparkOutStepType().Schema.CreateDefaults()),
            });
    }

    [Fact]
    public void A_default_job_is_valid()
    {
        CreateValidator().Validate(CreateJob(), Capability).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Infeed_beyond_the_machine_limit_is_reported()
    {
        // 每刀 200 µm 直径量 = 0.1 mm 半径量，超过机床 0.05 mm。
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(200.0)),
        });

        ParameterValidationResult result = CreateValidator().Validate(CreateJob(overrides), Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepParameterKeys.InfeedPerPassDiameterMicrometer
            && violation.Kind == ParameterViolationKind.ExceedsMachineLimit);
    }

    [Fact]
    public void Roll_longer_than_the_machine_is_reported()
    {
        GrindingJob job = GrindingJob.Create(
            "J-2",
            "R-2",
            RollGeometry.FromDiameter(9000.0, 650.0),
            ProfileTypeKeys.Cylindrical,
            ParameterSet.Empty,
            new[] { new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()) });

        CreateValidator().Validate(job, Capability).Violations.Should().Contain(violation =>
            violation.Kind == ParameterViolationKind.ExceedsMachineLimit);
    }

    [Fact]
    public void Step_order_gaps_are_rejected_when_the_job_is_built()
    {
        FluentActions.Invoking(() => GrindingJob.Create(
            "J-3",
            "R-3",
            Geometry,
            ProfileTypeKeys.Cylindrical,
            ParameterSet.Empty,
            new[]
            {
                new GrindingJobStep(1, StepTypeKeys.Rough, ParameterSet.Empty),
                new GrindingJobStep(3, StepTypeKeys.SparkOut, ParameterSet.Empty),
            })).Should().Throw<DomainException>();
    }

    [Fact]
    public void A_job_without_steps_is_rejected()
    {
        FluentActions.Invoking(() => GrindingJob.Create(
            "J-4", "R-4", Geometry, ProfileTypeKeys.Cylindrical, ParameterSet.Empty,
            System.Array.Empty<GrindingJobStep>())).Should().Throw<DomainException>();
    }
}
