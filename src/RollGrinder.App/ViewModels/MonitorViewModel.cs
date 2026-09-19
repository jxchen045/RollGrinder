using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Units;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.App.ViewModels;

/// <summary>监控界面上的一行轴数据。</summary>
public sealed partial class AxisRowViewModel : ObservableObject
{
    public AxisRowViewModel(AxisDescription axis)
    {
        ArgumentNullException.ThrowIfNull(axis);
        Name = axis.Name;
        Role = axis.Role;
    }

    public string Name { get; }

    public string Role { get; }

    [ObservableProperty]
    private string positionText = "--";

    [ObservableProperty]
    private string speedText = "--";
}

/// <summary>
/// 磨削监控。只读取监视服务发布的快照，自身不碰网关；
/// 刷新由界面定时器按 hmi.json 的 uiRefreshHz 驱动，与数据到达频率无关。
/// </summary>
public sealed partial class MonitorViewModel : ViewModelBase
{
    private readonly IMachineMonitor monitor;
    private readonly IStringLocalizer localizer;
    private readonly HmiSettings settings;
    private readonly List<(double Seconds, double DiameterMm)> diameterHistory = new();
    private readonly DateTimeOffset startedAtUtc;

    public MonitorViewModel(
        IMachineMonitor monitor,
        MachineDescription machine,
        HmiSettings settings,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        TimeProvider timeProvider)
        : base(alarms)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        ArgumentNullException.ThrowIfNull(machine);
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.startedAtUtc = timeProvider.GetUtcNow();
        Axes = new ObservableCollection<AxisRowViewModel>(
            machine.Axes.Where(axis => axis.IsPresent).Select(axis => new AxisRowViewModel(axis)));
    }

    /// <summary>本台机床装有的轴。</summary>
    public ObservableCollection<AxisRowViewModel> Axes { get; }

    [ObservableProperty]
    private string connectionText = "--";

    [ObservableProperty]
    private string channelStateText = "--";

    [ObservableProperty]
    private string programNameText = "--";

    [ObservableProperty]
    private string measuredDiameterText = "--";

    [ObservableProperty]
    private string snapshotAgeText = "--";

    /// <summary>实测直径的趋势（秒，直径 mm），供图表使用。</summary>
    public IReadOnlyList<(double Seconds, double DiameterMm)> DiameterHistory => this.diameterHistory;

    /// <summary>趋势数据有更新。</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>界面定时器每一拍调用一次：取最新快照并刷新显示。</summary>
    public void Refresh(DateTimeOffset nowUtc)
    {
        MachineStateSnapshot snapshot = this.monitor.Current;

        ConnectionText = this.localizer["ConnectionState_" + snapshot.ConnectionState];
        SnapshotAgeText = FormatSeconds((nowUtc - snapshot.CapturedAtUtc).TotalSeconds);

        double? channelState = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState);
        ChannelStateText = channelState is null
            ? "--"
            : this.localizer["ChannelState_" + (NcChannelState)(int)channelState.Value];

        ProgramNameText = string.IsNullOrEmpty(snapshot.GetTextOrNull(MachineTagKeys.ProgramName))
            ? "--"
            : snapshot.GetTextOrNull(MachineTagKeys.ProgramName)!;

        foreach (AxisRowViewModel axis in Axes)
        {
            axis.PositionText = Format(snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(axis.Name)), "F3");
            axis.SpeedText = Format(snapshot.GetNumberOrNull(MachineTagKeys.AxisActualSpeedRpm(axis.Name)), "F1");
        }

        double? measuredDiameterMm = snapshot.GetNumberOrNull(MachineTagKeys.MeasuredDiameterMm);
        MeasuredDiameterText = Format(measuredDiameterMm, "F4");

        if (measuredDiameterMm is not null)
        {
            AppendHistory((nowUtc - this.startedAtUtc).TotalSeconds, measuredDiameterMm.Value);
        }
    }

    private void AppendHistory(double seconds, double diameterMm)
    {
        this.diameterHistory.Add((seconds, diameterMm));

        double horizonSeconds = seconds - this.settings.ChartHistorySeconds;
        int expired = 0;
        while (expired < this.diameterHistory.Count && this.diameterHistory[expired].Seconds < horizonSeconds)
        {
            expired++;
        }

        if (expired > 0)
        {
            this.diameterHistory.RemoveRange(0, expired);
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Format(double? value, string format) =>
        value is null ? "--" : value.Value.ToString(format, CultureInfo.CurrentCulture);

    private static string FormatSeconds(double seconds) =>
        seconds.ToString("F1", CultureInfo.CurrentCulture);
}
