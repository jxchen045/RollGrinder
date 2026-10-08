using FluentAssertions;
using RollGrinder.Core.Steps;
using Xunit;

namespace RollGrinder.Core.Tests;

public sealed class StepParameterGroupsTests
{
    [Theory]
    [InlineData(StepParameterKeys.FeedMmPerMin, StepParameterGroup.Speed)]
    [InlineData(StepParameterKeys.WorkpieceSpeedRpm, StepParameterGroup.Speed)]
    [InlineData(StepParameterKeys.WheelSurfaceSpeedMPerSec, StepParameterGroup.Speed)]
    [InlineData(StepParameterKeys.InfeedPerPassDiameterMicrometer, StepParameterGroup.Infeed)]
    [InlineData(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, StepParameterGroup.Infeed)]
    [InlineData(StepParameterKeys.PassCount, StepParameterGroup.Removal)]
    [InlineData(StepParameterKeys.StockDiameterMicrometer, StepParameterGroup.Removal)]
    [InlineData(StepParameterKeys.SparkOutPassCount, StepParameterGroup.Removal)]
    [InlineData(StepParameterKeys.ReversalDwellSeconds, StepParameterGroup.Other)]
    [InlineData(StepParameterKeys.SpeedVariationPercent, StepParameterGroup.Other)]
    [InlineData("somethingNew", StepParameterGroup.Other)]
    public void Groups_parameters_by_what_the_operator_judges(string key, StepParameterGroup expected) =>
        StepParameterGroups.GroupOf(key).Should().Be(expected);

    [Fact]
    public void A_grinding_step_splits_speed_and_infeed_left_the_rest_right()
    {
        // 速度 3、进给 2、道次与去除 3、其他 3（变速合成一行）：左 5 行，右 6 行。
        StepParameterGroups.SplitIndex(new[] { 3, 2, 3, 3 }).Should().Be(2);
    }

    [Fact]
    public void A_single_group_stays_in_the_left_column() =>
        StepParameterGroups.SplitIndex(new[] { 4 }).Should().Be(1);

    [Fact]
    public void No_groups_means_nothing_on_either_side() =>
        StepParameterGroups.SplitIndex(System.Array.Empty<int>()).Should().Be(0);

    [Fact]
    public void Two_equal_groups_take_one_column_each() =>
        StepParameterGroups.SplitIndex(new[] { 2, 2 }).Should().Be(1);
}
