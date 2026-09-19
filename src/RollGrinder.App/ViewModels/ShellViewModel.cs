using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 界面外壳：顶栏（品牌 / 页面上下文 / 状态条 / NC 连接 / 权限 / 时钟）、
/// 页面容器、底部 8 键功能条。
/// 刷新节拍由窗口的定时器驱动（hmi.json 的 uiRefreshHz，5–10 Hz），
/// 只有当前页会收到 OnTick。
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private const string NoAlarmCode = "0000";

    private readonly IAlarmLog alarmLog;
    private readonly IMachineMonitor monitor;
    private readonly IUserSession userSession;
    private readonly IStringLocalizer localizer;
    private readonly Navigator navigator;
    private readonly Stack<PageKey> history = new();
    private readonly Dictionary<PageKey, PageViewModelBase> pages;

    private long lastShownAlarmId = -1;

    public ShellViewModel(
        IEnumerable<PageViewModelBase> pages,
        Navigator navigator,
        IAlarmLog alarmLog,
        IMachineMonitor monitor,
        IUserSession userSession,
        HmiSettings settings,
        IStringLocalizer localizer)
        : base(alarmLog)
    {
        ArgumentNullException.ThrowIfNull(pages);
        this.navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        this.alarmLog = alarmLog ?? throw new ArgumentNullException(nameof(alarmLog));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.userSession = userSession ?? throw new ArgumentNullException(nameof(userSession));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        ArgumentNullException.ThrowIfNull(settings);

        this.pages = pages.ToDictionary(page => page.Key);
        RefreshInterval = TimeSpan.FromSeconds(1.0 / settings.UiRefreshHz);

        this.navigator.NavigationRequested += (_, key) => NavigateTo(key);
        this.navigator.BackRequested += (_, _) => GoBack();

        this.currentPage = this.pages[PageKey.AutoGrinding];
        this.currentPage.OnActivated();
    }

    /// <summary>界面刷新周期。</summary>
    public TimeSpan RefreshInterval { get; }

    /// <summary>报警列表（报警页与弹出条共用）。</summary>
    public ObservableCollection<AlarmRowViewModel> AlarmRows { get; } = new();

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

    /// <summary>品牌标识。</summary>
    public string Brand => this.localizer["Shell_Brand"];

    /// <summary>界面定时器每一拍调用。</summary>
    public void Tick(DateTimeOffset nowUtc)
    {
        ClockText = nowUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        MachineStateSnapshot snapshot = this.monitor.Current;
        IsConnected = snapshot.ConnectionState == GatewayConnectionState.Connected;
        ConnectionText = this.localizer[IsConnected ? "Top_NcConnected" : "Top_NcDisconnected"];
        RoleText = this.localizer["Role_" + this.userSession.CurrentRole];

        RefreshBanner(snapshot);
        CurrentPage.OnTick(nowUtc);
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

    private void NavigateTo(PageKey key)
    {
        if (!this.pages.TryGetValue(key, out PageViewModelBase? page) || page == CurrentPage)
        {
            return;
        }

        this.history.Push(CurrentPage.Key);
        CurrentPage = page;
        page.OnActivated();
    }

    private void GoBack()
    {
        PageKey target = this.history.Count > 0 ? this.history.Pop() : PageKey.AutoGrinding;
        if (!this.pages.TryGetValue(target, out PageViewModelBase? page) || page == CurrentPage)
        {
            return;
        }

        CurrentPage = page;
        page.OnActivated();
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
