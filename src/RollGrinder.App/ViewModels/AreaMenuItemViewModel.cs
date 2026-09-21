using System;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 区域菜单里的一格。六格对应主页与五个子页面，
/// 每格除了页名还给一句话说明，避免操作员靠页名猜里面有什么。
/// </summary>
public sealed partial class AreaMenuItemViewModel : ObservableObject
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

    /// <summary>键盘/小键盘上的序号（1–6）。</summary>
    public int ShortcutNumber { get; }

    public string TitleResourceKey { get; }

    public string HintResourceKey { get; }

    public ICommand Command { get; }

    public string Title => this.localizer[TitleResourceKey];

    public string Hint => this.localizer[HintResourceKey];

    /// <summary>序号文本。</summary>
    public string ShortcutText => ShortcutNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 这一格现在进不进得去。离线模式下自动磨削、手动、诊断进不去——
    /// 但**照样列出来并说明原因**，藏起来只会让人以为软件少做。
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>进不去时给的说明。</summary>
    public string UnavailableHint => IsAvailable ? string.Empty : this.localizer["Nav_OfflineUnavailable"];

    /// <summary>是不是当前所在的区域：高亮它，点它只是关菜单。</summary>
    [ObservableProperty]
    private bool isCurrent;
}
