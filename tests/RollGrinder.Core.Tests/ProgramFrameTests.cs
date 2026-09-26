using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>工艺程序首尾固定："开始"第一道、"结束"最后一道，各一个（修改稿 5.3）。</summary>
public sealed class ProgramFrameTests
{
    private static readonly GrindingStepTypeRegistry Registry =
        new(new IGrindingStepType[] { new StartStepType(), new EndStepType(), new RoughGrindingStepType(), new MeasureStepType() });

    private static GrindingJobStep Step(int order, string key) =>
        new(order, key, Registry.Get(key).Schema.CreateDefaults());

    [Fact]
    public void A_program_without_start_and_end_gets_them()
    {
        var steps = ProgramFrame.Normalize(new[] { Step(1, StepTypeKeys.Measure), Step(2, StepTypeKeys.Rough) }, Registry);

        steps.Select(s => s.StepTypeKey).Should().Equal(
            StepTypeKeys.Start, StepTypeKeys.Measure, StepTypeKeys.Rough, StepTypeKeys.End);
        steps.Select(s => s.Order).Should().Equal(1, 2, 3, 4);
        ProgramFrame.IsNormalized(steps).Should().BeTrue();
    }

    [Fact]
    public void Misplaced_or_duplicated_start_and_end_are_moved_to_the_ends_once()
    {
        var steps = ProgramFrame.Normalize(new[]
        {
            Step(1, StepTypeKeys.Rough), Step(2, StepTypeKeys.End), Step(3, StepTypeKeys.Start),
            Step(4, StepTypeKeys.Measure), Step(5, StepTypeKeys.Start),
        }, Registry);

        steps.Select(s => s.StepTypeKey).Should().Equal(
            StepTypeKeys.Start, StepTypeKeys.Rough, StepTypeKeys.Measure, StepTypeKeys.End);
    }

    [Fact]
    public void Only_start_and_end_are_fixed()
    {
        ProgramFrame.IsFixed(StepTypeKeys.Start).Should().BeTrue();
        ProgramFrame.IsFixed(StepTypeKeys.End).Should().BeTrue();
        ProgramFrame.IsFixed(StepTypeKeys.Rough).Should().BeFalse();
        ProgramFrame.IsNormalized(new[] { Step(1, StepTypeKeys.Rough) }).Should().BeFalse();
    }
}
