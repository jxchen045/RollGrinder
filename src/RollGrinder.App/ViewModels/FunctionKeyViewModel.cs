using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;

namespace RollGrinder.App.ViewModels;

/// <summary>功能键的外观：决定用哪种按钮样式。</summary>
public enum FunctionKeyKind
{
    /// <summary>普通键：白底描边。</summary>
    Normal = 0,

    /// <summary>主操作：实心强调色。</summary>
    Primary = 1,

    /// <summary>开始干活：绿色。</summary>
    Start = 2,

    /// <summary>危险动作：红色。</summary>
    Danger = 3,
}

/// <summary>
/// 底部功能条上的一个键。每页固定 8 个，最后一个恒为"返回"。
/// </summary>
public sealed partial class FunctionKeyViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;

    public FunctionKeyViewModel(
        string labelResourceKey,
        ICommand command,
        IStringLocalizer localizer,
        FunctionKeyKind kind = FunctionKeyKind.Normal)
    {
        LabelResourceKey = labelResourceKey ?? throw new ArgumentNullException(nameof(labelResourceKey));
        Command = command ?? throw new ArgumentNullException(nameof(command));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Kind = kind;
    }

    /// <summary>用一个还没实现的动作占位：键照常显示，按下去只是提示尚未接通。</summary>
    public static FunctionKeyViewModel Placeholder(
        string labelResourceKey,
        IStringLocalizer localizer,
        Action onPressed,
        FunctionKeyKind kind = FunctionKeyKind.Normal) =>
        new(labelResourceKey, new RelayCommand(onPressed), localizer, kind);

    public string LabelResourceKey { get; }

    public string Label => this.localizer[LabelResourceKey];

    public ICommand Command { get; }

    public FunctionKeyKind Kind { get; }
}
