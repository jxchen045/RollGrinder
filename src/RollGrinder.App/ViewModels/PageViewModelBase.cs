using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>顶栏上的一项上下文信息，例如"轧辊编号 BX-20916"。</summary>
/// <param name="LabelResourceKey">标签资源键。</param>
/// <param name="Value">取值（现场数据，不翻译）。</param>
/// <param name="IsMonospaced">是否用等宽字体显示（编号、位置值用）。</param>
public sealed record ContextItem(string LabelResourceKey, string Value, bool IsMonospaced = false);

/// <summary>
/// 一个画面（最终稿 4.1、4.4）。外壳负责画左栏、标题行、路径条、对话行与两排软键；
/// 画面给出标题、上下文、横键（功能组，一页 8 个，多了分页）与竖键（这一组里的操作）。
///
/// 竖键第 7 / 8 格的规则由这里统一执行：有待确认的事时是"✕ 取消 / ✓ 确认"（全局，<see cref="ShellInteraction"/>），
/// 否则画面有待提交的改动时是它给的一对（<see cref="SetCommitPair"/>），否则是菜单自己的（返回、翻页或普通键）。
/// 按不了的键留在原位变灰，原因写进 <see cref="FunctionKeyViewModel.DisabledReason"/>，外壳在对话行说出来。
/// </summary>
public abstract partial class PageViewModelBase : ViewModelBase
{
    /// <summary>横键一页的格数。</summary>
    public const int HorizontalKeyCount = SoftKeyRow<FunctionKeyViewModel>.SlotCount;

    /// <summary>右侧竖向软键的格数。</summary>
    public const int VerticalKeyCount = SoftKeyMenu<FunctionKeyViewModel>.SlotCount;

    private readonly SoftKeyMenu<FunctionKeyViewModel> verticalMenu = new();
    private FunctionKeyViewModel? commitCancel;
    private FunctionKeyViewModel? commitConfirm;

    protected PageViewModelBase(IAlarmSink alarms, IStringLocalizer localizer, INavigator navigator, ShellInteraction interaction)
        : base(alarms)
    {
        Localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        Interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        Interaction.Confirmations.Changed += (_, _) => RefreshVerticalKeys();
        RefreshVerticalKeys();
    }

    /// <summary>
    /// 取字用。公开而不是 protected：报表打印这类**排版**留在视图的代码后置里，
    /// 它得用与视图模型同一个取字器，否则同一张报表预览与打印可能是两种语言。
    /// </summary>
    public IStringLocalizer Localizer { get; }

    protected INavigator Navigator { get; }

    /// <summary>确认与对话行。</summary>
    protected ShellInteraction Interaction { get; }

    /// <summary>画面标识。</summary>
    public abstract PageKey Key { get; }

    /// <summary>画面所在的区域。</summary>
    public AreaKey Area => AreaCatalog.AreaOf(Key);

    /// <summary>画面标题的资源键。</summary>
    public abstract string TitleResourceKey { get; }

    /// <summary>画面标题。</summary>
    public string Title => Localizer[TitleResourceKey];

    /// <summary>黄色帮助（最终稿 F9）里本画面的条目资源键前缀；null 表示本画面没有专门的帮助，用目录。</summary>
    public virtual string? HelpTopicKey => null;

    /// <summary>路径条 / 窗口标题上的上下文（例如"轧辊 R-2026-001"）。</summary>
    public ObservableCollection<ContextItem> ContextItems { get; } = new();

    /// <summary>本画面的横键（功能组）。空位用 <see cref="FunctionKeyViewModel.Empty"/>；多于 8 个外壳分页。</summary>
    public ObservableCollection<FunctionKeyViewModel> FunctionKeys { get; } = new();

    /// <summary>右侧 8 个竖向软键，恒为 8 格，外壳照着画。键盘上是 Shift+F1…F8。</summary>
    public ObservableCollection<FunctionKeyViewModel> VerticalKeys { get; } = new();

    /// <summary>当前子菜单的标题（例如"插入段"）；在根层为空。</summary>
    [ObservableProperty]
    private string verticalMenuTitle = string.Empty;

    /// <summary>
    /// 外壳不画路径条与通道行，工作区顶上去用。自动磨削页这样做（方案 F）：
    /// 方式、通道状态、回参考点已在本页上排的位置块里，路径条上又没有返回与未保存标记可显示。
    /// </summary>
    public virtual bool HidesPathRows => false;

