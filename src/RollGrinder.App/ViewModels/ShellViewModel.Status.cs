using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 外壳的状态部分（最终稿 4.1、4.7）：标题行（报警、连接、时间、急停）、方式方块、通道行，
/// 以及随 NC 方式切换机床区的基本画面。
/// </summary>
public sealed partial class ShellViewModel
{
    private const string NoAlarmCode = "0000";

    /// <summary>软件名（标题行左端）。</summary>
    public string Brand => this.localizer["Shell_Brand"];

    /// <summary>机床名（machine.json displayName）。现场数据，不翻译。</summary>
    public string MachineName { get; }

    [ObservableProperty]
    private string clockText = "--:--";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string connectionText = string.Empty;

    /// <summary>标题行上的用户：权限 · 用户名；未登录时写"未登录"。点它登录 / 签退。</summary>
    [ObservableProperty]
    private string userText = string.Empty;

    /// <summary>有没有未消除的报警（标题行中间红字）。</summary>
    [ObservableProperty]
    private bool hasAlarm;

    [ObservableProperty]
    private string alarmCode = NoAlarmCode;

    [ObservableProperty]
    private string alarmText = string.Empty;

    /// <summary>除了显示的这条，还有几条："+N"。</summary>
    [ObservableProperty]
    private string alarmMoreText = string.Empty;

    [ObservableProperty]
    private AlarmSeverity alarmSeverity = AlarmSeverity.Information;

    /// <summary>急停中：标题行整行变红"急停中——解除后按复位"，全部命令键变灰（最终稿 4.7）。</summary>
    [ObservableProperty]
    private bool isEmergencyStop;

    /// <summary>NC 操作方式。</summary>
    [ObservableProperty]
    private MachineMode machineMode = MachineMode.Unknown;

    /// <summary>方式方块上的字：JOG / MDA / AUTO。</summary>
    [ObservableProperty]
    private string modeText = string.Empty;

    /// <summary>通道行左：通道状态、是否已回参考点。</summary>
    [ObservableProperty]
    private string channelText = string.Empty;

    /// <summary>通道在运行（"◈ 运行"绿字）。</summary>
    [ObservableProperty]
    private bool isChannelRunning;

    /// <summary>通道行中：机床已起动（钥匙）。</summary>
    [ObservableProperty]
    private string machineOnText = string.Empty;

    /// <summary>通道行右：手持盒的轴、手轮倍率、使能。</summary>
    [ObservableProperty]
    private string pendantText = string.Empty;

    /// <summary>界面定时器每一拍调用。</summary>
    public void Tick(DateTimeOffset nowUtc)
    {
        ClockText = nowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

        MachineStateSnapshot snapshot = this.monitor.Current;
        IsConnected = snapshot.ConnectionState == GatewayConnectionState.Connected;
        ConnectionText = this.localizer[IsConnected ? "Top_NcConnected" : "Top_NcDisconnected"];
        UserText = this.userSession.IsSignedIn
            ? this.localizer["Role_" + this.userSession.CurrentRole] + " " + UserNameText
            : this.localizer["Role_SignedOut"];

        ApplyRunState(snapshot);
        ApplyEmergencyStop(snapshot);
        ApplyMode(snapshot);
        RefreshChannelRow(snapshot);
        RefreshAlarm();

        this.interaction.Tick();
        CurrentPage.OnTick(nowUtc);
    }

