using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;
using RollGrinder.Services.Session;
using Serilog;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 界面外壳（最终稿 4.1）：左栏快捷入口、标题行（报警、用户、连接、时间）、右上区域 / 方式方块、
/// 程序路径条（其他区域是当前窗口的青色标题）、通道行（只在机床区）、功能键块、对话行、
/// 横键条（功能组，右端"&gt;"翻页）、竖键条（本组的操作），以及登录、用户管理、离开确认、数字键盘、帮助。
///
/// 画面切换的规则只有一处，见 <see cref="NavigationModel"/>：区域之间是平的，
/// "返回"只退一级，脏页离开要经确认，自动循环运行期间编辑页落只读锁。
/// 按不了的键不吞掉点击：对话行说明原因（没权限 / 运行中 / 缺映射 / 前置条件 / 急停）。
///
/// 刷新节拍由窗口的定时器驱动（hmi.json 的 uiRefreshHz，5–10 Hz），只有当前页会收到 OnTick。
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly IAlarmLog alarmLog;
    private readonly IMachineMonitor monitor;
    private readonly IUserSession userSession;
    private readonly IUserDirectory userDirectory;
    private readonly IStringLocalizer localizer;
    private readonly ShellInteraction interaction;
    private readonly Navigator navigator;
    private readonly NavigationModel model;
    private readonly Dictionary<PageKey, PageViewModelBase> pages;
    private readonly SoftKeyRow<FunctionKeyViewModel> horizontalRow = new();
    private readonly IAppOptions options;
    private string culture;

    private PageKey? pendingPage;
    private bool pendingIsTaskReturn;
    private PageKey? pendingReturnTo;
    private string? pendingGroup;

    public ShellViewModel(
        IEnumerable<PageViewModelBase> pages,
        Navigator navigator,
        IAlarmLog alarmLog,
        IMachineMonitor monitor,
        IUserSession userSession,
        IUserDirectory userDirectory,
        IAppOptions options,
        HmiSettings settings,
        MachineDescription machine,
        ShellInteraction interaction,
        IStringLocalizer localizer)
        : base(alarmLog)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(machine);
        this.navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        this.alarmLog = alarmLog ?? throw new ArgumentNullException(nameof(alarmLog));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.userSession = userSession ?? throw new ArgumentNullException(nameof(userSession));
        this.userDirectory = userDirectory ?? throw new ArgumentNullException(nameof(userDirectory));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        this.interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));

        this.pages = pages.ToDictionary(page => page.Key);
        this.options = options;
        this.culture = settings.Culture;
        IsOffline = options.IsOffline;
        MachineName = machine.DisplayName;
        ScreenshotDirectory = System.IO.Path.Combine(options.DataDirectory, "screenshots");
        RefreshInterval = TimeSpan.FromSeconds(1.0 / settings.UiRefreshHz);
        Keypad = new NumericKeypadViewModel(interaction, localizer);
        Help = new HelpViewModel(localizer);
        Help.PropertyChanged += OnHelpChanged;

        this.newUserRole = settings.DefaultRole;
        foreach (UserRole role in AssignableRoles)
        {
            UserRole chosen = role;
            RoleChoices.Add(new ParameterChoiceViewModel(
                role.ToString(), localizer["Role_" + role], new RelayCommand(() => NewUserRole = chosen))
            {
                IsSelected = role == this.newUserRole,
            });
        }

        // 离线模式下机床区用不了（没有机床），开机画面改成第一个离线能用的区域。
        this.model = new NavigationModel(IsOffline ? FirstOfflinePage() : null);

        IReadOnlyList<QuickBarEntry> entries = QuickBarCatalog.Resolve(machine.QuickBar, out IReadOnlyList<string> rejected);
        foreach (string id in rejected)
        {
            Log.Warning("machine.json quickBar entry {Entry} is unknown, duplicated or beyond the seven slots; ignored", id);
        }

        foreach (QuickBarEntry entry in entries.Where(e => this.pages.ContainsKey(AreaCatalog.EntryPage(e.Area, MachineMode.Jog))))
        {
            QuickBarEntry target = entry;
            QuickBarItems.Add(new QuickBarItemViewModel(
                entry, QuickBarItems.Count + 1, new RelayCommand(() => PressQuickBar(target)), localizer));
        }

        this.interaction.DialogLine.Changed += (_, _) => OnUiThread(SyncDialogLine);
        this.navigator.Requested += (_, request) => Handle(request);

        this.currentPage = this.pages[this.model.CurrentPage];
        this.currentPage.PropertyChanged += OnCurrentPagePropertyChanged;
        this.currentPage.FunctionKeys.CollectionChanged += OnCurrentPageKeysChanged;
        this.userSession.SessionChanged += (_, _) => OnUiThread(ApplyAccess);
        ApplyAccess();
        RebuildHorizontalKeys();
        SyncNavigation();
        this.currentPage.OnActivated();
    }

    /// <summary>界面刷新周期。</summary>
    public TimeSpan RefreshInterval { get; }

    /// <summary>截屏存到哪里（数据目录下的 screenshots）。</summary>
    public string ScreenshotDirectory { get; }

    /// <summary>
    /// 离线模式：没有机床。标题行标出来，免得有人对着一台"连不上"的机床查半天线路。
    /// </summary>
    public bool IsOffline { get; }

    /// <summary>左栏（最多 7 个，Ctrl+1…7）。</summary>
    public ObservableCollection<QuickBarItemViewModel> QuickBarItems { get; } = new();

    /// <summary>横键条实际渲染的 8 格：当前画面的功能组（分页），或区域菜单的 8 个区域。</summary>
    public ObservableCollection<FunctionKeyViewModel> HorizontalKeys { get; } = new();

    /// <summary>数字键盘。</summary>
    public NumericKeypadViewModel Keypad { get; }

    /// <summary>黄色帮助。</summary>
    public HelpViewModel Help { get; }

    /// <summary>竖键条实际渲染的 8 格：帮助模式下是帮助的键，否则是当前画面的。</summary>
    public IReadOnlyList<FunctionKeyViewModel> VerticalKeys => Help.IsOpen ? Help.Keys : CurrentPage.VerticalKeys;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerticalKeys))]
    [NotifyPropertyChangedFor(nameof(IsMachineArea))]
    private PageViewModelBase currentPage;

    /// <summary>区域菜单是否展开：横键条原地换成 8 个区域（对齐 Operate 的 MENU SELECT），不是浮层。</summary>
    [ObservableProperty]
    private bool isAreaMenuOpen;

    /// <summary>横键不止一页时右端的页码，例如"1/2"；只有一页时为空。</summary>
    [ObservableProperty]
    private string horizontalPageText = string.Empty;

    /// <summary>横键有下一页（"&gt;"亮着）。</summary>
    [ObservableProperty]
    private bool hasMoreHorizontalKeys;

    /// <summary>当前在机床区：显示灰色程序路径条和通道行；其他区域显示青色的当前窗口标题。</summary>
    public bool IsMachineArea => CurrentPage.Area == AreaKey.Machine;

    /// <summary>右上区域方块的符号与名字。</summary>
    [ObservableProperty]
    private string areaTileGlyph = string.Empty;

    [ObservableProperty]
    private string areaTileText = string.Empty;

    /// <summary>路径条 / 窗口标题：画面、子功能与上下文。</summary>
    [ObservableProperty]
    private string pathText = string.Empty;

    /// <summary>路径条左端"« xxx"：有可退的（子功能、任务返回点）时出现。</summary>
    [ObservableProperty]
    private string backText = string.Empty;

    /// <summary>对话行。</summary>
    [ObservableProperty]
    private string dialogText = string.Empty;

    [ObservableProperty]
    private DialogLineKind dialogKind;

    /// <summary>离开确认框是否展开。</summary>
    [ObservableProperty]
    private bool isLeaveConfirmOpen;

    /// <summary>离开确认框里显示的页名。</summary>
    [ObservableProperty]
    private string leaveConfirmPageTitle = string.Empty;

    /// <summary>离开确认框要不要给"保存并离开"：本页接上存储之后才给。</summary>
    [ObservableProperty]
    private bool leaveConfirmCanSave;

    /// <summary>自动循环是否还挂着程序：挂着就锁编辑页。</summary>
    [ObservableProperty]
    private bool isMachineRunning;

    /// <summary>
    /// 有浮层挡着时，底下的画面与软键不接受点击。数字键盘与帮助不算：它们停靠在旁边，表单照样能填。
    /// </summary>
    public bool IsOverlayOpen => IsLeaveConfirmOpen || IsSignInOpen || IsUserAdminOpen || IsUserMenuOpen;

    partial void OnIsLeaveConfirmOpenChanged(bool value) => OnPropertyChanged(nameof(IsOverlayOpen));

    partial void OnIsSignInOpenChanged(bool value) => OnPropertyChanged(nameof(IsOverlayOpen));

    partial void OnIsUserAdminOpenChanged(bool value) => OnPropertyChanged(nameof(IsOverlayOpen));

    partial void OnIsUserMenuOpenChanged(bool value) => OnPropertyChanged(nameof(IsOverlayOpen));

    partial void OnCurrentPageChanged(PageViewModelBase? oldValue, PageViewModelBase newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnCurrentPagePropertyChanged;
            oldValue.FunctionKeys.CollectionChanged -= OnCurrentPageKeysChanged;
        }

        newValue.PropertyChanged += OnCurrentPagePropertyChanged;
        newValue.FunctionKeys.CollectionChanged += OnCurrentPageKeysChanged;
    }

    private void OnCurrentPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModelBase.ActiveSubViewKey))
        {
            SyncNavigation();
        }
    }

    private void OnCurrentPageKeysChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!this.model.IsAreaMenuOpen)
        {
            RebuildHorizontalKeys();
        }
    }

    private void OnHelpChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HelpViewModel.IsOpen))
        {
            OnPropertyChanged(nameof(VerticalKeys));
        }
    }

    /// <summary>登录、签退、改权限后，把"当前能做什么"推给每一页（权限表见 PermissionPolicy）。</summary>
    private void ApplyAccess()
    {
        foreach (PageViewModelBase page in this.pages.Values)
        {
            page.ApplyAccess(this.userSession.Can);
        }

        OnPropertyChanged(nameof(CanManageUsers));
        RefreshQuickBar();
        if (this.model.IsAreaMenuOpen)
        {
            RebuildHorizontalKeys();
        }
    }

    /// <summary>离线时第一个能进的画面，兼作开机画面。</summary>
    private PageKey FirstOfflinePage()
    {
        foreach (AreaKey area in AreaCatalog.MenuOrder)
        {
            PageKey entry = AreaCatalog.EntryPage(area, MachineMode.Jog);
            if (this.pages.TryGetValue(entry, out PageViewModelBase? page) && page.WorksOffline)
            {
                return entry;
            }
        }

        return NavigationModel.DefaultHomePage;
    }

    // ── 软键 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 按下一个软键（点屏、F 键、Shift+F 键都走这里）：能按就执行；按不了就在对话行说原因——
    /// 灰键不吞点击，否则人会以为屏坏了（最终稿 4.4）。
    /// </summary>
    [RelayCommand]
    private void PressKey(FunctionKeyViewModel? key)
    {
        if (key is null || IsOverlayOpen || key.IsPlaceholder)
        {
            return;
        }

        if (!key.IsUsable)
        {
            this.interaction.Refuse(key.ReasonText);
            return;
        }

        key.Command.Execute(null);
    }

    /// <summary>键盘 F1–F8：横键第 n 个（n 从 0 起）。本页开着命名框之类时不透传。</summary>
    public void PressHorizontalKey(int index)
    {
        if (CurrentPage.HasModalPrompt || index < 0 || index >= HorizontalKeys.Count)
        {
            return;
        }

        PressKey(HorizontalKeys[index]);
    }

    /// <summary>键盘 Shift+F1–F8：竖键第 n 个。</summary>
    public void PressVerticalKey(int index)
    {
        IReadOnlyList<FunctionKeyViewModel> keys = VerticalKeys;
        if (index < 0 || index >= keys.Count)
        {
            return;
        }

        // 命名框开着时只有它的"✕ 取消 / ✓ 确认"（竖键 7 / 8）能按，别的动作等框答完。
        if (CurrentPage.HasModalPrompt && !Help.IsOpen && keys[index].Kind is not (FunctionKeyKind.Cancel or FunctionKeyKind.Confirm))
        {
            return;
        }

        PressKey(keys[index]);
    }

    /// <summary>横键条右端的"&gt;"：翻到下一页功能组。</summary>
    [RelayCommand]
    private void NextHorizontalPage()
    {
        if (!this.model.IsAreaMenuOpen && this.horizontalRow.NextPage())
        {
            SyncHorizontalSlots();
        }
    }

    /// <summary>
    /// 重建横键条：区域菜单开着就是 8 个区域，否则是当前画面的功能组（一页 8 个，分页）。
    /// </summary>
    private void RebuildHorizontalKeys()
    {
        if (this.model.IsAreaMenuOpen)
        {
            HorizontalKeys.Clear();
            foreach (FunctionKeyViewModel key in BuildAreaKeys())
            {
                HorizontalKeys.Add(key);
            }

            HorizontalPageText = string.Empty;
            HasMoreHorizontalKeys = false;
            return;
        }

        this.horizontalRow.Set(CurrentPage.FunctionKeys.Select(key => key.IsPlaceholder ? null : key));
        SyncHorizontalSlots();
    }

    private void SyncHorizontalSlots()
    {
        HorizontalKeys.Clear();
        int index = 0;
        foreach (FunctionKeyViewModel? slot in this.horizontalRow.Slots)
        {
            index++;
            FunctionKeyViewModel key = slot ?? FunctionKeyViewModel.Empty(this.localizer);
            key.ShortcutText = key.IsPlaceholder ? null : this.localizer.Format("Fn_ShortcutFormat", index);
            HorizontalKeys.Add(key);
        }

        HasMoreHorizontalKeys = this.horizontalRow.PageCount > 1;
        HorizontalPageText = HasMoreHorizontalKeys
            ? this.localizer.Format("Fn_PageFormat", this.horizontalRow.PageIndex + 1, this.horizontalRow.PageCount)
            : string.Empty;
    }

    /// <summary>区域菜单态的 8 个区域键：F(n) = 第 n 个区域；进不去的留在原位变灰，按了说原因。</summary>
    private IEnumerable<FunctionKeyViewModel> BuildAreaKeys()
    {
        List<AreaKey> areas = AreaCatalog.MenuOrder
            .Where(area => this.pages.ContainsKey(AreaCatalog.EntryPage(area, MachineMode.Jog)))
            .ToList();
        IReadOnlyList<AreaSoftKey> layout = AreaCatalog.BuildMenu(areas, this.model.CurrentArea, area => AreaUnavailableReason(area) is null);

        foreach (AreaSoftKey slot in layout)
        {
            AreaKey area = slot.Area;
            var key = new FunctionKeyViewModel(
                AreaCatalog.TitleKey(area),
                new RelayCommand(() => ChooseArea(area)),
                this.localizer,
                slot.IsCurrent ? FunctionKeyKind.AreaMenuCurrent : FunctionKeyKind.AreaMenu)
            {
                ShortcutText = this.localizer.Format("Fn_ShortcutFormat", slot.SlotNumber),
            };
            key.DisabledReason = AreaUnavailableReason(area);
            key.IsEnabled = key.DisabledReason is null;
            yield return key;
        }
    }

    /// <summary>某个区域现在为什么进不去；进得去为 null。</summary>
    private string? AreaUnavailableReason(AreaKey area)
    {
        PageKey entry = AreaCatalog.EntryPage(area, MachineMode.Jog);
        if (!this.pages.TryGetValue(entry, out PageViewModelBase? page))
        {
            return this.localizer["Nav_OfflineUnavailable"];
        }

        if (IsOffline && !page.WorksOffline)
        {
            return this.localizer["Nav_OfflineUnavailable"];
        }

        if (area == AreaKey.Commissioning && !this.userSession.Can(Permission.EditMachineConfig))
        {
            return this.localizer["Nav_NeedsManufacturer"];
        }

        return null;
    }

    private void RefreshQuickBar()
    {
        foreach (QuickBarItemViewModel item in QuickBarItems)
        {
            item.UnavailableReason = AreaUnavailableReason(item.Entry.Area);
            item.IsAvailable = item.UnavailableReason is null;
            item.IsCurrent = item.Entry.Area == this.model.CurrentArea
                && (item.Entry.GroupKey is null
                    ? !QuickBarItems.Any(other => other.Entry.Area == item.Entry.Area && other.Entry.GroupKey is { } g && CurrentGroupIs(g))
                    : CurrentGroupIs(item.Entry.GroupKey));
        }
    }

    private string? currentGroup;

    private bool CurrentGroupIs(string groupKey) => string.Equals(this.currentGroup, groupKey, StringComparison.Ordinal);

    // ── 左栏、区域菜单 ────────────────────────────────────────────────────────

    /// <summary>左栏：一点直达（Ctrl+n 同）。进不去就说原因。</summary>
    private void PressQuickBar(QuickBarEntry entry)
    {
        if (IsOverlayOpen)
        {
            return;
        }

        if (AreaUnavailableReason(entry.Area) is { } reason)
        {
            this.interaction.Refuse(reason);
            return;
        }

        RequestArea(entry.Area, entry.GroupKey);
    }

    /// <summary>键盘 Ctrl+1…7。</summary>
    public void PressQuickBar(int index)
    {
        if (index >= 0 && index < QuickBarItems.Count)
        {
            QuickBarItems[index].Command.Execute(null);
        }
    }

    /// <summary>右上区域方块、F10：打开 / 收起区域菜单。</summary>
    [RelayCommand]
    private void ToggleAreaMenu()
    {
        if (IsOverlayOpen)
        {
            return;
        }

        this.model.ToggleAreaMenu();
        SyncNavigation();
    }

    [RelayCommand]
    private void CloseAreaMenu()
    {
        this.model.CloseAreaMenu();
        SyncNavigation();
    }

    private void ChooseArea(AreaKey area)
    {
        this.model.CloseAreaMenu();
        RequestArea(area, groupKey: null);
    }

    /// <summary>路径条左端"« xxx"与 Esc 的最后一级："返回"。</summary>
    [RelayCommand]
    private void Back()
    {
        BackDescriptor back = this.model.DescribeBack();
        switch (back.Role)
        {
            case BackRole.CloseAreaMenu:
                CloseAreaMenu();
                break;

            case BackRole.CloseSubView:
                CloseSubView();
                break;

            case BackRole.BackToTask:
                RequestPage(back.Target ?? this.model.HomePage, isTaskReturn: true, returnTo: null);
                break;

            default:
                break;
        }
    }

    // ── 键盘 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Esc（最终稿 4.6）：先收浮层与键盘，再取消待确认的事，再收本页的框、竖键子菜单，最后"返回"。
    /// </summary>
    public void PressEscape()
    {
        if (IsSignInOpen)
        {
            // 登录框退不掉：没登录就什么都不给。
            return;
        }

        if (IsUserMenuOpen)
        {
            IsUserMenuOpen = false;
            return;
        }

        if (IsUserAdminOpen)
        {
            CloseUserAdmin();
            return;
        }

        if (IsLeaveConfirmOpen)
        {
            CancelLeave();
            return;
        }

        if (Keypad.IsOpen)
        {
            Keypad.Close();
            return;
        }

        if (this.interaction.Confirmations.Cancel())
        {
            return;
        }

        if (Help.IsOpen)
        {
            Help.Close();
            return;
        }

        if (CurrentPage.TryDismissPrompt())
        {
            return;
        }

        if (CurrentPage.CloseVerticalMenu())
        {
            return;
        }

        Back();
    }

    /// <summary>回车：有待确认的事就是"✓ 确认"（最终稿 4.5）。返回 true 表示回车被用掉了。</summary>
    public bool PressEnter()
    {
        if (IsOverlayOpen || this.interaction.Confirmations.Pending is null)
        {
            return false;
        }

        _ = this.interaction.Confirmations.ConfirmAsync();
        return true;
    }

    // ── 功能键块 ──────────────────────────────────────────────────────────────

    /// <summary>"↶ 撤销"。</summary>
    [RelayCommand]
    private void Undo()
    {
        if (CurrentPage.CanUndo)
        {
            CurrentPage.Undo();
        }
        else
        {
            this.interaction.Refuse(this.localizer["Fb_NothingToUndo"]);
        }
    }

    /// <summary>"↷ 重做"。</summary>
    [RelayCommand]
    private void Redo()
    {
        if (CurrentPage.CanRedo)
        {
            CurrentPage.Redo();
        }
        else
        {
            this.interaction.Refuse(this.localizer["Fb_NothingToRedo"]);
        }
    }

    /// <summary>"i 帮助"：开着就关，关着就按本画面的条目打开。</summary>
    [RelayCommand]
    private void ToggleHelp()
    {
        if (Help.IsOpen)
        {
            Help.Close();
        }
        else
        {
            Help.Open(CurrentPage.HelpTopicKey);
        }
    }

    /// <summary>左栏底部"⇆ 侧屏"：侧屏归数控系统自带的操作界面，上位机里不开；按下去说清楚原因。</summary>
    [RelayCommand]
    private void SideScreen() => this.interaction.Refuse(this.localizer["Quick_SideScreenUnavailable"]);

    /// <summary>"⌨ 键盘"、"▦ 计算"、"◉ 截屏"是视图层的事（系统键盘、计算器、截图），由窗口接。</summary>
    public event EventHandler<ShellViewRequest>? ViewRequested;

    [RelayCommand]
    private void ShowTouchKeyboard() => ViewRequested?.Invoke(this, ShellViewRequest.TouchKeyboard);

    [RelayCommand]
    private void ShowCalculator() => ViewRequested?.Invoke(this, ShellViewRequest.Calculator);

    [RelayCommand]
    private void TakeScreenshot() => ViewRequested?.Invoke(this, ShellViewRequest.Screenshot);

    /// <summary>
    /// Ctrl+L：中文 ⇄ English。问一句再改 hmi.json，重启上位机后生效——
    /// 界面文字在载入时取定，当场换会弄出一半中文一半英文的画面。
    /// </summary>
    public void ToggleLanguage()
    {
        string next = this.culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
        this.interaction.Ask(
            this.localizer.Format("Language_SwitchQuestion", this.localizer["Language_" + next.Replace("-", string.Empty, StringComparison.Ordinal)]),
            async () =>
            {
                try
                {
                    await RollGrinder.Composition.JsonHmiSettingsProvider
                        .SaveCultureAsync(this.options, next, CancellationToken.None).ConfigureAwait(true);
                    this.culture = next;
                    this.interaction.Say(this.localizer["Language_Saved"]);
                }
                catch (Exception ex) when (ex is GatewayException or System.IO.IOException or UnauthorizedAccessException)
                {
                    Alarms.RaiseException(ex);
                }
            });
    }

    /// <summary>
    /// Ctrl+C / X / V（最终稿 4.6）：交给本页（段表、工序序列）。本页不认就返回 false，按键照常往下传。
    /// 剪切、粘贴会改东西：只读时不做，在对话行说原因。
    /// </summary>
    public bool Clipboard(ClipboardAction action)
    {
        if (IsOverlayOpen || Help.IsOpen || CurrentPage.HasModalPrompt || !CurrentPage.SupportsClipboard)
        {
            return false;
        }

        if (action != ClipboardAction.Copy && CurrentPage.ReadOnlyReason is { } reason)
        {
            this.interaction.Refuse(reason);
            return true;
        }

        CurrentPage.Clipboard(action);
        return true;
    }

    /// <summary>视图层做完一件事后在对话行报个结果。</summary>
    public void Report(string resourceKey, bool failed, params object?[] arguments)
    {
        string text = this.localizer.Format(resourceKey, arguments);
        if (failed)
        {
            this.interaction.Fail(text);
        }
        else
        {
            this.interaction.Say(text);
        }
    }

    /// <summary>对话行上的常驻说明（输入框获得焦点时由视图层推进来：含义、单位、范围）。</summary>
    public void ShowFieldHint(string? text) => this.interaction.Hint(text);

    // ── 离开确认 ──────────────────────────────────────────────────────────────

    /// <summary>离开确认框："保存并离开"。</summary>
    [RelayCommand]
    private async Task SaveAndLeaveAsync()
    {
        PageViewModelBase page = CurrentPage;
        bool saved = await page.SaveAsync(CancellationToken.None).ConfigureAwait(true);
        if (!saved)
        {
            // 没保存成功就留在本页：宁可挡住切换，也不能把修改丢掉。
            Alarms.Raise(AlarmSeverity.Warning, "Alarm_SaveFailedStayingOnPage", page.Title);
            CancelLeave();
            return;
        }

        CommitPendingNavigation();
    }

    /// <summary>离开确认框："放弃修改并离开"。</summary>
    [RelayCommand]
    private void DiscardAndLeave()
    {
        CurrentPage.DiscardChanges();
        CommitPendingNavigation();
    }

    /// <summary>离开确认框："取消"——留在本页继续编辑。</summary>
    [RelayCommand]
    private void CancelLeave()
    {
        this.pendingPage = null;
        this.pendingIsTaskReturn = false;
        this.pendingReturnTo = null;
        this.pendingGroup = null;
        IsLeaveConfirmOpen = false;
    }

    // ── 换页 ──────────────────────────────────────────────────────────────────

    private void Handle(NavigationRequest request)
    {
        switch (request.Kind)
        {
            case NavigationRequestKind.GoTo:
                RequestPage(request.Target, isTaskReturn: false, returnTo: null, request.SubViewKey);
                break;

            case NavigationRequestKind.GoToArea when request.Area is AreaKey area:
                RequestArea(area, request.SubViewKey);
                break;

            case NavigationRequestKind.StartTask:
                RequestPage(request.Target, isTaskReturn: false, returnTo: request.ReturnTo ?? this.model.CurrentPage);
                break;

            case NavigationRequestKind.CompleteTask:
                RequestPage(this.model.TaskReturnPage ?? this.model.HomePage, isTaskReturn: true, returnTo: null);
                break;

            case NavigationRequestKind.OpenSubView:
                if (request.SubViewKey is { Length: > 0 } subViewKey)
                {
                    this.model.OpenSubView(subViewKey);
                    CurrentPage.ActiveSubViewKey = subViewKey;
                    SyncNavigation();
                }

                break;

            case NavigationRequestKind.CloseSubView:
                CloseSubView();
                break;

            case NavigationRequestKind.OpenAreaMenu:
                this.model.OpenAreaMenu();
                SyncNavigation();
                break;

            default:
                break;
        }
    }

    private void CloseSubView()
    {
        if (this.model.CloseSubView())
        {
            CurrentPage.ActiveSubViewKey = null;
        }

        SyncNavigation();
    }

    /// <summary>去一个区域：入口画面随 NC 方式（机床区），并打开指定的功能组。</summary>
    private void RequestArea(AreaKey area, string? groupKey)
    {
        PageKey target = AreaCatalog.EntryPage(area, MachineMode);
        if (!this.pages.ContainsKey(target))
        {
            target = AreaCatalog.PagesOf(area).FirstOrDefault(this.pages.ContainsKey, target);
        }

        // 已经在这个区域的某个画面上：左栏再点一下回区域入口（机床区回基本画面）。
        RequestPage(target, isTaskReturn: false, returnTo: null, groupKey);
    }

    /// <summary>切画面的唯一入口：脏页先拦一道，确认过了才真换。</summary>
    private void RequestPage(PageKey page, bool isTaskReturn, PageKey? returnTo, string? groupKey = null)
    {
        if (!this.pages.ContainsKey(page))
        {
            return;
        }

        if (page == this.model.CurrentPage && returnTo is null && !isTaskReturn)
        {
            // 已经在这页了：收掉菜单与子功能，打开要的功能组，不做无意义的切换。
            CloseSubView();
            this.model.CloseAreaMenu();
            ShowGroup(groupKey);
            SyncNavigation();
            return;
        }

        if (CurrentPage.IsDirty && page != this.model.CurrentPage)
        {
            OpenLeaveConfirm(page, isTaskReturn, returnTo, groupKey);
            return;
        }

        Commit(page, isTaskReturn, returnTo, groupKey);
    }

    /// <summary>脏页要走了：把这次跳转的意图存下来，先问操作员。</summary>
    private void OpenLeaveConfirm(PageKey page, bool isTaskReturn, PageKey? returnTo, string? groupKey)
    {
        this.pendingPage = page;
        this.pendingIsTaskReturn = isTaskReturn;
        this.pendingReturnTo = returnTo;
        this.pendingGroup = groupKey;
        LeaveConfirmPageTitle = CurrentPage.Title;
        LeaveConfirmCanSave = CurrentPage.CanSave;
        this.model.CloseAreaMenu();
        IsLeaveConfirmOpen = true;
        SyncNavigation();
    }

    private void CommitPendingNavigation()
    {
        PageKey? page = this.pendingPage;
        bool isTaskReturn = this.pendingIsTaskReturn;
        PageKey? returnTo = this.pendingReturnTo;
        string? groupKey = this.pendingGroup;
        CancelLeave();

        if (page is PageKey target)
        {
            Commit(target, isTaskReturn, returnTo, groupKey);
        }
    }

    private void Commit(PageKey page, bool isTaskReturn, PageKey? returnTo, string? groupKey)
    {
        // 换页时没答的确认一律作废：问题是针对上一页说的。
        this.interaction.Confirmations.Cancel();
        Keypad.Close();

        PageViewModelBase previous = CurrentPage;
        previous.ActiveSubViewKey = null;
        previous.ResetVerticalMenu();

        if (isTaskReturn)
        {
            if (this.model.TaskReturnPage == page)
            {
                this.model.CompleteTask();
            }
            else
            {
                this.model.GoTo(page);
            }
        }
        else if (returnTo is PageKey origin)
        {
            this.model.StartTask(page, origin);
        }
        else
        {
            this.model.GoTo(page);
        }

        PageViewModelBase next = this.pages[this.model.CurrentPage];
        if (!ReferenceEquals(previous, next))
        {
            previous.OnDeactivated();
            this.currentGroup = null;
            CurrentPage = next;
            next.ApplyRunState(IsMachineRunning);
            next.ApplyEmergencyStop(IsEmergencyStop);
            next.OnActivated();
        }

        ShowGroup(groupKey);
        RebuildHorizontalKeys();
        SyncNavigation();
    }

    private void ShowGroup(string? groupKey)
    {
        this.currentGroup = groupKey is not null && CurrentPage.ShowGroup(groupKey) ? groupKey : null;
    }

    /// <summary>把状态机的当前样子刷到界面：区域菜单、区域方块、路径条、左栏当前项。</summary>
    private void SyncNavigation()
    {
        bool menuChanged = IsAreaMenuOpen != this.model.IsAreaMenuOpen;
        IsAreaMenuOpen = this.model.IsAreaMenuOpen;
        if (menuChanged)
        {
            RebuildHorizontalKeys();
        }

        AreaKey area = this.model.CurrentArea;
        AreaTileGlyph = AreaCatalog.Glyph(area);
        AreaTileText = this.localizer[AreaCatalog.TitleKey(area)];

        string separator = this.localizer["Nav_PathSeparator"];
        string path = CurrentPage.Title;
        if (this.model.CurrentSubViewKey is { } subView)
        {
            path += separator + this.localizer[subView];
        }

        PathText = path;

        BackDescriptor back = this.model.DescribeBack();
        BackText = back.Role switch
        {
            BackRole.CloseSubView or BackRole.BackToTask when back.Target is PageKey target && this.pages.TryGetValue(target, out PageViewModelBase? page)
                => this.localizer.Format("Nav_BackToPageFormat", page.Title),
            _ => string.Empty,
        };

        RefreshQuickBar();
    }

    private void SyncDialogLine()
    {
        DialogText = this.interaction.DialogLine.Text;
        DialogKind = this.interaction.DialogLine.Kind;
    }
}

/// <summary>段表、工序序列里的剪贴板动作（Ctrl+C / X / V）。</summary>
public enum ClipboardAction
{
    /// <summary>复制选中的一条。</summary>
    Copy = 0,

    /// <summary>剪下选中的一条。</summary>
    Cut = 1,

    /// <summary>粘在选中的一条之后。</summary>
    Paste = 2,
}

/// <summary>外壳请视图层做的事。</summary>
public enum ShellViewRequest
{
    /// <summary>调出系统触摸键盘（输名称、备注）。</summary>
    TouchKeyboard = 0,

    /// <summary>调出计算器。</summary>
    Calculator = 1,

    /// <summary>截屏存进数据目录。</summary>
    Screenshot = 2,
}
