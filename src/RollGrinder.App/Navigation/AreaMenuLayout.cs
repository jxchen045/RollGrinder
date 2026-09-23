using System;
using System.Collections.Generic;

namespace RollGrinder.App.Navigation;

/// <summary>页面菜单态下软键条上的一格。</summary>
/// <param name="Area">目的区域。</param>
/// <param name="ShortcutNumber">序号（1 起）：既是 F 键位置，也是 Ctrl+n 的 n。</param>
/// <param name="IsCurrent">是不是当前所在的区域。</param>
/// <param name="IsAvailable">现在进不进得去（离线时有几页进不去）。</param>
public sealed record AreaSoftKey(PageKey Area, int ShortcutNumber, bool IsCurrent, bool IsAvailable);

/// <summary>
/// 页面菜单的排布。页面菜单不是浮层，而是把底部软键条**原地**换成区域键：
/// 前 7 格是区域（F1–F7，同时也是 Ctrl+1…7），第 8 格是"取消"。页面照常显示、照常可用。
///
/// 做成纯逻辑，是为了把两条约定钉进测试：
/// 1. 第 n 格 = F(n) = Ctrl+n，三者永远一致；
/// 2. 区域最多 7 个——第 8 格属于导航槽，加第 8 个区域必须先想清楚翻页方案，不能悄悄挤掉"取消"。
/// </summary>
public static class AreaMenuLayout
{
    /// <summary>软键条上能放区域的格数（第 8 格是导航槽）。</summary>
    public const int AreaSlotCount = 7;

    /// <summary>区域在菜单里的顺序：常用的在前，也就是左边、F 键号小的一头。</summary>
    public static IReadOnlyList<PageKey> DefaultOrder { get; } = new[]
    {
        PageKey.AutoGrinding,
        PageKey.Steps,
        PageKey.Profile,
        PageKey.Manual,
        PageKey.Records,
        PageKey.Diagnostics,
        PageKey.Settings,
    };

    /// <summary>排出菜单态的区域格。</summary>
    /// <param name="areas">要列出的区域，按顺序（通常是 <see cref="DefaultOrder"/> 里实际注册了的那些）。</param>
    /// <param name="currentArea">当前区域。</param>
    /// <param name="isAvailable">某区域现在进不进得去。进不去的照样列出来，只是标成不可用。</param>
    public static IReadOnlyList<AreaSoftKey> Build(
        IReadOnlyList<PageKey> areas,
        PageKey currentArea,
        Func<PageKey, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(isAvailable);

        if (areas.Count > AreaSlotCount)
        {
            throw new InvalidOperationException(
                $"{areas.Count} areas do not fit the soft-key row; at most {AreaSlotCount} fit beside the navigation key.");
        }

        var keys = new List<AreaSoftKey>(areas.Count);
        for (int i = 0; i < areas.Count; i++)
        {
            PageKey area = areas[i];
            keys.Add(new AreaSoftKey(area, i + 1, area == currentArea, isAvailable(area)));
        }

        return keys;
    }
}
