using System;
using System.Windows.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 页面菜单里的一个区域。菜单态时外壳按它在底部软键条上排出一个区域键；
/// Ctrl+1…7 也按它的序号直达。每格除了页名还带一句话说明（悬停提示），
/// 避免操作员靠页名猜里面有什么。
/// </summary>
public sealed class AreaMenuItemViewModel
{
    private readonly IStringLocalizer localizer;

    public AreaMenuItemViewModel(
        PageKey key,
        int shortcutNumber,
        string titleResourceKey,
        string hintResourceKey,
        ICommand command,
        IStringLocalizer localizer,
        bool isAvailable = true)
    {
        Key = key;
        IsAvailable = isAvailable;
        ShortcutNumber = shortcutNumber;
        TitleResourceKey = titleResourceKey ?? throw new ArgumentNullException(nameof(titleResourceKey));
        HintResourceKey = hintResourceKey ?? throw new ArgumentNullException(nameof(hintResourceKey));
        Command = command ?? throw new ArgumentNullException(nameof(command));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>目的区域。</summary>
    public PageKey Key { get; }

    /// <summary>序号（1–7）：菜单态下的 F 键位置，也是 Ctrl+n 的 n。</summary>
    public int ShortcutNumber { get; }

    public string TitleResourceKey { get; }

    public string HintResourceKey { get; }

    public ICommand Command { get; }

    public string Hint => this.localizer[HintResourceKey];

    /// <summary>
    /// 这一格现在进不进得去。离线模式下自动磨削、手动、诊断进不去——
    /// 但**照样列出来并说明原因**，藏起来只会让人以为软件少做。
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>进不去时给的说明。</summary>
    public string UnavailableHint => IsAvailable ? string.Empty : this.localizer["Nav_OfflineUnavailable"];
}
