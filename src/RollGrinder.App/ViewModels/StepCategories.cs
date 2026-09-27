using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Steps;

namespace RollGrinder.App.ViewModels;

/// <summary>"插入工序 ▸"里的一个类别。</summary>
/// <param name="LabelResourceKey">类别键上的字（也是它子菜单的标题）。</param>
/// <param name="StepTypeKeys">这一类里的工序类型，按显示次序。</param>
public sealed record StepCategory(string LabelResourceKey, IReadOnlyList<string> StepTypeKeys);

/// <summary>
/// 插工序时的分类（修改稿 5.3）：磨削、测量两类各开一层子菜单；砂轮修整、倒角、辅助动作、暂停一个工序一个键。
/// 工序槽（粗 / 半精 / 精 / 超精 / 倒角或修整）是 NC 那头的分组，这里是操作员找工序的分组，两者不必一样。
/// 以后新注册的工序类型没在这里列出，就归到"其他"，不会凭空消失。
/// </summary>
public static class StepCategories
{
    private static readonly (string Label, string[] Types)[] Known =
    {
        ("Vk_CatGrinding", new[]
        {
            StepTypeKeys.Rough, StepTypeKeys.SemiFinish, StepTypeKeys.Finish,
            StepTypeKeys.SparkOut, StepTypeKeys.ShortStroke, StepTypeKeys.Polish,
        }),
        ("Vk_CatMeasuring", new[] { StepTypeKeys.Measure, StepTypeKeys.Roundness, StepTypeKeys.EddyCurrent }),
        ("Vk_CatDress", new[] { StepTypeKeys.WheelDress }),
        ("Vk_CatChamfer", new[] { StepTypeKeys.Chamfer }),
        ("Vk_CatAuxiliary", new[] { StepTypeKeys.Auxiliary }),
        ("Vk_CatPause", new[] { StepTypeKeys.Pause }),
    };

    /// <summary>按已注册的工序类型排出类别；开始 / 结束固定在首尾，不在里面。空类别不出现。</summary>
    public static IReadOnlyList<StepCategory> For(IEnumerable<string> registeredStepTypeKeys)
    {
        ArgumentNullException.ThrowIfNull(registeredStepTypeKeys);
        var registered = registeredStepTypeKeys.Where(key => !ProgramFrame.IsFixed(key)).ToList();
        var result = new List<StepCategory>();
        foreach ((string label, string[] types) in Known)
        {
            string[] present = types.Where(registered.Contains).ToArray();
            if (present.Length > 0)
            {
                result.Add(new StepCategory(label, present));
            }
        }

        string[] other = registered.Where(key => !Known.Any(known => known.Types.Contains(key))).ToArray();
        if (other.Length > 0)
        {
            result.Add(new StepCategory("Vk_CatOther", other));
        }

        return result;
    }
}