    /// <summary>本页是不是编辑页：自动循环运行期间要落只读锁。</summary>
    public virtual bool LocksDuringRun => false;

    /// <summary>
    /// 没有机床时这一页还用不用得了。
    ///
    /// 默认 false——一页要在离线模式下开放，得有人确认它真的不碰机床。
    /// </summary>
    public virtual bool WorksOffline => false;

    /// <summary>有没有未保存的修改。脏页离开时外壳会拦一道。</summary>
    [ObservableProperty]
    private bool isDirty;

    /// <summary>当前是否只读：自动循环运行中（<see cref="IsRunLocked"/>），或当前权限改不了本页（<see cref="IsRoleLocked"/>）。</summary>
    [ObservableProperty]
    private bool isReadOnly;

    /// <summary>自动循环运行中，本页落了只读锁。</summary>
    [ObservableProperty]
    private bool isRunLocked;

    /// <summary>当前登录的权限改不了本页（只能看）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleLockText))]
    private bool isRoleLocked;

    /// <summary>急停中：会让机床动的键一律变灰（最终稿 4.7）。</summary>
    [ObservableProperty]
    private bool isEmergencyStopped;

    /// <summary>改本页的内容要哪项权限；null 表示本页没有要按权限锁的编辑内容。</summary>
    public virtual Permission? EditPermission => null;

    /// <summary>路径条上的"只读"标记：要哪一级才能改。</summary>
    public string RoleLockText => EditPermission is { } permission
        ? Localizer.Format("Shell_ReadOnlyRoleFormat", Localizer["Role_" + PermissionPolicy.MinimumRole(permission)])
        : string.Empty;

    // 外壳登录后推进来的权限判断。外壳推之前（单元测试里直接造页面）按"都有"算。
    private Func<Permission, bool> grants = _ => true;

    /// <summary>当前登录有没有这项权限。</summary>
    public bool Can(Permission permission) => this.grants(permission);

    /// <summary>当前打开的子功能资源键；null 表示停在画面根部。</summary>
    [ObservableProperty]
    private string? activeSubViewKey;

    /// <summary>本页能不能就地保存。接上存储之前为 false，离开确认框就不会给出"保存并离开"。</summary>
    public virtual bool CanSave => false;

    /// <summary>保存本页的修改。返回 false 表示没保存成功，外壳会留在本页。</summary>
    public virtual Task<bool> SaveAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>丢掉未保存的修改（操作员在离开确认框里选了"放弃"）。</summary>
    public virtual void DiscardChanges() => IsDirty = false;

    /// <summary>
    /// 本页有没有开着一个要先答完的框（例如另存为的命名框）。开着的时候外壳不响应横键，
    /// 免得框还没答完又按出别的动作。
    /// </summary>
    public virtual bool HasModalPrompt => false;

    /// <summary>Esc：本页有开着的框就收掉并返回 true；没有返回 false，外壳再按"返回"处理。</summary>
    public virtual bool TryDismissPrompt() => false;

    /// <summary>
    /// 打开一个功能组（对应本画面的一个横键，例如参数区的"砂轮"）。左栏的"砂轮"入口用它。
    /// 不认识的组返回 false。
    /// </summary>
    public virtual bool ShowGroup(string groupKey) => false;

    /// <summary>本画面有没有可以复制 / 粘贴的列表（辊形的段表、工艺程序的工序序列）。</summary>
    public virtual bool SupportsClipboard => false;

    /// <summary>复制 / 剪切 / 粘贴选中的一条（外壳已挡掉只读时的剪切与粘贴）。</summary>
    public virtual void Clipboard(ClipboardAction action)
    {
    }

    /// <summary>本页只读时说明为什么（运行中锁定、权限不够）；可编辑时为 null。</summary>
    public string? ReadOnlyReason =>
        IsRunLocked ? Localizer["Key_RunLocked"] : IsRoleLocked && EditPermission is { } edit ? NeedsRole(edit) : null;

    /// <summary>功能键块"↶ 撤销"：本画面有没有可撤销的改动。</summary>
    public virtual bool CanUndo => false;

    /// <summary>功能键块"↷ 重做"。</summary>
    public virtual bool CanRedo => false;

