using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>跨工序检查与余量分配（修改稿 5.3）：只看工序之间的关系，报成提示。</summary>
public sealed class ProgramChecksTests
{
    private static readonly GrindingStepTypeRegistry Registry = new(new IGrindingStepType[]
    {
        new RoughGrindingStepType(),
        new SemiFinishGrindingStepType(),
        new FinishGrindingStepType(),
        new SparkOutStepType(),
        new MeasureStepType(),
    });

    private static GrindingJobStep Step(int order, string type, double? stock = null)
    {
        ParameterSet parameters = Registry.Get(type).Schema.CreateDefaults();
        if (stock is double value)
        {
            parameters = parameters.With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(value));
        }

        return new GrindingJobStep(order, type, parameters);
    }

    [Fact]
    public void A_sensible_program_has_no_findings()
    {
        var steps = new[]
        {
            Step(1, StepTypeKeys.Rough, 200), Step(2, StepTypeKeys.SemiFinish, 60), Step(3, StepTypeKeys.Finish, 20),
            Step(4, StepTypeKeys.Measure),
        };

        ProgramChecks.Find(steps, Registry, 280).Should().BeEmpty();
    }

    [Fact]
    public void Stock_that_does_not_add_up_to_the_total_is_reported()
    {
        var steps = new[] { Step(1, StepTypeKeys.Rough, 200), Step(2, StepTypeKeys.Finish, 20), Step(3, StepTypeKeys.Measure) };

        ProgramFinding finding = ProgramChecks.Find(steps, Registry, 300).Should().ContainSingle().Subject;
        finding.Kind.Should().Be(ProgramFindingKind.StockDoesNotAddUp);
        finding.Value.Should().Be(220);
        finding.Reference.Should().Be(300);
        ProgramChecks.Find(steps, Registry, null).Should().BeEmpty("没给总余量就不对账");
    }

    [Fact]
    public void Grinding_without_a_measurement_afterwards_is_reported_on_the_last_grinding_step()
    {
        var steps = new[] { Step(1, StepTypeKeys.Measure), Step(2, StepTypeKeys.Rough, 200), Step(3, StepTypeKeys.Finish, 20) };

        ProgramChecks.Find(steps, Registry, null).Should().ContainSingle()
            .Which.Should().Be(new ProgramFinding(ProgramFindingKind.NoMeasurementAfterGrinding, 3));
    }

    [Fact]
    public void A_finer_step_removing_more_than_the_rougher_one_before_it_is_reported()
    {
        var steps = new[] { Step(1, StepTypeKeys.Rough, 20), Step(2, StepTypeKeys.Finish, 60), Step(3, StepTypeKeys.Measure) };

        ProgramChecks.Find(steps, Registry, null).Should().ContainSingle()
            .Which.Should().Be(new ProgramFinding(ProgramFindingKind.StockIncreasesTowardsFinish, 2, 60, 20));
    }

    [Fact]
    public void Distribution_keeps_the_current_proportions_and_adds_up_exactly()
    {
        IReadOnlyList<double> shares = ProgramChecks.Distribute(
            new[] { 200.0, 60.0, 20.0 }, new[] { StepTypeKeys.Rough, StepTypeKeys.SemiFinish, StepTypeKeys.Finish }, 350.0);

        shares.Sum().Should().BeApproximately(350.0, 1e-9);
        shares[0].Should().BeApproximately(250.0, 0.11);
        shares[1].Should().BeApproximately(75.0, 0.11);
        shares[2].Should().BeApproximately(25.0, 0.11);
    }

    [Fact]
    public void Distribution_from_all_zero_uses_the_typical_shares()
    {
        IReadOnlyList<double> shares = ProgramChecks.Distribute(
            new[] { 0.0, 0.0 }, new[] { StepTypeKeys.Rough, StepTypeKeys.Finish }, 70.0);

        shares.Should().Equal(60.0, 10.0);
    }
}
