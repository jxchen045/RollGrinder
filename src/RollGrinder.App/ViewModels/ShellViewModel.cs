using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Data;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 界面外壳：顶栏（菜单 / 页面上下文 / 状态条 / NC 连接 / 权限 / 时钟）、
/// 页面容器、底部 8 键功能条（前 7 个来自页面，第 8 个是导航槽），
/// 以及两个浮层：区域菜单与离开确认。
///
/// 页面切换的规则只有这一处，见 <see cref="NavigationModel"/>：
/// 区域之间是平的（不叠历史栈），导航槽只退一级且标签写明退到哪，
/// 脏页离开要经确认，自动循环运行期间编辑页落只读锁。
///
/// 刷新节拍由窗口的定时器驱动（hmi.json 的 uiRefreshHz，5–10 Hz），
/// 只有当前页会收到 OnTick。
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private const string NoAlarmCode = "0000";

    private static readonly IReadOnlyList<PageKey> AreaOrder = new[]
    {
        PageKey.AutoGrinding,
        PageKey.Steps,
        PageKey.Profile,
        PageKey.Manual,
        PageKey.Records,
        PageKey.Diagnostics,
        PageKey.Settings,
    };

    private readonly IAlarmLog alarmLog;
    private readonly IMachineMonitor monitor;
    private readonly IUserSession userSession;
    private readonly IUserDirectory userDirectory;
    private readonly IStringLocalizer localizer;
    private readonly Navigator navigator;
    private readonly NavigationModel model;
    private readonly Dictionary<PageKey, PageViewModelBase> pages;
    private readonly FunctionKeyViewModel navigationKey;

    private long lastShownAlarmId = -1;
    private PageKey? pendingArea;
    private bool pendingIsTaskReturn;
    private PageKey? pendingReturnTo;

    public ShellViewModel(
        IEnumerable<PageViewModelBase> pages,
        Navigator navigator,
        IAlarmLog alarmLog,
        IMachineMonitor monitor,
        IUserSession userSession,
        IUserDirectory userDirectory,
        IAppOptions options,
        HmiSettings settings,
        IStringLocalizer localizer)
        : base(alarmLog)
    {
        ArgumentNullException.ThrowIfNull(pages);
        this.navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        this.alarmLog = alarmLog ?? throw new ArgumentNullException(nameof(alarmLog));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.userSession = userSession ?? throw new ArgumentNullException(nameof(userSession));
        this.userDirectory = userDirectory ?? throw new ArgumentNullException(nameof(userDirectory));
        this.newUserRole = settings.DefaultRole;
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        ArgumentNullException.ThrowIfNull(settings);

        ArgumentNullException.ThrowIfNull(options);
        this.pages = pages.ToDictionary(page => page.Key);
        IsOffline = options.IsOffline;

        // 离线模式下自动磨削页用不了（没有机床可监控），主页改成第一个离线可用的区域——
        // 否则"返回主页"会把人送到一个只能看不能用的页面上。
        this.model = new NavigationModel(IsOffline ? FirstOfflineArea() : null);
        RefreshInterval = TimeSpan.FromSeconds(1.0 / settings.UiRefreshHz);

        this.navigationKey = new FunctionKeyViewModel(
            "Nav_AreaMenu",
            new RelayCommand(ActivateNavigationKey),
            localizer,
            FunctionKeyKind.Navigation);

        foreach (PageKey area in AreaOrder)
        {
            if (!this.pages.TryGetValue(area, out PageViewModelBase? page))
            {
                continue;
            }

            PageKey target = area;
            bool available = !IsOffline || page.WorksOffline;
            AreaMenuItems.Add(new AreaMenuItemViewModel(
                target,
                AreaMenuItems.Count + 1,
                page.TitleResourceKey,
                page.MenuHintResourceKey,
                new RelayCommand(() => ChooseArea(target), () => available),
                localizer,
                available));
        }

        this.navigator.Requested += (_, request) => Handle(request);

        this.currentPage = this.pages[this.model.HomeArea];
        RebuildFunctionKeys();
        SyncNavigation();
        this.currentPage.OnActivated();
    }

    /// <summary>界面刷新周期。</summary>
    public TimeSpan RefreshInterval { get; }

    /// <summary>报警列表（报警页与弹出条共用）。</summary>
    public ObservableCollection<AlarmRowViewModel> AlarmRows { get; } = new();

    /// <summary>底部功能条实际渲染的 8 格：前 7 格来自页面（不足补空位），第 8 格是导航槽。</summary>
    public ObservableCollection<FunctionKeyViewModel> FunctionKeys { get; } = new();

    /// <summary>区域菜单的六格。</summary>
    public ObservableCollection<AreaMenuItemViewModel> AreaMenuItems { get; } = new();

    [ObservableProperty]
    private PageViewModelBase currentPage;

    [ObservableProperty]
    private string clockText = "--:--:--";

    [ObservableProperty]
    private string bannerCode = NoAlarmCode;

    [ObservableProperty]
    private string bannerText = string.Empty;

    [ObservableProperty]
    private AlarmSeverity bannerSeverity = AlarmSeverity.Information;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string connectionText = string.Empty;

    [ObservableProperty]
    private string roleText = string.Empty;

    /// <summary>区域菜单是否展开。</summary>
    [ObservableProperty]
    private bool isAreaMenuOpen;

    /// <summary>离开确认框是否展开。</summary>
    [ObservableProperty]
    private bool isLeaveConfirmOpen;

    /// <summary>离开确认框里显示的页名。</summary>
    [ObservableProperty]
    private string leaveConfirmPageTitle = string.Empty;

    /// <summary>离开确认框要不要给"保存并离开"：本页接上存储之后才给。</summary>
    [ObservableProperty]
    private bool leaveConfirmCanSave;

    /// <summary>当前面包屑：主页 / 主页 › 子页 / 主页 › 子页 › 子视图。</summary>
    [ObservableProperty]
    private string breadcrumbText = string.Empty;

    /// <summary>自动循环是否还挂着程序：挂着就锁编辑页。</summary>
    [ObservableProperty]
    private bool isMachineRunning;

    /// <summary>品牌标识。</summary>
    public string Brand => this.localizer["Shell_Brand"];

    /// <summary>
    /// 离线模式：没有机床。顶栏标出来，免得有人对着一台"连不上"的机床查半天线路。
    /// </summary>
    public bool IsOffline { get; }

    /// <summary>离线时第一个能进的区域，兼作主页。</summary>
    private PageKey FirstOfflineArea()
    {
        foreach (PageKey area in AreaOrder)
        {
            if (this.pages.TryGetValue(area, out PageViewModelBase? page) && page.WorksOffline)
            {
                return area;
            }
        }

        return NavigationModel.DefaultHomeArea;
    }

    /// <summary>有浮层挡着时，底下的页面与功能条不接受点击。</summary>
    public bool IsOverlayOpen => IsAreaMenuOpen || IsLeaveConfirmOpen || IsSignInOpen || IsUserAdminOpen;

    partial void OnIsAreaMenuOpenChanged(bool value) => OnPropertyChanged(nameof(IsOverlayOpen));

    partial void OnIsLeaveConfirmOpenChanged(bool value) => OnPropertyChanged(nameof(IsOverlayOpen));

    partial void OnIsSignInOpenChanged(bool value) => OnPropertyChanged(nameof(IsOverlayOpen));

    partial void OnIsUserAdminOpenChanged(bool value) => OnPropertyChanged(nameof(IsOverlayOpen));

    // ── 登录 ──────────────────────────────────────────────────────────────────
    //
    // 没登录就什么都不给：登录浮层是 IsOverlayOpen 的一部分，
    // 所以功能键与页面点击在签退状态下自动不透传，不用每个页面各自记得判断。

    /// <summary>登录浮层开着没有。启动时是开着的。</summary>
    [ObservableProperty]
    private bool isSignInOpen = true;

    /// <summary>登录了没有——顶栏按它显示用户名还是"未登录"。</summary>
    [ObservableProperty]
    private bool isSignedIn;

    /// <summary>当前用户名，顶栏显示。</summary>
    [ObservableProperty]
    private string userNameText = string.Empty;

    /// <summary>登录框里选/填的用户名。</summary>
    [ObservableProperty]
    private string signInUserName = string.Empty;

    /// <summary>登录框里的口令。由 PasswordBox 的事件推进来——WPF 不让绑 Password。</summary>
    [ObservableProperty]
    private string signInPassword = string.Empty;

    /// <summary>首次设口令时的新口令与确认。</summary>
    [ObservableProperty]
    private string newPassword = string.Empty;

    [ObservableProperty]
    private string confirmPassword = string.Empty;

    /// <summary>这个账号还没设过口令：登录框切到"先设一个口令"。</summary>
    [ObservableProperty]
    private bool needsNewPassword;

    /// <summary>常规登录（不是"先设口令"那一支）。给界面切换两块输入区用。</summary>
    public bool IsNormalSignIn => !NeedsNewPassword;

    partial void OnNeedsNewPasswordChanged(bool value) => OnPropertyChanged(nameof(IsNormalSignIn));

    /// <summary>登录框里的提示（口令不对、两次不一致……）；没有提示时为空。</summary>
    [ObservableProperty]
    private string signInMessage = string.Empty;

    /// <summary>库里有哪些用户名，登录框做成下拉，省得在触摸屏上打字。</summary>
    public ObservableCollection<string> KnownUserNames { get; } = new();

    /// <summary>账号列表，用户管理浮层用。</summary>
    public ObservableCollection<UserAccount> UserAccounts { get; } = new();

    [ObservableProperty]
    private bool isUserAdminOpen;

    [ObservableProperty]
    private UserAccount? selectedUserAccount;

    /// <summary>新建账号的用户名、口令与权限。</summary>
    [ObservableProperty]
    private string newUserName = string.Empty;

    [ObservableProperty]
    private string newUserPassword = string.Empty;

    [ObservableProperty]
    private UserRole newUserRole;

    /// <summary>管理员以上才看得到"用户管理"。</summary>
    public bool CanManageUsers => this.userSession.HasAtLeast(UserRole.Administrator);

    /// <summary>权限下拉的取值。</summary>
    public IReadOnlyList<UserRole> AssignableRoles { get; } =
        new[] { UserRole.Operator, UserRole.Administrator, UserRole.Manufacturer };

    /// <summary>启动后把用户名列表拉进来，登录框的下拉才有东西可选。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        SignInMessage = string.Empty;

        try
        {
            if (NeedsNewPassword)
            {
                await CompleteFirstPasswordAsync(cancellationToken).ConfigureAwait(true);
                return;
            }

            SignInResult result = await this.userDirectory
                .SignInAsync(SignInUserName, SignInPassword, cancellationToken).ConfigureAwait(true);

            switch (result.Outcome)
            {
                case SignInOutcome.Succeeded:
                    Accept(result.Account!);
                    break;

                case SignInOutcome.PasswordNotSet:
                    // 首次启动种下的管理账号走这一支：先设口令再放行。
                    NeedsNewPassword = true;
                    SignInMessage = this.localizer["SignIn_SetPasswordFirst"];
                    break;

                default:
                    // 用户名不存在与口令不对不分开报——分开报等于告诉人哪个用户名存在。
                    SignInMessage = this.localizer["SignIn_Rejected"];
                    break;
            }
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    private async Task CompleteFirstPasswordAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(NewPassword))
        {
            SignInMessage = this.localizer["SignIn_PasswordEmpty"];
            return;
        }

        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            SignInMessage = this.localizer["SignIn_PasswordMismatch"];
            return;
        }

        try
        {
            await this.userDirectory
                .SetPasswordAsync(SignInUserName, NewPassword, cancellationToken).ConfigureAwait(true);

            SignInResult result = await this.userDirectory
                .SignInAsync(SignInUserName, NewPassword, cancellationToken).ConfigureAwait(true);
            if (result.Account is not null)
            {
                Accept(result.Account);
            }
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    private void Accept(UserAccount account)
    {
        this.userSession.SignIn(account);
        IsSignedIn = true;
        UserNameText = account.UserName;
        IsSignInOpen = false;
        ClearSignInFields();
        OnPropertyChanged(nameof(CanManageUsers));
    }

    [RelayCommand]
    private void SignOut()
    {
        this.userSession.SignOut();
        IsSignedIn = false;
        UserNameText = string.Empty;
        IsUserAdminOpen = false;
        ClearSignInFields();
        IsSignInOpen = true;
        OnPropertyChanged(nameof(CanManageUsers));
    }

    private void ClearSignInFields()
    {
        SignInPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        NeedsNewPassword = false;
        SignInMessage = string.Empty;
    }

    private async Task RefreshUserNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<UserAccount> accounts =
                await this.userDirectory.ListAsync(cancellationToken).ConfigureAwait(true);

            KnownUserNames.Clear();
            UserAccounts.Clear();
            foreach (UserAccount account in accounts)
            {
                KnownUserNames.Add(account.UserName);
                UserAccounts.Add(account);
            }

            if (string.IsNullOrEmpty(SignInUserName))
            {
                SignInUserName = KnownUserNames.FirstOrDefault() ?? string.Empty;
            }
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    // ── 用户管理 ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenUserAdminAsync(CancellationToken cancellationToken)
    {
        if (!CanManageUsers)
        {
            return;
        }

        await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
        SelectedUserAccount = UserAccounts.FirstOrDefault();
        IsUserAdminOpen = true;
    }

    [RelayCommand]
    private void CloseUserAdmin() => IsUserAdminOpen = false;

    [RelayCommand]
    private async Task CreateUserAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 口令留空表示"首次登录时再设"，和出厂那个管理账号一样。
            await this.userDirectory.CreateAsync(
                NewUserName,
                string.IsNullOrEmpty(NewUserPassword) ? null : NewUserPassword,
                NewUserRole,
                cancellationToken).ConfigureAwait(true);

            NewUserName = string.Empty;
            NewUserPassword = string.Empty;
            await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    [RelayCommand]
    private async Task DeleteUserAsync(CancellationToken cancellationToken)
    {
        if (SelectedUserAccount is null)
        {
            return;
        }

        try
        {
            await this.userDirectory
                .DeleteAsync(SelectedUserAccount.UserName, cancellationToken).ConfigureAwait(true);
            await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (DomainException ex)
        {
            // 最后一个制造商级账号不许删——删了这台机器就再也没人能管了。
            Alarms.RaiseException(ex);
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    /// <summary>把选中账号的口令清掉：下次登录时由本人重设，管理员看不到明文。</summary>
    [RelayCommand]
    private async Task ResetUserPasswordAsync(CancellationToken cancellationToken)
    {
        if (SelectedUserAccount is null)
        {
            return;
        }

        try
        {
            await this.userDirectory.DeleteAsync(SelectedUserAccount.UserName, cancellationToken).ConfigureAwait(true);
            await this.userDirectory.CreateAsync(
                SelectedUserAccount.UserName, null, SelectedUserAccount.Role, cancellationToken).ConfigureAwait(true);
            await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    /// <summary>界面定时器每一拍调用。</summary>
    public void Tick(DateTimeOffset nowUtc)
    {
        ClockText = nowUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        MachineStateSnapshot snapshot = this.monitor.Current;
        IsConnected = snapshot.ConnectionState == GatewayConnectionState.Connected;
        ConnectionText = this.localizer[IsConnected ? "Top_NcConnected" : "Top_NcDisconnected"];
        RoleText = this.userSession.IsSignedIn
            ? this.localizer["Role_" + this.userSession.CurrentRole]
            : this.localizer["Role_SignedOut"];

        ApplyRunState(snapshot);
        RefreshBanner(snapshot);
        CurrentPage.OnTick(nowUtc);
    }

    /// <summary>键盘/软键按下第 n 个功能键（n 从 0 起）。浮层挡着时不透传。</summary>
    public void PressFunctionKey(int index)
    {
        if (IsOverlayOpen || index < 0 || index >= FunctionKeys.Count)
        {
            return;
        }

        FunctionKeyViewModel key = FunctionKeys[index];
        if (key.IsEnabled && key.Command.CanExecute(null))
        {
            key.Command.Execute(null);
        }
    }

    /// <summary>Esc：有浮层先收浮层（等同于"取消"），没有才退一级。</summary>
    public void PressEscape()
    {
        if (IsSignInOpen)
        {
            // 登录框退不掉：没登录就什么都不给。
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

        if (IsAreaMenuOpen)
        {
            CloseAreaMenu();
            return;
        }

        ActivateNavigationKey();
    }

    [RelayCommand]
    private void OpenAreaMenu()
    {
        this.model.OpenAreaMenu();
        SyncNavigation();
    }

    [RelayCommand]
    private void CloseAreaMenu()
    {
        this.model.CloseAreaMenu();
        SyncNavigation();
    }

    [RelayCommand]
    private void ClearAlarms()
    {
        this.alarmLog.Clear();
        this.lastShownAlarmId = -1;
        AlarmRows.Clear();
        BannerCode = NoAlarmCode;
        BannerText = string.Empty;
        BannerSeverity = AlarmSeverity.Information;
    }

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
        this.pendingArea = null;
        this.pendingIsTaskReturn = false;
        this.pendingReturnTo = null;
        IsLeaveConfirmOpen = false;
    }

    private void ChooseArea(PageKey area)
    {
        this.model.CloseAreaMenu();
        RequestArea(area, isTaskReturn: false);
    }

    private void Handle(NavigationRequest request)
    {
        switch (request.Kind)
        {
            case NavigationRequestKind.GoToArea:
                RequestArea(request.Target, isTaskReturn: false);
                break;

            case NavigationRequestKind.StartTask:
                RequestTask(request.Target, request.ReturnTo ?? this.model.CurrentArea);
                break;

            case NavigationRequestKind.CompleteTask:
                RequestArea(this.model.TaskReturnArea ?? this.model.HomeArea, isTaskReturn: true);
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
                OpenAreaMenu();
                break;

            default:
                break;
        }
    }

    /// <summary>导航槽：按角色退一级或打开菜单。标签已经说明了会发生什么。</summary>
    private void ActivateNavigationKey()
    {
        NavigationKeyDescriptor descriptor = this.model.DescribeNavigationKey();
        switch (descriptor.Role)
        {
            case NavigationKeyRole.OpenAreaMenu:
                OpenAreaMenu();
                break;

            case NavigationKeyRole.CloseSubView:
                CloseSubView();
                break;

            case NavigationKeyRole.BackToTask:
                RequestArea(descriptor.TargetArea ?? this.model.HomeArea, isTaskReturn: true);
                break;

            case NavigationKeyRole.BackToHome:
                RequestArea(this.model.HomeArea, isTaskReturn: false);
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

    /// <summary>切区域的唯一入口：脏页先拦一道，确认过了才真换。</summary>
    private void RequestArea(PageKey area, bool isTaskReturn)
    {
        if (area == this.model.CurrentArea)
        {
            // 已经在这页了：收掉菜单与子视图，不做无意义的切换。
            CloseSubView();
            this.model.CloseAreaMenu();
            SyncNavigation();
            return;
        }

        if (!this.pages.ContainsKey(area))
        {
            return;
        }

        if (CurrentPage.IsDirty)
        {
            OpenLeaveConfirm(area, isTaskReturn, returnTo: null);
            return;
        }

        Commit(area, isTaskReturn, returnTo: null);
    }

    private void RequestTask(PageKey target, PageKey returnTo)
    {
        if (target == returnTo || !this.pages.ContainsKey(target))
        {
            RequestArea(target, isTaskReturn: false);
            return;
        }

        if (CurrentPage.IsDirty)
        {
            OpenLeaveConfirm(target, isTaskReturn: false, returnTo: returnTo);
            return;
        }

        Commit(target, isTaskReturn: false, returnTo: returnTo);
    }

    /// <summary>脏页要走了：把这次跳转的意图存下来，先问操作员。</summary>
    private void OpenLeaveConfirm(PageKey area, bool isTaskReturn, PageKey? returnTo)
    {
        this.pendingArea = area;
        this.pendingIsTaskReturn = isTaskReturn;
        this.pendingReturnTo = returnTo;
        LeaveConfirmPageTitle = CurrentPage.Title;
        LeaveConfirmCanSave = CurrentPage.CanSave;
        this.model.CloseAreaMenu();
        IsLeaveConfirmOpen = true;
        SyncNavigation();
    }

    private void CommitPendingNavigation()
    {
        PageKey? area = this.pendingArea;
        bool isTaskReturn = this.pendingIsTaskReturn;
        PageKey? returnTo = this.pendingReturnTo;
        this.pendingArea = null;
        this.pendingIsTaskReturn = false;
        this.pendingReturnTo = null;
        IsLeaveConfirmOpen = false;

        if (area is PageKey target)
        {
            Commit(target, isTaskReturn, returnTo);
        }
    }

    private void Commit(PageKey area, bool isTaskReturn, PageKey? returnTo)
    {
        PageViewModelBase previous = CurrentPage;
        previous.ActiveSubViewKey = null;

        if (isTaskReturn)
        {
            this.model.CompleteTask();
        }
        else if (returnTo is PageKey origin)
        {
            this.model.StartTask(area, origin);
        }
        else
        {
            this.model.GoToArea(area);
        }

        PageViewModelBase next = this.pages[this.model.CurrentArea];
        if (!ReferenceEquals(previous, next))
        {
            previous.OnDeactivated();
            CurrentPage = next;
            RebuildFunctionKeys();
            next.ApplyRunState(IsMachineRunning);
            next.OnActivated();
        }

        SyncNavigation();
    }

    /// <summary>重建 8 格：页面的键不足 7 个时补空位，保证导航槽永远在最右边同一格。</summary>
    private void RebuildFunctionKeys()
    {
        FunctionKeys.Clear();
        foreach (FunctionKeyViewModel key in CurrentPage.FunctionKeys)
        {
            FunctionKeys.Add(key);
        }

        while (FunctionKeys.Count < PageViewModelBase.PageFunctionKeyCount)
        {
            FunctionKeys.Add(new FunctionKeyViewModel(
                "Fn_Empty",
                new RelayCommand(() => { }, () => false),
                this.localizer)
            {
                IsEnabled = false,
            });
        }

        FunctionKeys.Add(this.navigationKey);
    }

    /// <summary>把状态机的当前样子刷到界面：导航槽标签、菜单高亮、面包屑。</summary>
    private void SyncNavigation()
    {
        NavigationKeyDescriptor descriptor = this.model.DescribeNavigationKey();
        this.navigationKey.LabelResourceKey = descriptor.LabelResourceKey;
        this.navigationKey.LabelArgument = descriptor.Role is NavigationKeyRole.CloseSubView or NavigationKeyRole.BackToTask
            && descriptor.TargetArea is PageKey target
                ? this.localizer[this.pages[target].TitleResourceKey]
                : null;

        IsAreaMenuOpen = this.model.IsAreaMenuOpen;
        OnPropertyChanged(nameof(IsOverlayOpen));

        foreach (AreaMenuItemViewModel item in AreaMenuItems)
        {
            item.IsCurrent = item.Key == this.model.CurrentArea;
        }

        BreadcrumbText = BuildBreadcrumb();
    }

    private string BuildBreadcrumb()
    {
        string home = this.localizer[this.pages[this.model.HomeArea].TitleResourceKey];
        string separator = this.localizer["Nav_BreadcrumbSeparator"];

        if (this.model.CurrentArea == this.model.HomeArea)
        {
            return this.model.CurrentSubViewKey is null
                ? home
                : home + separator + this.localizer[this.model.CurrentSubViewKey];
        }

        string area = this.localizer[this.pages[this.model.CurrentArea].TitleResourceKey];
        string trail = home + separator + area;
        return this.model.CurrentSubViewKey is null
            ? trail
            : trail + separator + this.localizer[this.model.CurrentSubViewKey];
    }

    private void ApplyRunState(MachineStateSnapshot snapshot)
    {
        double? channelState = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState);
        bool running = channelState is not null && (NcChannelState)(int)channelState.Value != NcChannelState.Reset;
        if (running == IsMachineRunning)
        {
            return;
        }

        IsMachineRunning = running;
        foreach (PageViewModelBase page in this.pages.Values)
        {
            page.ApplyRunState(running);
        }
    }

    private void RefreshBanner(MachineStateSnapshot snapshot)
    {
        IReadOnlyList<AlarmEntry> entries = this.alarmLog.Snapshot();

        if (entries.Count == 0)
        {
            if (AlarmRows.Count > 0)
            {
                AlarmRows.Clear();
            }

            BannerCode = NoAlarmCode;
            BannerText = DescribeMachineState(snapshot);
            BannerSeverity = AlarmSeverity.Information;
            return;
        }

        if (entries[0].Id != this.lastShownAlarmId)
        {
            this.lastShownAlarmId = entries[0].Id;
            AlarmRows.Clear();
            foreach (AlarmEntry entry in entries)
            {
                AlarmRows.Add(new AlarmRowViewModel(entry, this.localizer));
            }
        }

        AlarmEntry newest = entries[0];
        BannerCode = newest.Code == AlarmCodes.Unspecified
            ? NoAlarmCode
            : newest.Code.ToString(CultureInfo.InvariantCulture);
        BannerText = string.IsNullOrEmpty(newest.Detail)
            ? this.localizer[newest.MessageResourceKey]
            : this.localizer[newest.MessageResourceKey] + " · " + newest.Detail;
        BannerSeverity = newest.Severity;
    }

    private string DescribeMachineState(MachineStateSnapshot snapshot)
    {
        double? channelState = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState);
        string stateText = channelState is null
            ? this.localizer["ChannelState_Unknown"]
            : this.localizer["ChannelState_" + (NcChannelState)(int)channelState.Value];

        return stateText + " · " + this.localizer["Banner_NoAlarm"];
    }
}
