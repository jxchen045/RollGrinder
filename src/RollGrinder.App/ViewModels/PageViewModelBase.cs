using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Services.Alarms;

namespace RollGrinder.App.ViewModels;

/// <summary>顶栏上的一项上下文信息，例如"轧辊编号 BX-20916"。</summary>
/// <param name="LabelResourceKey">标签资源键。</param>
/// <param name="Value">取值（现场数据，不翻译）。</param>
/// <param name="IsMonospaced">是否用等宽字体显示（编号、位置值用）。</param>
public sealed record ContextItem(string LabelResourceKey, string Value, bool IsMonospaced = false);

/// <summary>
/// 一个一级页面。外壳负责画顶栏、页面菜单与底部功能条，
/// 页面负责给出标题、上下文与前 7 个功能键——第 8 个是导航槽，页面碰不到。
/// </summary>
public abstract partial class PageViewModelBase : ViewModelBase
{
    /// <summary>页面自己能占的功能键数量；第 8 个恒为导航槽。</summary>
    public const int PageFunctionKeyCount = 7;

    /// <summary>右侧竖向软键的格数。</summary>
    public const int VerticalKeyCount = SoftKeyMenu<FunctionKeyViewModel>.SlotCount;

    private readonly SoftKeyMenu<FunctionKeyViewModel> verticalMenu = new();

    protected PageViewModelBase(IAlarmSink alarms, IStringLocalizer localizer, INavigator navigator)
        : base(alarms)
    {
        Localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        RefreshVerticalKeys();
    }

    /// <summary>
    /// 取字用。公开而不是 protected：报表打印这类**排版**留在视图的代码后置里，
    /// 它得用与视图模型同一个取字器，否则同一张报表预览与打印可能是两种语言。
    /// </summary>
    public IStringLocalizer Localizer { get; }

    protected INavigator Navigator { get; }

    /// <summary>页面标识。</summary>
    public abstract PageKey Key { get; }

    /// <summary>页面标题的资源键。</summary>
    public abstract string TitleResourceKey { get; }

    /// <summary>页面标题。</summary>
    public string Title => Localizer[TitleResourceKey];

    /// <summary>页面菜单里这一页的悬停说明：一句话说明这页有什么，避免靠猜。</summary>
    public virtual string MenuHintResourceKey => TitleResourceKey;

    /// <summary>顶栏上显示的上下文。</summary>
    public ObservableCollection<ContextItem> ContextItems { get; } = new();

    /// <summary>本页的功能键，最多 7 个。</summary>
    public ObservableCollection<FunctionKeyViewModel> FunctionKeys { get; } = new();

    /// <summary>
    /// 右侧 8 个竖向软键（修改稿原则 1）：本页的编辑动作，带"▸"的打开一层子菜单，子菜单第 8 格是"返回"。
    /// 恒为 8 格，外壳照着画，空位是灰的。键盘上是 Shift+F1…F8。
    /// </summary>
    public ObservableCollection<FunctionKeyViewModel> VerticalKeys { get; } = new();

    /// <summary>当前子菜单的标题（例如"插入段"）；在根层为空。</summary>
    [ObservableProperty]
    private string verticalMenuTitle = string.Empty;

    /// <summary>本页是不是编辑页：自动循环运行期间要落只读锁。</summary>
    public virtual bool LocksDuringRun => false;

    /// <summary>
    /// 没有机床时这一页还用不用得了。
    ///
    /// 默认 false——一页要在离线模式下开放，得有人确认它真的不碰机床。
    /// 编程、辊形、记录、设置是 true：它们只和数据库与配置打交道。
    /// </summary>
    public virtual bool WorksOffline => false;

    /// <summary>有没有未保存的修改。脏页离开时外壳会拦一道。</summary>
    [ObservableProperty]
    private bool isDirty;

    /// <summary>当前是否只读（自动循环运行中）。</summary>
    [ObservableProperty]
    private bool isReadOnly;

    /// <summary>当前打开的二级子视图资源键；null 表示停在本页根部。</summary>
    [ObservableProperty]
    private string? activeSubViewKey;

