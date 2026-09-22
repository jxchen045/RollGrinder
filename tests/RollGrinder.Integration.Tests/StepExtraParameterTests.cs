using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using RollGrinder.Contracts;
using RollGrinder.Core.Steps;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 工序专属参数块，以及它落在 R 参数上的地址。
///
/// 这一块的含义由工序类型码决定、顺序就是协议，所以两件事要守住：
/// 各类工序声明的项数放得下，而且地址不和别人撞。
/// </summary>
public sealed class StepExtraParameterTests
{
    private static JsonDocument SampleTagMap() =>
        JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "config", "tagmap.sample.json")));

    private static IGrindingStepType[] AllStepTypes() =>
        typeof(IGrindingStepType).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsPublic: true }
                && typeof(IGrindingStepType).IsAssignableFrom(type))
            .Select(type => (IGrindingStepType)Activator.CreateInstance(type)!)
            .ToArray();

    [Fact]
    public void No_step_type_declares_more_extras_than_the_block_holds()
    {
        // 声明多了下发时才炸就晚了——那时作业已经在操作工手里。
        foreach (IGrindingStepType stepType in AllStepTypes())
        {
            stepType.NcExtraParameterKeys.Count.Should()
                .BeLessThanOrEqualTo(MachineTagKeys.JobStepExtraCount, stepType.Key);
        }
    }

    [Fact]
    public void Every_declared_extra_is_a_parameter_that_step_type_actually_has()
    {
        // 拼错一个键，下发时那一格就会写出一个默认值，而 NC 照读不误。
        foreach (IGrindingStepType stepType in AllStepTypes())
        {
            IReadOnlyList<string> declared = stepType.Schema.Descriptors
                .Select(descriptor => descriptor.Key).ToArray();

            foreach (string key in stepType.NcExtraParameterKeys)
            {
                declared.Should().Contain(key, $"{stepType.Key} 声明要下发 {key}");
            }
        }
    }

    [Fact]
    public void The_extras_block_is_big_enough_for_every_step_slot()
    {
        using JsonDocument document = SampleTagMap();
        JsonElement extras = document.RootElement.GetProperty("tags")
            .EnumerateArray()
            .Single(tag => tag.GetProperty("key").GetString() == MachineTagKeys.JobStepExtra);
        JsonElement steps = document.RootElement.GetProperty("tags")
            .EnumerateArray()
            .Single(tag => tag.GetProperty("key").GetString() == MachineTagKeys.JobStepTypeCode);

        int stepSlots = steps.GetProperty("arrayLength").GetInt32();

        extras.GetProperty("arrayLength").GetInt32().Should()
            .Be(stepSlots * MachineTagKeys.JobStepExtraCount, "每道工序一段，段长是约定好的项数");
    }

    [Fact]
    public void No_two_tags_land_on_the_same_R_parameter()
    {
        // 加一个数组时最容易犯的错：挑了一个看着空的起点，其实压在别人身上。
        // 撞了不会报错，只会把别人的值悄悄改掉——所以让测试来数这件事。
        var occupant = new Dictionary<int, string>();
        var clashes = new List<string>();

        using JsonDocument document = SampleTagMap();
        foreach (JsonElement tag in document.RootElement.GetProperty("tags").EnumerateArray())
        {
            string key = tag.GetProperty("key").GetString()!;
            foreach (int register in RegistersOf(tag))
            {
                if (occupant.TryGetValue(register, out string? other))
                {
                    clashes.Add($"R[{register}]: {other} 与 {key}");
                }

                occupant[register] = key;
            }
        }

        clashes.Should().BeEmpty();
    }

    /// <summary>这个变量占用哪几个 R 号；不是 R 参数（PLC 位、OPC 节点）的返回空。</summary>
    private static IEnumerable<int> RegistersOf(JsonElement tag)
    {
        string address = tag.GetProperty("address").GetString() ?? string.Empty;

        if (tag.TryGetProperty("arrayLength", out JsonElement lengthElement)
            && tag.TryGetProperty("indexOffset", out JsonElement offsetElement))
        {
            if (!address.Contains("R[{index}]", StringComparison.Ordinal))
            {
                yield break;
            }

            int offset = offsetElement.GetInt32();
            for (int i = 0; i < lengthElement.GetInt32(); i++)
            {
                yield return offset + i;
            }

            yield break;
        }

        Match match = Regex.Match(address, @"R\[(\d+)\]");
        if (match.Success)
        {
            yield return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }
}
