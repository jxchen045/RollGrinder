using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 工艺程序的首尾（修改稿 5.3）："开始"固定在第一道，"结束"固定在最后一道，各一个。
/// 以前两者都能随便插、随便挪，甚至不插，NC 那头的开始 / 结束动作就跟着乱了。
/// </summary>
public static class ProgramFrame
{
    /// <summary>这道工序是不是固定的首尾（不能删、不能挪、不能在它前后插别的越过它）。</summary>
    public static bool IsFixed(string stepTypeKey) =>
        stepTypeKey is StepTypeKeys.Start or StepTypeKeys.End;

    /// <summary>
    /// 把一串工序整理成"开始 … 结束"：缺了就补（默认参数），多了只留一个，挪到首尾；
    /// 中间各道的相对次序不变。已有的开始 / 结束保留自己的参数。返回的次序从 1 重新编号。
    /// </summary>
    public static IReadOnlyList<GrindingJobStep> Normalize(
        IEnumerable<GrindingJobStep> steps, GrindingStepTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(registry);

        List<GrindingJobStep> list = steps.OrderBy(step => step.Order).ToList();
        GrindingJobStep start = list.FirstOrDefault(step => step.StepTypeKey == StepTypeKeys.Start)
            ?? new GrindingJobStep(0, StepTypeKeys.Start, registry.Get(StepTypeKeys.Start).Schema.CreateDefaults());
        GrindingJobStep end = list.FirstOrDefault(step => step.StepTypeKey == StepTypeKeys.End)
            ?? new GrindingJobStep(0, StepTypeKeys.End, registry.Get(StepTypeKeys.End).Schema.CreateDefaults());

        IEnumerable<GrindingJobStep> middle = list.Where(step => !IsFixed(step.StepTypeKey));
        return new[] { start }.Concat(middle).Append(end)
            .Select((step, index) => step with { Order = index + 1 })
            .ToArray();
    }

    /// <summary>已经是"开始 … 结束"的样子了吗（首尾各一个，中间没有）。</summary>
    public static bool IsNormalized(IReadOnlyList<GrindingJobStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.Count >= 2
            && steps[0].StepTypeKey == StepTypeKeys.Start
            && steps[^1].StepTypeKey == StepTypeKeys.End
            && steps.Skip(1).Take(steps.Count - 2).All(step => !IsFixed(step.StepTypeKey));
    }
}