    /// <summary>本页能不能就地保存。接上存储之前为 false，离开确认框就不会给出"保存并离开"。</summary>
    public virtual bool CanSave => false;

    /// <summary>保存本页的修改。返回 false 表示没保存成功，外壳会留在本页。</summary>
    public virtual Task<bool> SaveAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>丢掉未保存的修改（操作员在离开确认框里选了"放弃"）。</summary>
    public virtual void DiscardChanges() => IsDirty = false;

    /// <summary>
    /// 本页有没有开着一个要先答完的框（例如另存为的命名框）。开着的时候外壳不响应功能键，
    /// 免得框还没答完又按出别的动作。
    /// </summary>
    public virtual bool HasModalPrompt => false;

    /// <summary>Esc：本页有开着的框就收掉并返回 true；没有返回 false，外壳再按"退一级"处理。</summary>
    public virtual bool TryDismissPrompt() => false;

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
        IsReadOnly = LocksDuringRun && machineRunning;
    }

    /// <summary>登记功能键。多于 7 个直接抛——设计稿就是 8 格，超了应该在编译期之外立刻暴露。</summary>
    protected void SetFunctionKeys(IEnumerable<FunctionKeyViewModel> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        FunctionKeys.Clear();
        foreach (FunctionKeyViewModel key in keys)
        {
            FunctionKeys.Add(key);
        }

        if (FunctionKeys.Count > PageFunctionKeyCount)
        {
            throw new InvalidOperationException(
                $"Page {Key} declares {FunctionKeys.Count} function keys; at most {PageFunctionKeyCount} fit beside the navigation key.");
        }

        ApplyKeyEnablement();
    }

    /// <summary>登记竖向软键的根层（最多 8 个）。子菜单一并收掉。</summary>
    protected void SetVerticalKeys(IEnumerable<FunctionKeyViewModel> keys)
    {
        this.verticalMenu.SetRoot(keys);
        RefreshVerticalKeys();
    }

    /// <summary>打开一层竖键子菜单（最多 7 项，第 8 格外壳自动放"返回"）。</summary>
    protected void OpenVerticalMenu(string titleResourceKey, IEnumerable<FunctionKeyViewModel> items)
    {
        this.verticalMenu.Open(titleResourceKey, items);
        RefreshVerticalKeys();
    }

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

    /// <summary>竖键退一层。在根层返回 false（Esc 就交给外壳去退页面）。</summary>
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

    private void RefreshVerticalKeys()
    {
        VerticalKeys.Clear();
        int index = 0;
        foreach (SoftKeySlot<FunctionKeyViewModel> slot in this.verticalMenu.Slots)
        {
            index++;
            FunctionKeyViewModel key = slot.IsBack
                ? new FunctionKeyViewModel("Vk_Back", new RelayCommand(() => CloseVerticalMenu()), Localizer, FunctionKeyKind.Navigation)
                : slot.Key ?? new FunctionKeyViewModel("Fn_Empty", new RelayCommand(() => { }, () => false), Localizer)
                {
                    IsEnabled = false,
                };
            key.ShortcutText = Localizer.Format("Vk_ShortcutFormat", index);
            VerticalKeys.Add(key);
        }

        VerticalMenuTitle = this.verticalMenu.TitleKey is { } titleKey ? Localizer[titleKey] : string.Empty;
        ApplyKeyEnablement();
    }

    /// <summary>标记本页有未保存的修改。</summary>
    protected void MarkDirty() => IsDirty = true;

    /// <summary>标记本页已保存/已同步。</summary>
    protected void MarkClean() => IsDirty = false;

    partial void OnIsReadOnlyChanged(bool value) => ApplyKeyEnablement();

    private void ApplyKeyEnablement()
    {
        foreach (FunctionKeyViewModel key in FunctionKeys)
        {
            key.IsEnabled = !(key.RequiresEditable && IsReadOnly);
        }

        foreach (FunctionKeyViewModel key in VerticalKeys)
        {
            if (key.LabelResourceKey != "Fn_Empty")
            {
                key.IsEnabled = !(key.RequiresEditable && IsReadOnly);
            }
        }
    }
}