    /// <summary>撤销一步。</summary>
    public virtual void Undo()
    {
    }

    /// <summary>重做一步。</summary>
    public virtual void Redo()
    {
    }

    /// <summary>切到本页时调用。</summary>
    public virtual void OnActivated()
    {
    }

    /// <summary>切走本页时调用。草稿留在内存里——误触回来数据还在。</summary>
    public virtual void OnDeactivated()
    {
    }

    /// <summary>界面节拍（5–10 Hz）。只有当前页会收到。</summary>
    public virtual void OnTick(DateTimeOffset nowUtc)
    {
    }

    /// <summary>外壳按机床状态刷新只读锁，并同步功能键的可用性。</summary>
    public void ApplyRunState(bool machineRunning)
    {
        IsRunLocked = LocksDuringRun && machineRunning;
        IsReadOnly = IsRunLocked || IsRoleLocked;
    }

    /// <summary>外壳在登录、签退、改权限时推进来：本页按它决定能不能改、哪些键可按。</summary>
    public void ApplyAccess(Func<Permission, bool> can)
    {
        this.grants = can ?? throw new ArgumentNullException(nameof(can));
        IsRoleLocked = EditPermission is { } permission && !can(permission);
        IsReadOnly = IsRunLocked || IsRoleLocked;
        ApplyKeyEnablement();
        OnAccessChanged();
    }

    /// <summary>外壳按急停状态推进来。</summary>
    public void ApplyEmergencyStop(bool stopped) => IsEmergencyStopped = stopped;

    /// <summary>权限变了。页面里有按权限显示的东西（例如只有制造商能改的格子）就在这里刷新。</summary>
    protected virtual void OnAccessChanged()
    {
    }

    /// <summary>登记横键（功能组）。null 是占位空键（最终稿里"空"的那格）。</summary>
    protected void SetFunctionKeys(IEnumerable<FunctionKeyViewModel?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        FunctionKeys.Clear();
        foreach (FunctionKeyViewModel? key in keys)
        {
            FunctionKeys.Add(key ?? FunctionKeyViewModel.Empty(Localizer));
        }

        ApplyKeyEnablement();
    }

    /// <summary>把某个横键标成"正显示着"（青底），其余取消。null 全部取消。</summary>
    protected void MarkActiveFunctionKey(FunctionKeyViewModel? active)
    {
        foreach (FunctionKeyViewModel key in FunctionKeys)
        {
            key.IsActive = ReferenceEquals(key, active);
        }
    }

    /// <summary>
    /// 登记竖向软键的根层。null 是占位空键；多于 8 个分页（每页 7 个，第 8 格翻页）。子菜单一并收掉。
    /// </summary>
    protected void SetVerticalKeys(IEnumerable<FunctionKeyViewModel?> keys)
    {
        this.verticalMenu.SetRoot(keys);
        RefreshVerticalKeys();
    }

    /// <summary>打开一层竖键子菜单（最多 7 项，第 8 格外壳自动放"返回"）。</summary>
    protected void OpenVerticalMenu(string titleResourceKey, IEnumerable<FunctionKeyViewModel?> items)
    {
        this.verticalMenu.Open(titleResourceKey, items);
        RefreshVerticalKeys();
    }

    /// <summary>
    /// 画面有待提交的改动（参数矩阵改了、标定值改了……）：竖键 7 / 8 换成这一对（"✕ 放弃改动 / ✓ 保存"）。
    /// 传 null 收回。待确认的事优先于它。
    /// </summary>
    protected void SetCommitPair(FunctionKeyViewModel? cancel, FunctionKeyViewModel? confirm)
    {
        if (ReferenceEquals(this.commitCancel, cancel) && ReferenceEquals(this.commitConfirm, confirm))
        {
            return;
        }

        this.commitCancel = cancel;
        this.commitConfirm = confirm;
        RefreshVerticalKeys();
    }

    /// <summary>
    /// 页面上的命名框（另存为、复制、改名）：开着时竖键 7 / 8 是"✕ 取消 / ✓ 确认"（最终稿 5.14 弹出框），
    /// 收起后换回页面原来的一对。
    /// </summary>
    protected void AttachPrompt(NamePromptViewModel prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var cancel = new FunctionKeyViewModel("Vk_Cancel", prompt.CancelCommand, Localizer, FunctionKeyKind.Cancel);
        var confirm = new FunctionKeyViewModel("Vk_Confirm", prompt.ConfirmCommand, Localizer, FunctionKeyKind.Confirm);
        (FunctionKeyViewModel? Cancel, FunctionKeyViewModel? Confirm) saved = (null, null);
        prompt.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(NamePromptViewModel.IsOpen))
            {
                return;
            }

