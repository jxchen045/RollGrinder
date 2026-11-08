using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Services.Library;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>方案 / 模板对比（设计稿 v4 5.5.1、5.6.1）：只列出真的不同的那几项，序号与作业一致。</summary>
public sealed class PlanDiffTests
{
    private static readonly GrindingStepTypeRegistry StepTypes = new(new IGrindingStepType[]
    {
        new StartStepType(), new EndStepType(), new RoughGrindingStepType(), new FinishGrindingStepType(), new SparkOutStepType(),
    });

    private static RollProfileDefinition Profile(double crown) => RollProfileDefinition.Create(
        "P", "凸度", 1800.0,
        CompositeRollProfile.Sequential(0.0, new[]
        {
            new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
            new SequentialSegment(ProfileTypeKeys.Crown, 1720.0,
                new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(crown))),
            new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
        }),
        DateTimeOffset.UnixEpoch) with { ToleranceMicrometer = 5.0 };

    private static GrindingProgram Program(double finishFeed) => GrindingProgram.Create("G", "标准", new[]
    {
        new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
        new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()
            .With(StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(finishFeed))),
    }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Only_the_changed_items_differ_and_steps_are_numbered_like_the_job()
    {
        var a = new CompareSide("方案 3", Profile(300.0), Program(800.0), new[] { new PlanParamRange(3, StepParameterKeys.FeedMmPerMin, "±10%") }, 40.0);
        var b = new CompareSide("方案 5", Profile(280.0), Program(760.0), new[] { new PlanParamRange(3, StepParameterKeys.FeedMmPerMin, "±5%") }, 40.0);

        DiffRow[] differs = PlanDiff.Compare(a, b, StepTypes).Where(row => row.Differs).ToArray();

        differs.Should().HaveCount(3);
        differs.Should().ContainSingle(row => row.Field == DiffField.SegmentParameter && row.Index == 2
            && row.ParameterKey == CrownProfileType.CrownDiameterMicrometerKey && row.A == "300" && row.B == "280");
        differs.Should().ContainSingle(row => row.Field == DiffField.StepParameter && row.Index == 3
            && row.TypeKey == StepTypeKeys.Finish && row.ParameterKey == StepParameterKeys.FeedMmPerMin);
        differs.Should().ContainSingle(row => row.Field == DiffField.Range && row.Index == 3 && row.A == "±10%" && row.B == "±5%");
    }

    [Fact]
    public void A_template_compares_only_what_it_has()
    {
        DiffRow[] rows = PlanDiff.Compare(new CompareSide("v1", Profile(300.0), null), new CompareSide("v2", Profile(300.0), null), StepTypes).ToArray();

        rows.Should().NotBeEmpty();
        rows.Should().NotContain(row => row.Field == DiffField.StepType || row.Field == DiffField.Range);
        rows.Should().OnlyContain(row => !row.Differs, "同一条辊形");
    }

    [Fact]
    public void Different_step_types_are_not_compared_parameter_by_parameter()
    {
        GrindingProgram other = GrindingProgram.Create("G2", "光磨", new[]
        {
            new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
            new GrindingJobStep(2, StepTypeKeys.SparkOut, new SparkOutStepType().Schema.CreateDefaults()),
        }, DateTimeOffset.UnixEpoch);

        DiffRow[] rows = PlanDiff.Compare(new CompareSide("a", null, Program(800.0)), new CompareSide("b", null, other), StepTypes).ToArray();

        rows.Should().ContainSingle(row => row.Field == DiffField.StepType && row.Index == 3 && row.Differs);
        rows.Should().NotContain(row => row.Field == DiffField.StepParameter && row.Index == 3);
    }
}
