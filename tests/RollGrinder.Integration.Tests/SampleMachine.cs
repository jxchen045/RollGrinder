using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RollGrinder.Core.Steps;

namespace RollGrinder.Integration.Tests;

/// <summary>样例 machine.json 里和工序类型有关的部分，给按反射枚举工序类型的测试用。</summary>
internal static class SampleMachine
{
    /// <summary>样例里登记的辅助动作（动作键 → 动作号）。</summary>
    public static IReadOnlyList<AuxiliaryAction> AuxiliaryActions()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "config", "machine.sample.json")));
        return document.RootElement.GetProperty("auxiliaryActionCodes").EnumerateObject()
            .Select(property => new AuxiliaryAction(property.Name, property.Value.GetInt32()))
            .ToArray();
    }

    /// <summary>
    /// Core 里的全部工序类型。反射枚举而不是手写名单——手写的名单会忘记更新。
    /// 辅助动作的选项来自 machine.json，按样例里登记的动作构造。
    /// </summary>
    public static IGrindingStepType[] AllStepTypes() =>
        typeof(IGrindingStepType).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsPublic: true }
                && typeof(IGrindingStepType).IsAssignableFrom(type))
            .Select(type => type == typeof(AuxiliaryActionStepType)
                ? new AuxiliaryActionStepType(AuxiliaryActions())
                : (IGrindingStepType)Activator.CreateInstance(type)!)
            .OrderBy(stepType => stepType.Key, StringComparer.Ordinal)
            .ToArray();
}