    /// <summary>点标题行的报警：去诊断 › 报警。</summary>
    [RelayCommand]
    private void OpenAlarms()
    {
        if (!IsOverlayOpen)
        {
            RequestArea(AreaKey.Diagnostics, "alarms");
        }
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

    private void ApplyEmergencyStop(MachineStateSnapshot snapshot)
    {
        bool stopped = snapshot.GetNumberOrNull(MachineTagKeys.EmergencyStop) is { } value && value != 0;
        if (stopped == IsEmergencyStop)
        {
            return;
        }

        IsEmergencyStop = stopped;
        foreach (PageViewModelBase page in this.pages.Values)
        {
            page.ApplyEmergencyStop(stopped);
        }

        if (stopped)
        {
            // 急停时待确认的事作废：问的时候机床还能动，现在不能了。
            this.interaction.Confirmations.Cancel();
        }
    }

    /// <summary>
    /// NC 方式变了：停在机床区基本画面上（没开子功能、没改动）时跟着换——JOG 手动磨削、AUTO 自动磨削。
    /// 停在手动动作页、作业向导或有改动时不动：人正在那儿干活。
    /// </summary>
    private void ApplyMode(MachineStateSnapshot snapshot)
    {
        MachineMode mode = snapshot.GetNumberOrNull(MachineTagKeys.OperatingMode) switch
        {
            null => MachineMode.Unknown,
            { } value => Enum.IsDefined(typeof(MachineMode), (int)value) ? (MachineMode)(int)value : MachineMode.Unknown,
        };

        ModeText = this.localizer["Mode_" + mode];
        if (mode == MachineMode)
        {
            return;
        }

        MachineMode = mode;
        PageKey entry = AreaCatalog.EntryPage(AreaKey.Machine, mode);
        if (AreaCatalog.IsModeBasePage(CurrentPage.Key)
            && CurrentPage.Key != entry
            && this.pages.ContainsKey(entry)
            && CurrentPage.ActiveSubViewKey is null
            && !CurrentPage.IsDirty
            && !IsOverlayOpen)
        {
            Commit(entry, isTaskReturn: false, returnTo: null, groupKey: null);
        }
    }

    private void RefreshChannelRow(MachineStateSnapshot snapshot)
    {
        double? channelState = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState);
        string state = channelState is null
            ? this.localizer["ChannelState_Unknown"]
            : this.localizer["ChannelState_" + (NcChannelState)(int)channelState.Value];
        IsChannelRunning = channelState is { } s && (NcChannelState)(int)s == NcChannelState.Running;

        string referenced = snapshot.GetNumberOrNull(MachineTagKeys.Referenced) switch
        {
            null => this.localizer["Channel_ReferencedUnknown"],
            0 => this.localizer["Channel_NotReferenced"],
            _ => this.localizer["Channel_Referenced"],
        };
        ChannelText = state + " · " + referenced;

        MachineOnText = snapshot.GetNumberOrNull(MachineTagKeys.MachineOn) switch
        {
            null => this.localizer["Channel_MachineOnUnknown"],
            0 => this.localizer["Channel_MachineOff"],
            _ => this.localizer["Channel_MachineOn"],
        };

        double? axis = snapshot.GetNumberOrNull(MachineTagKeys.PendantAxisSelect);
        double? factor = snapshot.GetNumberOrNull(MachineTagKeys.PendantHandwheelFactor);
        double? enabled = snapshot.GetNumberOrNull(MachineTagKeys.PendantEnable);
        PendantText = axis is null && factor is null && enabled is null
            ? this.localizer["Channel_PendantUnknown"]
            : this.localizer.Format(
                "Channel_PendantFormat",
                axis is { } a && a is >= 1 and <= 5 ? this.localizer["Pendant_Axis" + (int)a] : "—",
                factor is { } f ? f.ToString("0", CultureInfo.InvariantCulture) : "—",
                this.localizer[enabled is { } e && e != 0 ? "Pendant_Enabled" : "Pendant_Disabled"]);
    }

    private long lastAlarmId = -1;

    private void RefreshAlarm()
    {
        IReadOnlyList<AlarmEntry> entries = this.alarmLog.Snapshot();
        if (entries.Count == 0)
        {
            HasAlarm = false;
            AlarmCode = NoAlarmCode;
            AlarmText = string.Empty;
            AlarmMoreText = string.Empty;
            AlarmSeverity = AlarmSeverity.Information;
            this.lastAlarmId = -1;
            return;
        }

        AlarmEntry newest = entries[0];
        HasAlarm = true;
        AlarmMoreText = entries.Count > 1 ? this.localizer.Format("Top_AlarmMoreFormat", entries.Count - 1) : string.Empty;
        if (newest.Id == this.lastAlarmId)
        {
            return;
        }

        this.lastAlarmId = newest.Id;
        AlarmCode = newest.Code == AlarmCodes.Unspecified
            ? NoAlarmCode
            : newest.Code.ToString(CultureInfo.InvariantCulture);
        AlarmText = string.IsNullOrEmpty(newest.Detail)
            ? this.localizer[newest.MessageResourceKey]
            : this.localizer[newest.MessageResourceKey] + " · " + newest.Detail;
        AlarmSeverity = newest.Severity;
    }
}
