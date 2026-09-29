using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>软键的外观：决定用哪种按钮样式（最终稿 4.3、4.4）。</summary>
public enum FunctionKeyKind
{
    /// <summary>普通键：浅灰底。</summary>
    Normal = 0,

    /// <summary>主操作：实心强调色（弹出框里的"确定"这类）。</summary>
    Primary = 1,

    /// <summary>开始干活：绿色。</summary>
    Start = 2,

    /// <summary>危险动作：红色。</summary>
    Danger = 3,

    /// <summary>"« 返回"、"≡▸ / ≡◂"：外壳放的导航键。</summary>
    Navigation = 4,

    /// <summary>区域菜单态下横键条上的区域键。</summary>
    AreaMenu = 5,

    /// <summary>区域菜单态下当前所在的区域。</summary>
    AreaMenuCurrent = 6,

    /// <summary>竖键第 7 格"✕ 取消 / 放弃改动"：深红底白字。</summary>
    Cancel = 7,

    /// <summary>竖键第 8 格"✓ 确认 / 保存 / 下发改动"：深绿底白字。</summary>
    Confirm = 8,

    /// <summary>帮助模式下的竖键：黄底。</summary>
    Help = 9,
}

/// <summary>
/// 一个软键（横键或竖键）。
///
/// 按不了的键**留在原位变灰**，点它时对话行说明原因（最终稿 4.4）——所以按钮本身永远接得住点击，
/// "能不能按"由 <see cref="IsUsable"/> 决定，原因在 <see cref="DisabledReason"/>。
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
        Command.CanExecuteChanged += (_, _) => OnPropertyChanged(nameof(IsUsable));
    }

    /// <summary>用一个普通动作做一个键。</summary>
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

    /// <summary>按这个键要的权限；null 表示跟着本页的编辑权限走（见 PageViewModelBase.EditPermission）。</summary>
    public Permission? RequiredPermission { get; init; }

    /// <summary>这个键会让机床动（或下发给机床）：急停时一律变灰（最终稿 4.7）。</summary>
    public bool IsMachineCommand { get; init; }

    /// <summary>
    /// 命令暂时执行不了（例如还没选中一行）时对话行说的话（资源键）；null 用通用的"现在不能用"。
    /// </summary>
    public string? PreconditionResourceKey { get; init; }

    /// <summary>标签。带 {0} 的键用 <see cref="LabelArgument"/> 填空。</summary>
    public string Label => LabelArgument is null
        ? this.localizer[LabelResourceKey]
        : this.localizer.Format(LabelResourceKey, LabelArgument);

    /// <summary>键可不可按（权限、运行锁、急停、页面给的阻断）。灰了也留在原位，不让键位跳动。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsable))]
    private bool isEnabled = true;

    /// <summary>按下去真会做事：既没被锁，命令眼下也执行得了。外壳按它决定"执行"还是"说明原因"。</summary>
    public bool IsUsable => IsEnabled && Command.CanExecute(null);

    /// <summary>
    /// 页面给的阻断原因（已本地化）：缺标签、前置条件不满足……非空时键是灰的。
    /// 由页面设置；外壳算锁时把它和权限、运行锁合在一起。
    /// </summary>
    [ObservableProperty]
    private string? blocker;

    /// <summary>为什么按不了（已本地化）；能按时为 null。</summary>
    [ObservableProperty]
    private string? disabledReason;

    /// <summary>横键：这个功能组正显示着（按下后青底，最终稿 4.4）。</summary>
    [ObservableProperty]
    private bool isActive;

    /// <summary>标签资源键。成对键（启动 ⇄ 停止）会改写它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string labelResourceKey;

    /// <summary>带 {0} 的标签用它填空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string? labelArgument;

    /// <summary>印在标签下面的快捷键（如 Shift+F3）；null 不显示。让快捷键不用翻手册就能被发现。</summary>
    [ObservableProperty]
    private string? shortcutText;

    /// <summary>这一格是占位空键（画成空键，按了没反应）。</summary>
    public bool IsPlaceholder => LabelResourceKey == EmptyLabelKey;

    /// <summary>空键的资源键。</summary>
    public const string EmptyLabelKey = "Fn_Empty";

    /// <summary>按不了时对话行该说什么：锁的原因优先，其次是命令的前置条件。</summary>
    public string ReasonText => DisabledReason
        ?? this.localizer[PreconditionResourceKey ?? "Key_NotNow"];

    /// <summary>做一个占位空键。</summary>
    public static FunctionKeyViewModel Empty(IStringLocalizer localizer) =>
        new(EmptyLabelKey, new RelayCommand(() => { }, () => false), localizer) { IsEnabled = false };
}
