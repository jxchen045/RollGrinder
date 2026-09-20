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
    public void Rough_grinding_runs_both_infeed_components_at_once()
    {
        // MGK84160 操作说明书的磨削实例，粗磨一列：连续 0.05 mm/min 与周期 0.005 mm
        // 同时给值。两者是可叠加的分量，不是二选一。
        var stepType = new RoughGrindingStepType();

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, stepType.Schema.CreateDefaults());

        plan.FeedMode.Should().Be(StepFeedMode.Combined, "粗磨两路分量同时用");
        plan.ContinuousInfeedRadiusMmPerMin.Should().BeGreaterThan(0.0);
        plan.InfeedPerPassRadiusMm.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void Finish_grinding_keeps_only_the_per_reversal_component()
    {
        var stepType = new FinishGrindingStepType();

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, stepType.Schema.CreateDefaults());

        plan.FeedMode.Should().Be(StepFeedMode.PerReversal, "精磨要每道次等深，连续分量置 0");
        plan.InfeedPerPassRadiusMm.Should().BeGreaterThan(0.0);
        plan.ContinuousInfeedRadiusMmPerMin.Should().Be(0.0);
    }

    [Fact]
    public void Both_infeed_components_reach_the_plan_untouched()
    {
        // 这是本次纠正的核心：展开时谁都不许把另一路压成 0。
        var stepType = new SemiFinishGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(6.0))
            .With(StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(4.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.ContinuousInfeedRadiusMmPerMin.Should().BeApproximately(0.003, 1e-9);
        plan.InfeedPerPassRadiusMm.Should().BeApproximately(0.002, 1e-9);
        plan.FeedMode.Should().Be(StepFeedMode.Combined);
    }

    [Theory]
    [InlineData(0.0, 0.0, StepFeedMode.None)]
    [InlineData(5.0, 0.0, StepFeedMode.Continuous)]
    [InlineData(0.0, 5.0, StepFeedMode.PerReversal)]
    [InlineData(5.0, 5.0, StepFeedMode.Combined)]
    public void Feed_mode_is_derived_from_the_two_components(
        double continuousMicrometerPerMin, double perPassMicrometer, StepFeedMode expected)
    {
        var stepType = new SemiFinishGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(
                StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin,
                ParameterValue.FromNumber(continuousMicrometerPerMin))
            .With(
                StepParameterKeys.InfeedPerPassDiameterMicrometer,
                ParameterValue.FromNumber(perPassMicrometer));

        stepType.CreatePlan(Geometry, parameters).FeedMode.Should().Be(expected);
    }

    [Fact]
    public void There_is_no_infeed_mode_switch_to_get_wrong()
    {
        // 进给方式不再是一个可设的参数——它是从两个进给量推出来的。
        foreach (IGrindingStepType stepType in new IGrindingStepType[]
                 {
                     new ShortStrokeStepType(), new RoughGrindingStepType(),
                     new SemiFinishGrindingStepType(), new FinishGrindingStepType(), new PolishStepType(),
                 })
        {
            stepType.Schema.Descriptors.Select(descriptor => descriptor.Key)
                .Should().NotContain("feedMode", stepType.Key);
        }
    }

    [Theory]
    [InlineData(StepTypeKeys.Rough, 40.0, 35.0, 2300.0, 50.0, 5.0, 10.0)]
    [InlineData(StepTypeKeys.SemiFinish, 40.0, 38.0, 1200.0, 2.0, 2.0, 4.0)]
    [InlineData(StepTypeKeys.Finish, 35.0, 40.0, 800.0, 0.0, 2.0, 6.0)]
    public void Defaults_follow_the_manuals_worked_example(
        string stepTypeKey,
        double wheelSurfaceSpeedMPerSec,
        double workpieceSpeedRpm,
        double feedMmPerMin,
        double continuousMicrometerPerMin,
        double perPassMicrometer,
        double passCount)
    {
        // 取值对照 MGK84160 操作说明书的"磨削实例"表：
        // 粗磨 / 半粗磨 / 中磨 三列分别对应 Rough / SemiFinish / Finish。
        // 说明书写 mm 的地方这里是 µm 直径量，是同一个量（架构约束 ⑨）。
        IGrindingStepType stepType = stepTypeKey switch
        {
            StepTypeKeys.Rough => new RoughGrindingStepType(),
            StepTypeKeys.SemiFinish => new SemiFinishGrindingStepType(),
            _ => new FinishGrindingStepType(),
        };
        ParameterSet defaults = stepType.Schema.CreateDefaults();

        defaults.GetNumber(StepParameterKeys.WheelSurfaceSpeedMPerSec).Should().Be(wheelSurfaceSpeedMPerSec);
        defaults.GetNumber(StepParameterKeys.WorkpieceSpeedRpm).Should().Be(workpieceSpeedRpm);
        defaults.GetNumber(StepParameterKeys.FeedMmPerMin).Should().Be(feedMmPerMin);
        defaults.GetNumber(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin)
            .Should().Be(continuousMicrometerPerMin);
        defaults.GetNumber(StepParameterKeys.InfeedPerPassDiameterMicrometer).Should().Be(perPassMicrometer);
        defaults.GetNumber(StepParameterKeys.PassCount).Should().Be(passCount);
        defaults.GetChoice(StepParameterKeys.SpeedVariationTarget)
            .Should().Be(SpeedVariationChoices.Workpiece, "变速默认作用在轧辊（头架）转速上");
    }

    [Fact]
    public void Speed_variation_targets_the_workpiece_by_name()
    {
        var stepType = new SemiFinishGrindingStepType();

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, stepType.Schema.CreateDefaults());

        plan.SpeedVariation.Target.Should().Be(SpeedVariationTarget.Workpiece);
        plan.SpeedVariation.AffectsWorkpiece.Should().BeTrue();
        plan.SpeedVariation.AffectsWheel.Should().BeFalse();
        plan.SpeedVariation.AmplitudePercent.Should().Be(8.0);
        plan.SpeedVariation.PeriodSeconds.Should().BeGreaterThan(0.0, "只给幅度不给周期，变速下发不了");
        plan.SpeedVariation.PeakOf(100.0).Should().BeApproximately(108.0, 1e-9);
        plan.SpeedVariation.TroughOf(100.0).Should().BeApproximately(92.0, 1e-9);
    }

    [Fact]
    public void Turning_speed_variation_off_clears_amplitude_and_period()
    {
        var stepType = new SemiFinishGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.SpeedVariationTarget, ParameterValue.FromChoice(SpeedVariationChoices.Off));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.SpeedVariation.Should().Be(SpeedVariation.Off);
    }

    [Fact]
    public void Measurement_and_marker_steps_never_feed_or_vary_speed()
    {
        IGrindingStepType[] quietTypes =
        {
            new StartStepType(), new EndStepType(), new MeasureStepType(),
            new EddyCurrentStepType(), new SparkOutStepType(), new WheelDressStepType(),
            new ChamferStepType(),
        };

        foreach (IGrindingStepType stepType in quietTypes)
        {
            GrindingStepPlan plan = stepType.CreatePlan(Geometry, stepType.Schema.CreateDefaults());

            plan.FeedMode.Should().Be(StepFeedMode.None, $"{stepType.Key} 不做径向进给");
            plan.IsCutting.Should().BeFalse();
            plan.SpeedVariation.Target.Should().Be(SpeedVariationTarget.Off, $"{stepType.Key} 不变速");
        }
    }

    [Fact]
    public void Eddy_current_derives_the_carriage_speed_from_pitch_and_workpiece_speed()
    {
        var stepType = new EddyCurrentStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.ScanPitchMm, ParameterValue.FromNumber(4.0))
            .With(StepParameterKeys.WorkpieceSpeedRpm, ParameterValue.FromNumber(25.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.FeedMmPerMin.Should().BeApproximately(100.0, 1e-9, "拖板 = 螺距 × 转速");
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
    public void Pass_count_is_what_the_operator_set_not_something_derived()
    {
        // 道次是操作员按工艺定的预算，不再由"余量 ÷ 每刀"倒推——
        // 两者对不上时由校验报出来，而不是悄悄改掉操作员填的数。
        var stepType = new FinishGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.PassCount, ParameterValue.FromNumber(7.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.PassCount.Should().Be(7);
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

    /// <summary>装齐了所有选件、所有轴、也装了测头的一台机床。</summary>
    private static MachineCapability FullyEquipped => Capability with
    {
        InstalledOptions = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            MachineOptionKeys.WheelDresser,
            MachineOptionKeys.EddyCurrentTester,
            MachineOptionKeys.DualProbeMeasurement,
            MachineOptionKeys.U1Leveling,
            MachineOptionKeys.ContactDetection,
        },
        AvailableAxisRoles = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            MachineAxisRoleNames.CrownAdjust,
        },
        AvailableMeasurements = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            MeasurementQuantities.Diameter,
        },
    };

    /// <summary>
    /// 八个程序步骤开关全关。绝大多数用例不关心它们，关掉就不会互相干扰——
    /// 程序步骤自己的门禁另有专门的用例覆盖。
    /// </summary>
    private static ParameterSet AllProgramOptionsOff => new(
        ProgramOptionCatalog.All.Select(option =>
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                option.Key, ParameterValue.FromBoolean(false))));

    private static GrindingJobValidator CreateFullValidator() => new(
        new RollProfileTypeRegistry(new IRollProfileType[] { new CylindricalProfileType(), new CrownProfileType(), new TaperProfileType() }),
        new GrindingStepTypeRegistry(new IGrindingStepType[]
        {
            new RoughGrindingStepType(), new SparkOutStepType(), new FinishGrindingStepType(),
            new WheelDressStepType(), new EddyCurrentStepType(),
        }));

    private static GrindingJob JobWith(params GrindingJobStep[] steps) => GrindingJob.Create(
        "J-cap",
        "R-1",
        Geometry,
        ProfileTypeKeys.Cylindrical,
        ParameterSet.Empty,
        steps,
        AllProgramOptionsOff);

    private static GrindingJobValidator CreateValidator() => new(
        new RollProfileTypeRegistry(new IRollProfileType[] { new CylindricalProfileType(), new CrownProfileType(), new TaperProfileType() }),
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
            },
            AllProgramOptionsOff);
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
        // 粗磨默认走连续进给，这里显式切到周期进给，才轮得到"单刀切深"限幅。
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(0.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(200.0)),
        });

        ParameterValidationResult result = CreateValidator().Validate(CreateJob(overrides), Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepParameterKeys.InfeedPerPassDiameterMicrometer
            && violation.Kind == ParameterViolationKind.ExceedsMachineLimit);
    }

    [Fact]
    public void Pass_count_that_does_not_add_up_to_the_stock_target_is_reported()
    {
        // 只用周期分量时（连续置 0）：10 µm × 10 道 = 100 µm，却把磨削量填成 300 µm，
        // 机床磨到 100 就停，操作员以为磨了 300。这种对不上必须报出来。
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(0.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(10.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.PassCount, ParameterValue.FromNumber(10.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(300.0)),
        });

        ParameterValidationResult result = CreateValidator().Validate(CreateJob(overrides), Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepParameterKeys.StockDiameterMicrometer
            && violation.Kind == ParameterViolationKind.Inconsistent);
    }

    [Fact]
    public void Consistent_pass_count_and_stock_target_pass()
    {
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(0.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(10.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.PassCount, ParameterValue.FromNumber(10.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(100.0)),
        });

        CreateValidator().Validate(CreateJob(overrides), Capability).Violations
            .Should().NotContain(violation => violation.Kind == ParameterViolationKind.Inconsistent);
    }

    [Fact]
    public void Stock_target_is_not_reconciled_once_a_continuous_component_is_added()
    {
        // 叠了连续分量之后，实际去除量还取决于行程时间，"道次 × 每道次"不再等于磨削量，
        // 两个终止条件谁先到先停。这时候再报 Inconsistent 就是误报——说明书自己的
        // 粗磨参数（连续 0.05 + 周期 0.005）每根辊子都会中招。
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(50.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(10.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.PassCount, ParameterValue.FromNumber(10.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(300.0)),
        });

        CreateValidator().Validate(CreateJob(overrides), Capability).Violations
            .Should().NotContain(violation => violation.Kind == ParameterViolationKind.Inconsistent);
    }

    [Fact]
    public void A_continuous_component_beyond_the_configured_limit_is_reported()
    {
        // 机床侧那条软件保护：连续进给超限该参数就不生效。上位机直接拦在下发之前。
        MachineCapability bounded = Capability with { MaxContinuousInfeedRadiusMmPerMin = 0.01 };
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(100.0)),
        });

        ParameterValidationResult result = CreateValidator().Validate(CreateJob(overrides), bounded);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin
            && violation.Kind == ParameterViolationKind.ExceedsMachineLimit);
    }

    [Fact]
    public void Without_a_configured_continuous_limit_the_check_is_skipped()
    {
        // machine.json 没给这项就不校验——不替机床猜一个数字出来。
        Capability.MaxContinuousInfeedRadiusMmPerMin.Should().BeNull();
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(100.0)),
        });

        CreateValidator().Validate(CreateJob(overrides), Capability).Violations
            .Should().NotContain(violation =>
                violation.ParameterKey == StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin);
    }

    [Fact]
    public void Speed_variation_is_checked_at_its_peak_not_at_the_setpoint()
    {
        // 头架上限 120 r/min。设定 115 本身不超，但 ±10% 的峰值是 126.5——超了。
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.WorkpieceSpeedRpm, ParameterValue.FromNumber(115.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.SpeedVariationTarget, ParameterValue.FromChoice(SpeedVariationChoices.Workpiece)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.SpeedVariationPercent, ParameterValue.FromNumber(10.0)),
        });

        ParameterValidationResult result = CreateValidator().Validate(CreateJob(overrides), Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepParameterKeys.WorkpieceSpeedRpm
            && violation.Kind == ParameterViolationKind.ExceedsMachineLimit);
    }

    [Fact]
    public void The_same_speed_without_variation_is_accepted()
    {
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.WorkpieceSpeedRpm, ParameterValue.FromNumber(115.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.SpeedVariationTarget, ParameterValue.FromChoice(SpeedVariationChoices.Off)),
        });

        CreateValidator().Validate(CreateJob(overrides), Capability).Violations
            .Should().NotContain(violation =>
                violation.ParameterKey == StepParameterKeys.WorkpieceSpeedRpm);
    }

    [Fact]
    public void Wheel_surface_speed_beyond_the_configured_window_is_reported()
    {
        MachineCapability bounded = Capability with
        {
            MinWheelSurfaceSpeedMPerSec = 18.0,
            MaxWheelSurfaceSpeedMPerSec = 33.0,
        };

        // 线速度 32 本身在窗口内，但砂轮变速 ±8% 的峰值是 34.56——超了。
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.WheelSurfaceSpeedMPerSec, ParameterValue.FromNumber(32.0)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.SpeedVariationTarget, ParameterValue.FromChoice(SpeedVariationChoices.Wheel)),
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.SpeedVariationPercent, ParameterValue.FromNumber(8.0)),
        });

        ParameterValidationResult result = CreateValidator().Validate(CreateJob(overrides), bounded);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepParameterKeys.WheelSurfaceSpeedMPerSec
            && violation.Kind == ParameterViolationKind.ExceedsMachineLimit);
    }

    [Fact]
    public void Wheel_surface_speed_is_not_checked_when_the_machine_file_says_nothing()
    {
        // machine.json 没给上下限时跳过这项校验——不替机床猜一个数字出来。
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.WheelSurfaceSpeedMPerSec, ParameterValue.FromNumber(75.0)),
        });

        CreateValidator().Validate(CreateJob(overrides), Capability).Violations
            .Should().NotContain(violation =>
                violation.ParameterKey == StepParameterKeys.WheelSurfaceSpeedMPerSec);
    }

    [Fact]
    public void A_profile_segment_reaching_past_the_roll_body_is_reported_with_its_segment_number()
    {
        // 叠了四段的时候，只说"区间超了"操作员不知道该改哪一段，所以键上带段号。
        var crown = new CrownProfileType();
        var taper = new TaperProfileType();
        RollGeometry geometry = RollGeometry.FromDiameter(2000.0, 650.0);

        GrindingJob job = GrindingJob.Create(
            "J-seg",
            "R-1",
            geometry,
            new CompositeRollProfile(new[]
            {
                RollProfileSegment.Create(1, ProfileTypeKeys.Crown, 0.0, 2000.0, crown.Schema.CreateDefaults()),
                RollProfileSegment.Create(2, ProfileTypeKeys.Taper, 1900.0, 2400.0, taper.Schema.CreateDefaults()),
            }),
            new[] { new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()) });

        ParameterValidationResult result = CreateValidator().Validate(job, Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == "seg2.ToMm"
            && violation.Kind == ParameterViolationKind.ExceedsMachineLimit);
    }

    [Fact]
    public void A_bad_parameter_in_one_segment_names_that_segment()
    {
        var crown = new CrownProfileType();
        RollGeometry geometry = RollGeometry.FromDiameter(2000.0, 650.0);

        GrindingJob job = GrindingJob.Create(
            "J-seg",
            "R-1",
            geometry,
            new CompositeRollProfile(new[]
            {
                RollProfileSegment.Create(1, ProfileTypeKeys.Crown, 0.0, 2000.0, crown.Schema.CreateDefaults()),
                RollProfileSegment.Create(
                    2,
                    ProfileTypeKeys.Crown,
                    0.0,
                    500.0,
                    crown.Schema.CreateDefaults()
                        .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(1e9))),
            }),
            new[] { new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()) });

        ParameterValidationResult result = CreateValidator().Validate(job, Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == "seg2." + CrownProfileType.CrownDiameterMicrometerKey);
    }

    [Fact]
    public void An_option_that_is_not_offered_is_reported()
    {
        ParameterSet overrides = new ParameterSet(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                StepParameterKeys.SpeedVariationTarget, ParameterValue.FromChoice("sideways")),
        });

        ParameterValidationResult result = CreateValidator().Validate(CreateJob(overrides), Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepParameterKeys.SpeedVariationTarget
            && violation.Kind == ParameterViolationKind.NotAllowed);
    }

    [Fact]
    public void A_step_needing_a_device_the_machine_does_not_have_is_rejected()
    {
        var eddyCurrent = new EddyCurrentStepType();
        GrindingJob job = JobWith(
            new GrindingJobStep(1, StepTypeKeys.EddyCurrent, eddyCurrent.Schema.CreateDefaults()));

        // Capability 里没有任何选件：这台机床没装探伤器。
        ParameterValidationResult result = CreateFullValidator().Validate(job, Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepTypeKeys.EddyCurrent
            && violation.Kind == ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void The_same_step_passes_once_the_device_is_fitted()
    {
        var eddyCurrent = new EddyCurrentStepType();
        GrindingJob job = JobWith(
            new GrindingJobStep(1, StepTypeKeys.EddyCurrent, eddyCurrent.Schema.CreateDefaults()));

        CreateFullValidator().Validate(job, FullyEquipped).Violations
            .Should().NotContain(violation =>
                violation.Kind == ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void Wheel_dressing_needs_a_dresser()
    {
        var dress = new WheelDressStepType();
        GrindingJob job = JobWith(
            new GrindingJobStep(1, StepTypeKeys.WheelDress, dress.Schema.CreateDefaults()));

        CreateFullValidator().Validate(job, Capability).Violations.Should().Contain(violation =>
            violation.ParameterKey == StepTypeKeys.WheelDress
            && violation.Kind == ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void A_step_that_needs_no_device_runs_on_any_machine()
    {
        Capability.Supports(new RoughGrindingStepType()).Should().BeTrue();
        Capability.Supports(new StartStepType()).Should().BeTrue();
        Capability.Supports(new EddyCurrentStepType()).Should().BeFalse();
        FullyEquipped.Supports(new EddyCurrentStepType()).Should().BeTrue();
    }

    [Fact]
    public void A_missing_device_does_not_pile_parameter_errors_on_top()
    {
        // 装置都没有，参数再怎么校验都没意义——只报"未配置"这一条，别让人去改参数。
        var eddyCurrent = new EddyCurrentStepType();
        GrindingJob job = JobWith(
            new GrindingJobStep(1, StepTypeKeys.EddyCurrent, ParameterSet.Empty));

        ParameterValidationResult result = CreateFullValidator().Validate(job, Capability);

        result.Violations.Should().ContainSingle()
            .Which.Kind.Should().Be(ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void A_step_that_measures_needs_a_diameter_gauge()
    {
        var finish = new FinishGrindingStepType();
        GrindingJob job = JobWith(
            new GrindingJobStep(1, StepTypeKeys.Finish, finish.Schema.CreateDefaults()));

        // 精磨结束后要测量，而这台机床没有测径通道。
        ParameterValidationResult result = CreateFullValidator().Validate(job, Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepTypeKeys.Finish
            && violation.Kind == ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void In_process_gauging_needs_a_diameter_gauge_too()
    {
        var finish = new FinishGrindingStepType();
        GrindingJob job = JobWith(
            new GrindingJobStep(1, StepTypeKeys.Finish, finish.Schema.CreateDefaults()));

        ParameterValidationResult result = CreateFullValidator().Validate(job, Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepParameterKeys.InProcessMeasurement
            && violation.Kind == ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void Measuring_steps_are_accepted_on_a_machine_with_a_gauge()
    {
        var finish = new FinishGrindingStepType();
        GrindingJob job = JobWith(
            new GrindingJobStep(1, StepTypeKeys.Finish, finish.Schema.CreateDefaults()));

        CreateFullValidator().Validate(job, FullyEquipped).Violations
            .Should().NotContain(violation =>
                violation.Kind == ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void The_catalogue_holds_the_eight_switches_from_the_design_sheet()
    {
        ProgramOptionCatalog.All.Should().HaveCount(8);
        ProgramOptionCatalog.All.Select(option => option.Key).Should().Equal(
            ProgramOptionKeys.PreGrindMeasure,
            ProgramOptionKeys.MountingErrorMeasure,
            ProgramOptionKeys.AxisFeedForward,
            ProgramOptionKeys.U1AutoLevel,
            ProgramOptionKeys.WheelAutoApproach,
            ProgramOptionKeys.InProcessMeasure,
            ProgramOptionKeys.PostGrindMeasure,
            ProgramOptionKeys.EddyCurrentTest);

        ProgramOptionCatalog.All.Should().OnlyContain(option => option.HasRequirement,
            "八个开关都有前置条件，否则就不该做成可关的开关");
    }

    [Fact]
    public void A_job_without_program_options_still_has_every_switch_defined()
    {
        GrindingJob job = JobWith(
            new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()));

        // 不传就补默认值：少一个键不该让"这个开关开没开"变成未定义。
        GrindingJob withDefaults = GrindingJob.Create(
            job.JobId, job.RollId, job.Geometry, job.Profile, job.Steps);

        withDefaults.ProgramOptions.Count.Should().Be(8);
        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All)
        {
            withDefaults.IsProgramOptionEnabled(option.Key).Should().Be(option.DefaultEnabled);
        }
    }

    [Fact]
    public void A_switch_the_machine_cannot_do_is_reported_when_it_is_on()
    {
        GrindingJob job = JobFor(ProgramOptionKeys.EddyCurrentTest, isOn: true);

        // Capability 没装探伤器。
        ParameterValidationResult result = CreateValidator().Validate(job, Capability);

        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == ProgramOptionKeys.EddyCurrentTest
            && violation.Kind == ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void The_same_switch_turned_off_is_fine_on_any_machine()
    {
        GrindingJob job = JobFor(ProgramOptionKeys.EddyCurrentTest, isOn: false);

        CreateValidator().Validate(job, Capability).Violations
            .Should().NotContain(violation => violation.ParameterKey == ProgramOptionKeys.EddyCurrentTest);
    }

    [Fact]
    public void Every_switch_passes_on_a_fully_equipped_machine()
    {
        ParameterSet allOn = new(ProgramOptionCatalog.All.Select(option =>
            new System.Collections.Generic.KeyValuePair<string, ParameterValue>(
                option.Key, ParameterValue.FromBoolean(true))));

        GrindingJob job = GrindingJob.Create(
            "J-opt", "R-1", Geometry, ProfileTypeKeys.Cylindrical, ParameterSet.Empty,
            new[] { new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()) },
            allOn);

        CreateValidator().Validate(job, FullyEquipped).Violations
            .Should().NotContain(violation => violation.Kind == ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void Feed_forward_needs_the_crown_adjust_axis()
    {
        GrindingJob job = JobFor(ProgramOptionKeys.AxisFeedForward, isOn: true);

        // 装了所有选件与测头，但没有中高调整轴。
        MachineCapability withoutAxis = FullyEquipped with
        {
            AvailableAxisRoles = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal),
        };

        CreateValidator().Validate(job, withoutAxis).Violations.Should().Contain(violation =>
            violation.ParameterKey == ProgramOptionKeys.AxisFeedForward
            && violation.Kind == ParameterViolationKind.MachineOptionMissing);
    }

    [Fact]
    public void Measuring_switches_need_a_diameter_gauge()
    {
        MachineCapability withoutGauge = FullyEquipped with
        {
            AvailableMeasurements = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal),
        };

        string[] measuringSwitches =
        {
            ProgramOptionKeys.PreGrindMeasure,
            ProgramOptionKeys.InProcessMeasure,
            ProgramOptionKeys.PostGrindMeasure,
        };

        foreach (string key in measuringSwitches)
        {
            CreateValidator().Validate(JobFor(key, isOn: true), withoutGauge).Violations
                .Should().Contain(
                    violation => violation.ParameterKey == key
                        && violation.Kind == ParameterViolationKind.MachineOptionMissing,
                    $"开关 {key} 要用测头");
        }
    }

    /// <summary>只打开一个开关、其余全关的一份作业。</summary>
    private static GrindingJob JobFor(string optionKey, bool isOn) => GrindingJob.Create(
        "J-opt",
        "R-1",
        Geometry,
        ProfileTypeKeys.Cylindrical,
        ParameterSet.Empty,
        new[] { new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()) },
        AllProgramOptionsOff.With(optionKey, ParameterValue.FromBoolean(isOn)));

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

public sealed class StepDurationTests
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    [Fact]
    public void A_pass_is_one_return_stroke_along_the_body()
    {
        var stepType = new FinishGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.PassCount, ParameterValue.FromNumber(2.0))
            .With(StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(2000.0))
            .With(StepParameterKeys.SparkOutPassCount, ParameterValue.FromNumber(0.0))
            .With(StepParameterKeys.ReversalDwellSeconds, ParameterValue.FromNumber(0.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        // 一道 = 一个往复：2 道 × 2 × 2000 mm ÷ 2000 mm/min = 4 min
        plan.PassCount.Should().Be(2);
        plan.EstimateDuration(Geometry).TotalMinutes.Should().BeApproximately(4.0, 1e-9);
    }

    [Fact]
    public void Spark_out_passes_count_towards_the_estimate()
    {
        var stepType = new SparkOutStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.PassCount, ParameterValue.FromNumber(3.0))
            .With(StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(1000.0))
            .With(StepParameterKeys.ReversalDwellSeconds, ParameterValue.FromNumber(0.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.EstimateDuration(Geometry).TotalMinutes.Should().BeApproximately(12.0, 1e-9);
    }

    [Fact]
    public void Reversal_dwell_is_part_of_the_estimate()
    {
        // 每个往复停两次：3 道 × 2 × 2 s = 12 s = 0.2 min，加在 12 min 上。
        var stepType = new SparkOutStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.PassCount, ParameterValue.FromNumber(3.0))
            .With(StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(1000.0))
            .With(StepParameterKeys.ReversalDwellSeconds, ParameterValue.FromNumber(2.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.EstimateDuration(Geometry).TotalMinutes.Should().BeApproximately(12.2, 1e-9);
    }

    [Fact]
    public void Continuous_infeed_stops_at_the_stock_target_when_that_comes_first()
    {
        var stepType = new RoughGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(10.0))
            .With(StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(0.0))
            .With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(50.0))
            .With(StepParameterKeys.PassCount, ParameterValue.FromNumber(100.0))
            .With(StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(2000.0))
            .With(StepParameterKeys.ReversalDwellSeconds, ParameterValue.FromNumber(0.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        // 50 µm ÷ 10 µm/min = 5 min，远早于 100 道次的预算，取先到的那个。
        plan.EstimateDuration(Geometry).TotalMinutes.Should().BeApproximately(5.0, 1e-9);
    }

    [Fact]
    public void Both_components_together_reach_the_stock_target_sooner()
    {
        // 连续 10 µm/min，加上每道 5 µm；一道 = 2×2000÷2000 = 2 min，
        // 折算成 2.5 µm/min，合计 12.5 µm/min → 50 µm 要 4 min。
        // 按旧的"二选一"模型只算连续那一路会算成 5 min，实际早磨到了。
        var stepType = new RoughGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(10.0))
            .With(StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(5.0))
            .With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(50.0))
            .With(StepParameterKeys.PassCount, ParameterValue.FromNumber(100.0))
            .With(StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(2000.0))
            .With(StepParameterKeys.ReversalDwellSeconds, ParameterValue.FromNumber(0.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.EstimateDuration(Geometry).TotalMinutes.Should().BeApproximately(4.0, 1e-9);
    }

    [Fact]
    public void Spark_out_passes_run_after_the_stock_target_is_reached()
    {
        // 切削段 4 min 就到量，之后 2 道光磨照走（每道 2 min）→ 8 min。
        var stepType = new RoughGrindingStepType();
        ParameterSet parameters = stepType.Schema.CreateDefaults()
            .With(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, ParameterValue.FromNumber(10.0))
            .With(StepParameterKeys.InfeedPerPassDiameterMicrometer, ParameterValue.FromNumber(5.0))
            .With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(50.0))
            .With(StepParameterKeys.PassCount, ParameterValue.FromNumber(100.0))
            .With(StepParameterKeys.SparkOutPassCount, ParameterValue.FromNumber(2.0))
            .With(StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(2000.0))
            .With(StepParameterKeys.ReversalDwellSeconds, ParameterValue.FromNumber(0.0));

        GrindingStepPlan plan = stepType.CreatePlan(Geometry, parameters);

        plan.EstimateDuration(Geometry).TotalMinutes.Should().BeApproximately(8.0, 1e-9);
    }

    [Fact]
    public void A_step_without_feed_takes_no_estimated_time()
    {
        var plan = new GrindingStepPlan("X", 1, 0.0, 0.0, 0.0, 0.0, 0, false);

        plan.EstimateDuration(Geometry).Should().Be(System.TimeSpan.Zero);
    }
}