            if (prompt.IsOpen)
            {
                saved = (this.commitCancel, this.commitConfirm);
                SetCommitPair(cancel, confirm);
            }
            else
            {
                SetCommitPair(saved.Cancel, saved.Confirm);
            }
        };
    }

    /// <summary>路径条上的问题计数（最终稿：辊形、工艺的错误数写在路径条上）；0 时不显示。</summary>
    protected void SetIssueCount(int errors)
    {
        ContextItem? existing = ContextItems.FirstOrDefault(item => item.LabelResourceKey == IssueContextKey);
        if (existing is not null)
        {
            if (errors > 0 && existing.Value == errors.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return;
            }

            ContextItems.Remove(existing);
        }

        if (errors > 0)
        {
            ContextItems.Add(new ContextItem(IssueContextKey, errors.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    private const string IssueContextKey = "Context_Issues";

    /// <summary>
    /// 子菜单里的一项：按下去做事，然后收回根层（选好了就不必再按"返回"）。
    /// <paramref name="canChoose"/> 为假时这一项是灰的（例如对称编辑时的 CVC）。
    /// </summary>
    protected FunctionKeyViewModel MenuChoice(
        string labelResourceKey,
        Action onChosen,
        bool requiresEditable = true,
        string? labelArgument = null,
        Func<bool>? canChoose = null)
    {
        ArgumentNullException.ThrowIfNull(onChosen);
        return new FunctionKeyViewModel(
            labelResourceKey,
            new RelayCommand(
                () =>
                {
                    onChosen();
                    ResetVerticalMenu();
                },
                canChoose ?? (() => true)),
            Localizer,
            requiresEditable: requiresEditable)
        {
            LabelArgument = labelArgument,
        };
    }

    /// <summary>竖键退一层。在根层返回 false（Esc 就交给外壳去退）。</summary>
    public bool CloseVerticalMenu()
    {
        if (!this.verticalMenu.Back())
        {
            return false;
        }

        RefreshVerticalKeys();
        return true;
    }

    /// <summary>竖键收回根层。切走本页时外壳会调，免得回来时还停在一个过期的子菜单里。</summary>
    public void ResetVerticalMenu()
    {
        if (this.verticalMenu.Depth == 0)
        {
            return;
        }

        this.verticalMenu.CloseAll();
        RefreshVerticalKeys();
    }

    /// <summary>
    /// 问一句再做（最终稿 D5）：对话行写出问题，竖键 7 / 8 换成取消 / 确认，5 秒不答自动取消。
    /// </summary>
    protected void Ask(string questionResourceKey, Func<Task> action, params object?[] arguments) =>
        Interaction.Ask(Localizer.Format(questionResourceKey, arguments), action);

    /// <summary>同步动作的便捷重载。</summary>
    protected void Ask(string questionResourceKey, Action action, params object?[] arguments) =>
        Interaction.Ask(Localizer.Format(questionResourceKey, arguments), action);

    /// <summary>对话行：一条消息（3 秒后消失）。</summary>
    protected void Say(string resourceKey, params object?[] arguments) =>
        Interaction.Say(Localizer.Format(resourceKey, arguments));

    /// <summary>给一个键设阻断原因（null 解除），并立刻重算这个键。</summary>
    protected void Block(FunctionKeyViewModel key, string? reason)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Blocker = reason;
        Gate(key);
    }

    private void RefreshVerticalKeys()
    {
        IReadOnlyList<SoftKeySlot<FunctionKeyViewModel>> slots = this.verticalMenu.Slots;
        if (Interaction.Confirmations.Pending is { } pending)
        {
            slots = SoftKeyMenu<FunctionKeyViewModel>.WithCommitPair(
                slots,
                new FunctionKeyViewModel(pending.CancelLabelKey, new RelayCommand(() => Interaction.Confirmations.Cancel()), Localizer, FunctionKeyKind.Cancel),
                new FunctionKeyViewModel(pending.ConfirmLabelKey, new AsyncRelayCommand(() => Interaction.Confirmations.ConfirmAsync()), Localizer, FunctionKeyKind.Confirm));
        }
        else
        {
            slots = SoftKeyMenu<FunctionKeyViewModel>.WithCommitPair(slots, this.commitCancel, this.commitConfirm);
        }

        VerticalKeys.Clear();
        int index = 0;
        foreach (SoftKeySlot<FunctionKeyViewModel> slot in slots)
        {
            index++;
            FunctionKeyViewModel key = slot.Kind switch
            {
                SoftKeySlotKind.Back => new FunctionKeyViewModel("Vk_Back", new RelayCommand(() => CloseVerticalMenu()), Localizer, FunctionKeyKind.Navigation),
                SoftKeySlotKind.NextPage => new FunctionKeyViewModel("Vk_NextPage", new RelayCommand(NextVerticalPage), Localizer, FunctionKeyKind.Navigation),
                SoftKeySlotKind.FirstPage => new FunctionKeyViewModel("Vk_FirstPage", new RelayCommand(NextVerticalPage), Localizer, FunctionKeyKind.Navigation),
                _ => slot.Key ?? FunctionKeyViewModel.Empty(Localizer),
            };
            key.ShortcutText = key.IsPlaceholder ? null : Localizer.Format("Vk_ShortcutFormat", index);
            VerticalKeys.Add(key);
        }

        VerticalMenuTitle = this.verticalMenu.TitleKey is { } titleKey ? Localizer[titleKey] : string.Empty;
        ApplyKeyEnablement();
    }

    private void NextVerticalPage()
    {
        if (this.verticalMenu.NextPage())
        {
            RefreshVerticalKeys();
        }
    }

    /// <summary>标记本页有未保存的修改。</summary>
    protected void MarkDirty() => IsDirty = true;

    /// <summary>标记本页已保存/已同步。</summary>
    protected void MarkClean() => IsDirty = false;

    partial void OnIsReadOnlyChanged(bool value) => ApplyKeyEnablement();

    partial void OnIsEmergencyStoppedChanged(bool value) => ApplyKeyEnablement();

    private void ApplyKeyEnablement()
    {
        foreach (FunctionKeyViewModel key in FunctionKeys)
        {
            Gate(key);
        }

        foreach (FunctionKeyViewModel key in VerticalKeys)
        {
            Gate(key);
        }

        if (this.commitCancel is not null)
        {
            Gate(this.commitCancel);
        }

        if (this.commitConfirm is not null)
        {
            Gate(this.commitConfirm);
        }
    }

    private void Gate(FunctionKeyViewModel key)
    {
        if (key.IsPlaceholder)
        {
            key.IsEnabled = false;
            key.DisabledReason = null;
            return;
        }

        key.DisabledReason = LockReason(key);
        key.IsEnabled = key.DisabledReason is null;
    }

    /// <summary>
    /// 键为什么按不了（null = 能按）。先看页面给的阻断（缺标签、前置条件），再看急停，
    /// 然后是权限：键自己点名了权限的按那项权限（运行锁照样管改数据的键）；没点名的，改数据的键跟着本页的只读走。
    /// </summary>
    private string? LockReason(FunctionKeyViewModel key)
    {
        if (key.Blocker is { Length: > 0 } blocker)
        {
            return blocker;
        }

        if (IsEmergencyStopped && key.IsMachineCommand)
        {
            return Localizer["Key_EmergencyStop"];
        }

        if (key.RequiredPermission is { } permission)
        {
            if (!Can(permission))
            {
                return NeedsRole(permission);
            }

            return key.RequiresEditable && IsRunLocked ? Localizer["Key_RunLocked"] : null;
        }

        if (!key.RequiresEditable)
        {
            return null;
        }

        if (IsRunLocked)
        {
            return Localizer["Key_RunLocked"];
        }

        return IsRoleLocked && EditPermission is { } edit ? NeedsRole(edit) : null;
    }

    private string NeedsRole(Permission permission) =>
        Localizer.Format("Key_NeedsRoleFormat", Localizer["Role_" + PermissionPolicy.MinimumRole(permission)]);

    /// <summary>页面里按权限变化的键（例如补偿子视图里只有制造商能按的"保存"）改完权限后重算一遍。</summary>
    protected void RefreshKeyEnablement() => ApplyKeyEnablement();
}
