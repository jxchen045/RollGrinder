using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

public sealed class ParameterSchemaTests
{
    private static readonly ParameterSchema Schema = new(new[]
    {
        ParameterDescriptor.Number("feedMmPerMin", ParameterUnit.MillimeterPerMinute, 800.0, 1.0, 3000.0),
        ParameterDescriptor.Boolean("coolantOn", true),
        ParameterDescriptor.Text("operatorNote", string.Empty, isRequired: false),
    });

    [Fact]
    public void Defaults_cover_every_declared_parameter()
    {
        ParameterSet defaults = Schema.CreateDefaults();

        defaults.Count.Should().Be(3);
        defaults.GetNumber("feedMmPerMin").Should().Be(800.0);
        defaults.GetBoolean("coolantOn").Should().BeTrue();
        Schema.Validate(defaults).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Missing_required_parameter_is_reported()
    {
        var parameters = new ParameterSet(new[]
        {
            new KeyValuePair<string, ParameterValue>("coolantOn", ParameterValue.FromBoolean(false)),
        });

        ParameterValidationResult result = Schema.Validate(parameters);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ParameterViolation("feedMmPerMin", ParameterViolationKind.Missing));
    }

    [Fact]
    public void Out_of_range_values_report_the_limit()
    {
        ParameterSet parameters = Schema.CreateDefaults().With("feedMmPerMin", ParameterValue.FromNumber(9000.0));

        ParameterValidationResult result = Schema.Validate(parameters);

        result.Violations.Should().ContainSingle(violation =>
            violation.ParameterKey == "feedMmPerMin"
            && violation.Kind == ParameterViolationKind.AboveMaximum
            && violation.Limit == 3000.0);
    }

    [Fact]
    public void Wrong_kind_is_reported_rather_than_coerced()
    {
        ParameterSet parameters = Schema.CreateDefaults().With("feedMmPerMin", ParameterValue.FromText("fast"));

        Schema.Validate(parameters).Violations.Should().ContainSingle(violation =>
            violation.Kind == ParameterViolationKind.KindMismatch);
    }

    [Fact]
    public void Unknown_parameters_are_reported()
    {
        ParameterSet parameters = Schema.CreateDefaults().With("wheelGrit", ParameterValue.FromNumber(60.0));

        Schema.Validate(parameters).Violations.Should().ContainSingle(violation =>
            violation.ParameterKey == "wheelGrit" && violation.Kind == ParameterViolationKind.Unknown);
    }

    [Fact]
    public void Resource_key_follows_the_naming_convention()
    {
        Schema.Get("feedMmPerMin").ResourceKey.Should().Be("Parameter_feedMmPerMin");
    }

    [Fact]
    public void Parameter_set_is_immutable()
    {
        ParameterSet original = Schema.CreateDefaults();

        ParameterSet modified = original.With("feedMmPerMin", ParameterValue.FromNumber(100.0));

        original.GetNumber("feedMmPerMin").Should().Be(800.0);
        modified.GetNumber("feedMmPerMin").Should().Be(100.0);
    }

    [Fact]
    public void Values_round_trip_through_their_persisted_form()
    {
        ParameterValue number = ParameterValue.FromNumber(12.5);
        ParameterValue boolean = ParameterValue.FromBoolean(true);
        ParameterValue text = ParameterValue.FromText("A3");

        ParameterValue.Parse(ParameterValueKind.Number, number.ToInvariantString()).Should().Be(number);
        ParameterValue.Parse(ParameterValueKind.Boolean, boolean.ToInvariantString()).Should().Be(boolean);
        ParameterValue.Parse(ParameterValueKind.Text, text.ToInvariantString()).Should().Be(text);
    }

    [Fact]
    public void Reading_a_value_as_the_wrong_kind_throws()
    {
        ParameterValue value = ParameterValue.FromNumber(1.0);

        value.Invoking(v => v.Text).Should().Throw<DomainException>();
    }
}
