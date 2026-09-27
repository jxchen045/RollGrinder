using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.App.Controls;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>工序简图（修改稿 5.3）：光标落到哪一格参数，图上就亮哪一处。</summary>
public sealed class StepDiagramMapTests
{
    [Fact]
    public void Every_parameter_of_every_step_type_lights_something_up_on_its_own_diagram()
    {
        foreach (IGrindingStepType stepType in SampleMachine.AllStepTypes())
        {
            StepDiagramKind kind = StepDiagramMap.KindOf(stepType.Key);
            foreach (ParameterDescriptor descriptor in stepType.Schema.Descriptors)
            {
                DiagramElement? element = StepDiagramMap.ElementOf(descriptor.Key);
                element.Should().NotBeNull($"{stepType.Key}.{descriptor.Key} 在简图上没有对应的量");
                StepDiagramMap.ElementsOf(kind).Should().Contain(element!.Value,
                    $"{stepType.Key} 用的 {kind} 简图上要画出 {descriptor.Key}");
            }
        }
    }

    [Fact]
    public void Only_start_and_end_fall_back_to_a_plain_marker()
    {
        SampleMachine.AllStepTypes()
            .Where(stepType => StepDiagramMap.KindOf(stepType.Key) == StepDiagramKind.Marker)
            .Select(stepType => stepType.Key)
            .Should().BeEquivalentTo(new[] { StepTypeKeys.Start, StepTypeKeys.End });
    }

    [Fact]
    public void Every_drawn_element_has_a_language_free_symbol()
    {
        foreach (DiagramElement element in Enum.GetValues<DiagramElement>())
        {
            string symbol = StepDiagramMap.SymbolOf(element);
            symbol.Should().NotBeNullOrEmpty();
            symbol.All(c => c < 0x2E80).Should().BeTrue($"{element} 的符号不该带中文：{symbol}");
        }
    }
}
