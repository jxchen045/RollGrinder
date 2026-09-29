using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.App.Navigation;

/// <summary>区域菜单态下横键条上的一格。</summary>
/// <param name="Area">目的区域。</param>
/// <param name="SlotNumber">序号（1 起）：就是 F 键号。</param>
/// <param name="IsCurrent">是不是当前所在的区域。</param>
/// <param name="IsAvailable">现在进不进得去（离线、权限不够时照样占格，只是灰的）。</param>
public sealed record AreaSoftKey(AreaKey Area, int SlotNumber, bool IsCurrent, bool IsAvailable);

/// <summary>
/// 画面与区域的归属、区域的入口画面、区域菜单的排布（最终稿 4.1、D1）。
/// 纯逻辑，不引用 WPF，规则直接单测。
///
/// 约定：
/// 1. 8 个区域正好排满区域菜单的 8 个横键，F(n) = 第 n 个区域；
/// 2. 每个画面恰好属于一个区域；
/// 3. 机床区的入口随 NC 方式：AUTO 进自动磨削，其余（JOG、MDA、读不到）进手动磨削；
/// 4. 调试只在区域菜单里（不上左栏），要制造商权限。
/// </summary>
public static class AreaCatalog
{
    /// <summary>区域菜单的格数：横键条的 8 格。</summary>
    public const int MenuSlotCount = 8;

    /// <summary>区域在区域菜单里的顺序（最终稿 D1）。</summary>
    public static IReadOnlyList<AreaKey> MenuOrder { get; } = new[]
    {
        AreaKey.Machine,
        AreaKey.Profile,
        AreaKey.Steps,
        AreaKey.Library,
        AreaKey.Parameters,
        AreaKey.Records,
        AreaKey.Diagnostics,
        AreaKey.Commissioning,
    };

    /// <summary>画面属于哪个区域。</summary>
    public static AreaKey AreaOf(PageKey page) => page switch
    {
        PageKey.AutoGrinding or PageKey.ManualGrinding or PageKey.Manual or PageKey.Job => AreaKey.Machine,
        PageKey.Profile => AreaKey.Profile,
        PageKey.Steps => AreaKey.Steps,
        PageKey.Library => AreaKey.Library,
        PageKey.Parameters => AreaKey.Parameters,
        PageKey.Records => AreaKey.Records,
        PageKey.Diagnostics => AreaKey.Diagnostics,
        PageKey.Commissioning => AreaKey.Commissioning,
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Every page must belong to an area."),
    };

    /// <summary>区域里有哪些画面（入口画面在前）。</summary>
    public static IReadOnlyList<PageKey> PagesOf(AreaKey area) =>
        Enum.GetValues<PageKey>().Where(page => AreaOf(page) == area)
            .OrderBy(page => page == EntryPage(area, MachineMode.Jog) ? 0 : page == EntryPage(area, MachineMode.Auto) ? 1 : 2)
            .ThenBy(page => (int)page)
            .ToArray();

    /// <summary>进入一个区域时显示哪个画面。</summary>
    public static PageKey EntryPage(AreaKey area, MachineMode mode) => area switch
    {
        AreaKey.Machine => mode == MachineMode.Auto ? PageKey.AutoGrinding : PageKey.ManualGrinding,
        AreaKey.Profile => PageKey.Profile,
        AreaKey.Steps => PageKey.Steps,
        AreaKey.Library => PageKey.Library,
        AreaKey.Parameters => PageKey.Parameters,
        AreaKey.Records => PageKey.Records,
        AreaKey.Diagnostics => PageKey.Diagnostics,
        AreaKey.Commissioning => PageKey.Commissioning,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };

    /// <summary>
    /// 机床区的两个基本画面。NC 方式一变，停在基本画面上的外壳跟着换（手动磨削 ⇄ 自动磨削）；
    /// 停在手动动作页、作业向导这类画面上时不动——人正在那儿干活。
    /// </summary>
    public static bool IsModeBasePage(PageKey page) => page is PageKey.AutoGrinding or PageKey.ManualGrinding;

    /// <summary>区域名的资源键（区域方块、区域菜单、左栏共用）。</summary>
    public static string TitleKey(AreaKey area) => "Area_" + area;

    /// <summary>区域的图形符号（左栏、区域方块）。符号不是文字，不进资源文件。</summary>
    public static string Glyph(AreaKey area) => area switch
    {
        AreaKey.Machine => "⚙",
        AreaKey.Profile => "⌒",
        AreaKey.Steps => "≡",
        AreaKey.Library => "☰",
        AreaKey.Parameters => "▦",
        AreaKey.Records => "▤",
        AreaKey.Diagnostics => "⚠",
        AreaKey.Commissioning => "⚒",
        _ => "?",
    };

    /// <summary>只能从区域菜单进（不许放进左栏）：调试。</summary>
    public static bool IsMenuOnly(AreaKey area) => area == AreaKey.Commissioning;

    /// <summary>排出区域菜单的 8 格。</summary>
    /// <param name="areas">要列出的区域，按顺序（通常是 <see cref="MenuOrder"/> 里实际注册了的那些）。</param>
    /// <param name="currentArea">当前区域。</param>
    /// <param name="isAvailable">某区域现在进不进得去。进不去的照样列出来，只是标成不可用——键位不跳。</param>
    public static IReadOnlyList<AreaSoftKey> BuildMenu(
        IReadOnlyList<AreaKey> areas,
        AreaKey currentArea,
        Func<AreaKey, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(isAvailable);

        if (areas.Count > MenuSlotCount)
        {
            throw new InvalidOperationException(
                $"{areas.Count} areas do not fit the {MenuSlotCount} horizontal soft keys of the area menu.");
        }

        var keys = new List<AreaSoftKey>(areas.Count);
        for (int i = 0; i < areas.Count; i++)
        {
            AreaKey area = areas[i];
            keys.Add(new AreaSoftKey(area, i + 1, area == currentArea, isAvailable(area)));
        }

        return keys;
    }
}
