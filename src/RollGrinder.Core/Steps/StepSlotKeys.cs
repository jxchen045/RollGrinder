using System;
using System.Collections.Generic;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 工序槽：实机把工艺分成固定的 5 个槽，外加不占槽的那几种。
///
/// 我们内部仍然是"任意排序的若干种工序类型"——能力更强，可以插短行程、
/// 插光磨、插修整。槽只作**界面上的分组**：操作工在实机上按这 5 个槽想事情，
/// 选工序时照这个分组看，肌肉记忆不用变，而编排自由度一点没少。
///
/// 槽不参与任何计算，也不下发给 NC——NC 拿到的是工序类型码与顺序。
/// </summary>
public static class StepSlotKeys
{
    /// <summary>粗磨槽。短行程也归这里——它是粗磨之前把辊面走顺的那一下。</summary>
    public const string Rough = "Rough";

    /// <summary>半精磨槽。</summary>
    public const string SemiFinish = "SemiFinish";

    /// <summary>精磨槽。光磨归这里——它是精磨的收尾，不是另一档工艺。</summary>
    public const string Finish = "Finish";

    /// <summary>超精磨槽。</summary>
    public const string SuperFinish = "SuperFinish";

    /// <summary>倒角或修整砂轮槽。实机上这两件事共用第 5 槽。</summary>
    public const string ChamferOrDress = "ChamferOrDress";

    /// <summary>
    /// 不占槽：探伤、测量、标记与暂停。
    /// 实机上探伤就是独立于 5 个槽的一道工序，测量与标记本来也不是"磨"。
    /// </summary>
    public const string Independent = "Independent";

    /// <summary>
    /// 槽的呈现顺序，就是实机屏幕上的顺序。
    /// 界面按它排组，不按字母序——按字母序的话粗磨会排到精磨后面。
    /// </summary>
    public static IReadOnlyList<string> Ordered { get; } = new[]
    {
        Rough, SemiFinish, Finish, SuperFinish, ChamferOrDress, Independent,
    };

    /// <summary>界面文案的资源键。</summary>
    public static string ResourceKeyOf(string slotKey) => "StepSlot_" + slotKey;

    /// <summary>这个槽键是不是内置的那六个之一。</summary>
    public static bool IsKnown(string slotKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(slotKey);

        foreach (string known in Ordered)
        {
            if (string.Equals(known, slotKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>排序用的序号；不认识的槽排在最后。</summary>
    public static int OrderOf(string slotKey)
    {
        for (int i = 0; i < Ordered.Count; i++)
        {
            if (string.Equals(Ordered[i], slotKey, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return Ordered.Count;
    }
}
