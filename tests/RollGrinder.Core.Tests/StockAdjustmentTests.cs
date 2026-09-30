using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>本次余量与程序标准余量不同：差额由粗磨吸收，精工序不动（流程调整方案第 5 节）。</summary>
public sealed class StockAdjustmentTests
{
    private static GrindingJobStep Step(int order, string type, double stock) =>
        new(order, type, ParameterSet.Empty.With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(stock)));

    private static readonly GrindingJobStep[] Program =
    {
        new(1, StepTypeKeys.Start, ParameterSet.Empty),
        Step(2, StepTypeKeys.Rough, 280),
        Step(3, StepTypeKeys.Finish, 60),
        Step(4, StepTypeKeys.SparkOut, 10),
        new(5, StepTypeKeys.End, ParameterSet.Empty),
    };

    private static double Stock(StockAdjustmentResult r, string type) =>
        r.Steps.Single(s => s.StepTypeKey == type).Parameters.GetNumber(StepParameterKeys.StockDiameterMicrometer);

    [Fact]
    public void Same_stock_leaves_the_program_untouched()
    {
        StockAdjustmentResult result = StockAdjustment.Apply(Program, 350.5);
        result.ProblemResourceKey.Should().BeNull();
        result.Steps.Should().BeSameAs(Program);
    }

    [Theory]
    [InlineData(500, 430)]
    [InlineData(200, 130)]
    public void Rough_absorbs_the_difference_and_finishing_keeps_its_amount(double actual, double rough)
    {
        StockAdjustmentResult result = StockAdjustment.Apply(Program, actual);
        result.ProblemResourceKey.Should().BeNull();
        Stock(result, StepTypeKeys.Rough).Should().Be(rough);
        Stock(result, StepTypeKeys.Finish).Should().Be(60);
        Stock(result, StepTypeKeys.SparkOut).Should().Be(10);
    }

    [Fact]
    public void Two_rough_steps_share_in_their_original_ratio()
    {
        GrindingJobStep[] program = { Step(1, StepTypeKeys.Rough, 300), Step(2, StepTypeKeys.Rough, 100), Step(3, StepTypeKeys.Finish, 50) };
        StockAdjustmentResult result = StockAdjustment.Apply(program, 850);
        result.Steps[0].Parameters.GetNumber(StepParameterKeys.StockDiameterMicrometer).Should().Be(600);
        result.Steps[1].Parameters.GetNumber(StepParameterKeys.StockDiameterMicrometer).Should().Be(200);
    }

    [Fact]
    public void Less_than_the_finishing_amount_is_refused() =>
        StockAdjustment.Apply(Program, 50).ProblemResourceKey.Should().Be("Stock_BelowFinishing");

    [Fact]
    public void A_program_without_rough_cannot_absorb_a_difference() =>
        StockAdjustment.Apply(new[] { Step(1, StepTypeKeys.Finish, 60) }, 100).ProblemResourceKey.Should().Be("Stock_NoRoughStep");

    [Fact]
    public void A_measure_only_program_accepts_zero_stock() =>
        StockAdjustment.Apply(new[] { new GrindingJobStep(1, StepTypeKeys.Measure, ParameterSet.Empty) }, 0.0).ProblemResourceKey.Should().BeNull();
}
