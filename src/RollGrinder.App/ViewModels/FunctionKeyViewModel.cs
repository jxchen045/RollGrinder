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

    /// <summary>导航槽（第 8 键）：与功能键区分开，避免"以为按的是功能"。</summary>
    Navigation = 4,
}

/// <summary>
/// 底部功能条上的一个键。每页固定 8 个：前 7 个由页面给出，第 8 个恒为导航槽（外壳给出）。
/// </summary>
public sealed partial class FunctionKeyViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;

    public FunctionKeyViewModel(
        string labelResourceKey,
        ICommand command,
        IStringLocalizer localizer,
        FunctionKeyKind kind = FunctionKeyKind.Normal,
        bool requiresEditable = false)
    {
        this.labelResourceKey = labelResourceKey ?? throw new ArgumentNullException(nameof(labelResourceKey));
        Command = command ?? throw new ArgumentNullException(nameof(command));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Kind = kind;
        RequiresEditable = requiresEditable;
    }

    /// <summary>
    /// 用一个普通动作做一个键。
    ///
    /// （曾经叫 Placeholder：那时候还有一批键没接通，按下去只是提示。
    /// 现在一个都不剩了，留着那个名字只会让人以为这些键是假的。）
    /// </summary>
    public static FunctionKeyViewModel ForAction(
        string labelResourceKey,
        IStringLocalizer localizer,
        Action onPressed,
        FunctionKeyKind kind = FunctionKeyKind.Normal,
        bool requiresEditable = false) =>
        new(labelResourceKey, new RelayCommand(onPressed), localizer, kind, requiresEditable);

    public ICommand Command { get; }

    public FunctionKeyKind Kind { get; }

    /// <summary>这个键会改数据；自动循环运行期间要锁掉。</summary>
    public bool RequiresEditable { get; }

    /// <summary>标签。带 {0} 的键用 <see cref="LabelArgument"/> 填空。</summary>
    public string Label => LabelArgument is null
        ? this.localizer[LabelResourceKey]
        : this.localizer.Format(LabelResourceKey, LabelArgument);

    /// <summary>键可不可按。禁用时保留按钮位置，只是变灰——不让键位跳动。</summary>
    [ObservableProperty]
    private bool isEnabled = true;

    /// <summary>标签资源键。导航槽会随位置改写它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string labelResourceKey;

    /// <summary>带 {0} 的标签用它填空（导航槽填目的页名）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string? labelArgument;
}
