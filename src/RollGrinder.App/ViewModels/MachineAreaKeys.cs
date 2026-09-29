using System;
using System.Collections.Generic;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 机床区 JOG 方式下各画面共用的横键（最终稿 5.1）：
/// 手动磨削 · 测量臂 · 尾架 · 头架拨盘 · 托瓦 · 测量对中 · 辅助循环 ▸ · 作业。
/// 手动磨削、手动动作页、作业向导三个画面用同一排横键，键位不随画面跳动；当前那组青底。
/// </summary>
public static class MachineAreaKeys
{
    /// <summary>手动磨削（基本画面）。</summary>
    public const string Grinding = "grinding";

    /// <summary>手动动作页：测量臂。</summary>
    public const string MeasuringArm = "measuringArm";

    /// <summary>手动动作页：尾架（尾架前后、套筒伸缩）。</summary>
    public const string Tailstock = "tailstock";

    /// <summary>手动动作页：头架拨盘。</summary>
    public const string Driver = "driver";

    /// <summary>手动动作页：托瓦（两侧升降、U 轴归零）。</summary>
    public const string SteadyRest = "steadyRest";

    /// <summary>测量对中。</summary>
    public const string Centring = "measureCentring";

    /// <summary>辅助循环 ▸。</summary>
    public const string Cycles = "cycles";

    /// <summary>作业向导。</summary>
    public const string Job = "job";

    /// <summary>横键的顺序：组键 → 标签资源键。</summary>
    public static IReadOnlyList<(string Group, string LabelKey)> Order { get; } = new[]
    {
        (Grinding, "Fn_ManualGrinding"),
        (MeasuringArm, "ManualPage_measuringArm"),
        (Tailstock, "ManualPage_tailstock"),
        (Driver, "ManualPage_driver"),
        (SteadyRest, "ManualPage_steadyRest"),
        (Centring, "ManualPage_measureCentring"),
        (Cycles, "Fn_AuxCycles"),
        (Job, "Fn_Job"),
    };

    /// <summary>这一组在哪个画面上。</summary>
    public static PageKey PageOf(string group) => group switch
    {
        Grinding => PageKey.ManualGrinding,
        Job => PageKey.Job,
        _ => PageKey.Manual,
    };

    /// <summary>做出这一排横键，并把 <paramref name="activeGroup"/> 标成青底。</summary>
    public static IReadOnlyList<FunctionKeyViewModel> Create(INavigator navigator, IStringLocalizer localizer, string activeGroup)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(localizer);

        var keys = new List<FunctionKeyViewModel>(Order.Count);
        foreach ((string group, string labelKey) in Order)
        {
            string target = group;
            keys.Add(new FunctionKeyViewModel(
                labelKey,
                new CommunityToolkit.Mvvm.Input.RelayCommand(() => navigator.GoTo(PageOf(target), target)),
                localizer)
            {
                IsActive = string.Equals(group, activeGroup, StringComparison.Ordinal),
            });
        }

        return keys;
    }

    /// <summary>把一排横键里当前那组标成青底。</summary>
    public static void MarkActive(IEnumerable<FunctionKeyViewModel> keys, string activeGroup)
    {
        ArgumentNullException.ThrowIfNull(keys);
        int index = 0;
        foreach (FunctionKeyViewModel key in keys)
        {
            key.IsActive = index < Order.Count && string.Equals(Order[index].Group, activeGroup, StringComparison.Ordinal);
            index++;
        }
    }
}
