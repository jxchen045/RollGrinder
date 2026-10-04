using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using Xunit;

namespace RollGrinder.Core.Tests;

public sealed class ProgramOptionPlannerTests
{
    private static readonly GrindingStepTypeRegistry Registry = new(new IGrindingStepType[]
    {
        new StartStepType(), new EndStepType(), new MeasureStepType(), new RoughGrindingStepType(),
        new FinishGrindingStepType(), new EddyCurrentStepType(),
    });

    private static readonly MachineCapability Bare = new(0.05, 3000.0, 120.0, 1200.0, 300.0, 5200.0, 75.0, 650.0);

    private static readonly MachineCapability Equipped = Bare with
    {
        InstalledOptions = new HashSet<string>(System.StringComparer.Ordinal)
        {
            MachineOptionKeys.EddyCurrentTester, MachineOptionKeys.DualProbeMeasurement,
            MachineOptionKeys.U1Leveling, MachineOptionKeys.ContactDetection,
        },
        AvailableAxisRoles = new HashSet<string>(System.StringComparer.Ordinal) { MachineAxisRoleNames.RollProfile },
        AvailableMeasurements = new HashSet<string>(System.StringComparer.Ordinal) { MeasurementQuantities.Diameter },
    };

    private static GrindingJobStep Step(int order, string type, bool? inProcess = null)
    {
        ParameterSet parameters = Registry.Get(type).Schema.CreateDefaults();
        if (inProcess is { } on)
        {
            parameters = parameters.With(StepParameterKeys.InProcessMeasurement, ParameterValue.FromBoolean(on));
        }

        return new GrindingJobStep(order, type, parameters);
    }

    private static readonly GrindingJobStep[] MeasureGrindMeasure =
    {
        Step(1, StepTypeKeys.Start), Step(2, StepTypeKeys.Measure), Step(3, StepTypeKeys.Rough),
        Step(4, StepTypeKeys.Finish, inProcess: true), Step(5, StepTypeKeys.Measure), Step(6, StepTypeKeys.End),
    };

    [Fact]
    public void Step_linked_switches_point_at_the_step_they_govern()
    {
        ProgramOptionPlanner.LinkedStepOrder(ProgramOptionKeys.PreGrindMeasure, MeasureGrindMeasure, Registry).Should().Be(2);
        ProgramOptionPlanner.LinkedStepOrder(ProgramOptionKeys.PostGrindMeasure, MeasureGrindMeasure, Registry).Should().Be(5);
        ProgramOptionPlanner.LinkedStepOrder(ProgramOptionKeys.InProcessMeasure, MeasureGrindMeasure, Registry).Should().Be(4);
        ProgramOptionPlanner.LinkedStepOrder(ProgramOptionKeys.EddyCurrentTest, MeasureGrindMeasure, Registry).Should().BeNull();
    }

    [Fact]
    public void Switches_without_their_step_or_device_are_not_offered_and_go_out_off()
    {
        GrindingJobStep[] grindOnly = { Step(1, StepTypeKeys.Start), Step(2, StepTypeKeys.Rough, inProcess: false), Step(3, StepTypeKeys.End) };

        ParameterSet sent = ProgramOptionPlanner.Normalize(ParameterSet.Empty, grindOnly, Equipped, Registry);

        sent.GetBoolean(ProgramOptionKeys.PreGrindMeasure).Should().BeFalse();
        sent.GetBoolean(ProgramOptionKeys.PostGrindMeasure).Should().BeFalse();
        sent.GetBoolean(ProgramOptionKeys.InProcessMeasure).Should().BeFalse();
        sent.GetBoolean(ProgramOptionKeys.EddyCurrentTest).Should().BeFalse();
        sent.GetBoolean(ProgramOptionKeys.WheelAutoApproach).Should().BeTrue("device switches keep the catalog default");
        ProgramOptionPlanner.Normalize(ParameterSet.Empty, grindOnly, Bare, Registry)
            .GetBoolean(ProgramOptionKeys.WheelAutoApproach).Should().BeFalse("the bare machine has no contact detection");
    }

    [Fact]
    public void A_step_in_the_program_turns_its_switch_on_by_default_and_the_operator_can_skip_it_this_time()
    {
        GrindingJobStep[] withEddy = MeasureGrindMeasure.Take(5).Append(Step(6, StepTypeKeys.EddyCurrent)).Append(Step(7, StepTypeKeys.End)).ToArray();

        ProgramOptionPlanner.Normalize(ParameterSet.Empty, withEddy, Equipped, Registry)
            .GetBoolean(ProgramOptionKeys.EddyCurrentTest).Should().BeTrue("the catalog default is off, but the program has the step");

        var skip = new ParameterSet(new[]
        {
            new KeyValuePair<string, ParameterValue>(ProgramOptionKeys.EddyCurrentTest, ParameterValue.FromBoolean(false)),
        });
        ProgramOptionPlanner.Normalize(skip, withEddy, Equipped, Registry).GetBoolean(ProgramOptionKeys.EddyCurrentTest).Should().BeFalse();
        ProgramOptionPlanner.Normalize(skip, withEddy, Equipped, Registry).Count.Should().Be(ProgramOptionCatalog.All.Count);
    }
}
