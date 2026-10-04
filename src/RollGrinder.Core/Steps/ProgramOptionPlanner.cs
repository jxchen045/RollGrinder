using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 程序步骤开关在一份作业里怎么用："本次取舍"——这一支辊开磨前，哪些 NC 子程序走、哪些跳过。
///
/// 它不属于工艺编程：工序序列定的是"有哪几道、每道怎么磨"，开关定的是"这一次走不走"。
/// 所以开关只在作业核对页出现，并且只列这一份作业**用得上**的：
/// <list type="bullet">
/// <item>挂着工序的四个（磨前测量、磨后测量、在线测量、涡流探伤）：程序里有那道工序才列，默认开——
/// 程序里排了那道工序，本意就是要做；这一次不做再关掉。</item>
/// <item>挂着装置的（安装误差、轴线前馈、U1 调平、自动趋近）：机床有那个装置才列，默认按目录。</item>
/// <item>打印两项由上位机做，总是列，默认关。</item>
/// </list>
/// 用不上的一律按"关"下发，NC 不会收到一个开着、却没有对应工序或装置的开关。
/// </summary>
public static class ProgramOptionPlanner
{
    /// <summary>这个开关是不是挂在某道工序上（程序里没有那道工序就不列）。</summary>
    public static bool IsStepLinked(string key) =>
        key is ProgramOptionKeys.PreGrindMeasure or ProgramOptionKeys.PostGrindMeasure
            or ProgramOptionKeys.InProcessMeasure or ProgramOptionKeys.EddyCurrentTest;

    /// <summary>
    /// 开关挂着的那道工序的次序；没挂工序或程序里没有那道工序返回 null。
    /// 磨前测量 = 第一道磨削工序之前的测量；磨后测量 = 最后一道磨削工序之后的测量；
    /// 在线测量 = 第一道开着在线测量的磨削工序；涡流探伤 = 第一道涡流探伤工序。
    /// </summary>
    public static int? LinkedStepOrder(string key, IReadOnlyList<GrindingJobStep> steps, GrindingStepTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(registry);

        GrindingJobStep[] ordered = steps.OrderBy(step => step.Order).ToArray();
        bool IsGrinding(GrindingJobStep step) =>
            registry.TryGet(step.StepTypeKey, out IGrindingStepType? type) && type is TraverseGrindingStepType;

        GrindingJobStep? firstGrinding = ordered.FirstOrDefault(IsGrinding);
        GrindingJobStep? lastGrinding = ordered.LastOrDefault(IsGrinding);
        GrindingJobStep? found = key switch
        {
            ProgramOptionKeys.PreGrindMeasure when firstGrinding is not null =>
                ordered.FirstOrDefault(step => step.StepTypeKey == StepTypeKeys.Measure && step.Order < firstGrinding.Order),
            ProgramOptionKeys.PostGrindMeasure when lastGrinding is not null =>
                ordered.LastOrDefault(step => step.StepTypeKey == StepTypeKeys.Measure && step.Order > lastGrinding.Order),
            ProgramOptionKeys.InProcessMeasure => ordered.FirstOrDefault(step => IsGrinding(step)
                && step.Parameters.TryGet(StepParameterKeys.InProcessMeasurement, out ParameterValue? value)
                && value is { Kind: ParameterValueKind.Boolean, Boolean: true }),
            ProgramOptionKeys.EddyCurrentTest => ordered.FirstOrDefault(step => step.StepTypeKey == StepTypeKeys.EddyCurrent),
            _ => null,
        };
        return found?.Order;
    }

    /// <summary>这一份作业用不用得上这个开关（机床做得了，挂工序的还要程序里有那道工序）。</summary>
    public static bool AppliesTo(
        ProgramOptionDescriptor option, IReadOnlyList<GrindingJobStep> steps, MachineCapability capability, GrindingStepTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(capability);

        return capability.Supports(option)
            && (!IsStepLinked(option.Key) || LinkedStepOrder(option.Key, steps, registry) is not null);
    }

    /// <summary>用得上的开关的默认值：挂工序的开，其余按目录。用不上的不在里面。</summary>
    public static bool DefaultFor(ProgramOptionDescriptor option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return IsStepLinked(option.Key) || option.DefaultEnabled;
    }

    /// <summary>
    /// 整理成下发用的一整套开关：十个键都有；用不上的一律关；用得上的取 <paramref name="chosen"/> 里的值，没选过的取默认。
    /// </summary>
    public static ParameterSet Normalize(
        ParameterSet chosen, IReadOnlyList<GrindingJobStep> steps, MachineCapability capability, GrindingStepTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(chosen);

        return new ParameterSet(ProgramOptionCatalog.All.Select(option =>
        {
            bool on = AppliesTo(option, steps, capability, registry)
                && (chosen.TryGet(option.Key, out ParameterValue? value) && value is { Kind: ParameterValueKind.Boolean }
                    ? value.Boolean
                    : DefaultFor(option));
            return new KeyValuePair<string, ParameterValue>(option.Key, ParameterValue.FromBoolean(on));
        }));
    }
}
