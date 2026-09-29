using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 左栏的一个快捷入口（最终稿 D2）：图形符号 + 两个字，80×88，Ctrl+n。
/// 当前所在的区域（及功能组）青底；进不去的留在原位变灰，点它时对话行说明原因。
/// </summary>
public sealed partial class QuickBarItemViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;

    public QuickBarItemViewModel(QuickBarEntry entry, int shortcutNumber, ICommand command, IStringLocalizer localizer)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        ShortcutNumber = shortcutNumber;
        Command = command ?? throw new ArgumentNullException(nameof(command));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public QuickBarEntry Entry { get; }

    /// <summary>Ctrl+n 的 n。</summary>
    public int ShortcutNumber { get; }

    public ICommand Command { get; }

    public string Glyph => Entry.Glyph;

    public string Label => this.localizer[Entry.LabelResourceKey];

    /// <summary>快捷键提示（Ctrl+n）。</summary>
    public string ShortcutText => this.localizer.Format("Nav_ShortcutFormat", ShortcutNumber);

    /// <summary>当前就在这里。</summary>
    [ObservableProperty]
    private bool isCurrent;

    /// <summary>现在进得去；进不去时 <see cref="UnavailableReason"/> 说明原因。</summary>
    [ObservableProperty]
    private bool isAvailable = true;

    /// <summary>进不去的原因（已本地化）。</summary>
    [ObservableProperty]
    private string? unavailableReason;
}
