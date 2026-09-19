using System.Collections.Generic;

namespace RollGrinder.Core.Steps;

/// <summary>工序类型注册表。</summary>
public sealed class GrindingStepTypeRegistry : KeyedRegistry<IGrindingStepType>
{
    public GrindingStepTypeRegistry(IEnumerable<IGrindingStepType> stepTypes)
        : base(stepTypes, stepType => stepType.Key)
    {
    }
}
