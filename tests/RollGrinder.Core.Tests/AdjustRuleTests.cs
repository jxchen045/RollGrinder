using FluentAssertions;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>两层参数的范围规则（设计说明 5.2 / 5.3，第 3 期验收"超范围不收"）。</summary>
public sealed class AdjustRuleTests
{
    private static readonly ParameterDescriptor Feed = ParameterDescriptor.Number(StepParameterKeys.FeedMmPerMin, ParameterUnit.MillimeterPerMinute, 800.0, 100.0, 3000.0);

    [Theory]
    [InlineData("±10%", AdjustMode.Percent)]
    [InlineData("decrease", AdjustMode.DecreaseOnly)]
    [InlineData("increase", AdjustMode.IncreaseOnly)]
    [InlineData("locked", AdjustMode.Locked)]
    [InlineData("4..8", AdjustMode.Absolute)]
    public void Rules_round_trip_through_their_text(string text, AdjustMode mode)
    {
        AdjustRule rule = AdjustRule.Parse(text)!;
        rule.Mode.Should().Be(mode);
        AdjustRule.Parse(rule.ToText()).Should().Be(rule);
    }

    [Fact]
    public void Unreadable_text_is_not_guessed() => AdjustRule.Parse("about ten").Should().BeNull();

    [Fact]
    public void Plus_minus_ten_percent_of_800_allows_720_to_880()
    {
        AdjustBounds bounds = AdjustRanges.Resolve(AdjustRule.PlusMinus(10.0), 800.0, Feed);

        bounds.Should().Be(new AdjustBounds(true, 720.0, 880.0));
        AdjustRanges.Check(AdjustRule.PlusMinus(10.0), 800.0, 760.0, Feed).Should().Be(AdjustRefusal.None);
        AdjustRanges.Check(AdjustRule.PlusMinus(10.0), 800.0, 700.0, Feed).Should().Be(AdjustRefusal.BelowRange, "超范围不收");
        AdjustRanges.Check(AdjustRule.PlusMinus(10.0), 800.0, 900.0, Feed).Should().Be(AdjustRefusal.AboveRange);
    }

    [Fact]
    public void Decrease_only_stops_at_the_plan_value_and_the_machine_floor()
    {
        AdjustRanges.Resolve(AdjustRule.DecreaseOnly, 800.0, Feed).Should().Be(new AdjustBounds(true, 100.0, 800.0));
        AdjustRanges.Check(AdjustRule.DecreaseOnly, 800.0, 810.0, Feed).Should().Be(AdjustRefusal.AboveRange);
    }

    [Fact]
    public void The_machine_limit_caps_every_range()
    {
        AdjustRanges.Resolve(AdjustRule.PlusMinus(50.0), 2800.0, Feed).Upper.Should().Be(3000.0, "机床限是硬上限，方案和作业都超不过");
        AdjustRanges.Resolve(AdjustRule.IncreaseOnly, 800.0, Feed).Upper.Should().Be(3000.0);
    }

    [Fact]
    public void Locked_parameters_take_only_the_plan_value()
    {
        AdjustRanges.Check(AdjustRule.Locked, 800.0, 800.0, Feed).Should().Be(AdjustRefusal.None);
        AdjustRanges.Check(AdjustRule.Locked, 800.0, 801.0, Feed).Should().Be(AdjustRefusal.Locked);
    }

    [Fact]
    public void A_plan_override_wins_over_the_machine_table_and_the_built_in_defaults()
    {
        var machine = new System.Collections.Generic.Dictionary<string, AdjustRule> { [StepParameterKeys.FeedMmPerMin] = AdjustRule.PlusMinus(5.0) };

        AdjustRanges.RuleFor(StepParameterKeys.FeedMmPerMin, null, null).Should().Be(AdjustRule.PlusMinus(10.0));
        AdjustRanges.RuleFor(StepParameterKeys.FeedMmPerMin, null, machine).Should().Be(AdjustRule.PlusMinus(5.0));
        AdjustRanges.RuleFor(StepParameterKeys.FeedMmPerMin, AdjustRule.Between(700, 820), machine).Mode.Should().Be(AdjustMode.Absolute);
        AdjustRanges.RuleFor(StepParameterKeys.WheelSurfaceSpeedMPerSec, null, null).Should().Be(AdjustRule.Locked, "砂轮线速度不能调");
    }
}
