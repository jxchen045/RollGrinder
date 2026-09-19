using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 主界面外壳：页签、报警条与统一刷新节拍。
/// 刷新由界面定时器按 hmi.json 的 uiRefreshHz（5–10 Hz）驱动，
/// 报警也在同一拍里从报警表取快照，避免后台线程直接动界面集合。
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly IAlarmLog alarmLog;
    private readonly IStringLocalizer localizer;

    private long lastShownAlarmId = -1;

    public ShellViewModel(
        MonitorViewModel monitor,
        JobEditorViewModel jobEditor,
        MeasurementViewModel measurement,
        RecordsViewModel records,
        IAlarmLog alarmLog,
        IAppOptions options,
        MachineDescription machine,
        HmiSettings settings,
        IStringLocalizer localizer)
        : base(alarmLog)
    {
        Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        JobEditor = jobEditor ?? throw new ArgumentNullException(nameof(jobEditor));
        Measurement = measurement ?? throw new ArgumentNullException(nameof(measurement));
        Records = records ?? throw new ArgumentNullException(nameof(records));
        this.alarmLog = alarmLog ?? throw new ArgumentNullException(nameof(alarmLog));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(settings);

        MachineDisplayName = machine.DisplayName;
        GatewayName = options.Gateway.ToString();
        RefreshInterval = TimeSpan.FromSeconds(1.0 / settings.UiRefreshHz);
    }

    public MonitorViewModel Monitor { get; }

    public JobEditorViewModel JobEditor { get; }

    public MeasurementViewModel Measurement { get; }

    public RecordsViewModel Records { get; }

    public ObservableCollection<AlarmRowViewModel> AlarmRows { get; } = new();

    public string MachineDisplayName { get; }

    public string GatewayName { get; }

    /// <summary>界面刷新周期。</summary>
    public TimeSpan RefreshInterval { get; }

    [ObservableProperty]
    private string latestAlarmText = string.Empty;

    [ObservableProperty]
    private bool hasActiveAlarm;

    /// <summary>界面定时器每一拍调用。</summary>
    public void Tick(DateTimeOffset nowUtc)
    {
        Monitor.Refresh(nowUtc);
        RefreshAlarms();
    }

    [RelayCommand]
    private void ClearAlarms()
    {
        this.alarmLog.Clear();
        this.lastShownAlarmId = -1;
        AlarmRows.Clear();
        LatestAlarmText = string.Empty;
        HasActiveAlarm = false;
    }

    private void RefreshAlarms()
    {
        IReadOnlyList<AlarmEntry> entries = this.alarmLog.Snapshot();
        if (entries.Count == 0)
        {
            if (AlarmRows.Count > 0)
            {
                AlarmRows.Clear();
                LatestAlarmText = string.Empty;
                HasActiveAlarm = false;
            }

            return;
        }

        if (entries[0].Id == this.lastShownAlarmId)
        {
            return;
        }

        this.lastShownAlarmId = entries[0].Id;
        AlarmRows.Clear();
        foreach (AlarmEntry entry in entries)
        {
            AlarmRows.Add(new AlarmRowViewModel(entry, this.localizer));
        }

        AlarmRowViewModel newest = AlarmRows[0];
        LatestAlarmText = string.IsNullOrEmpty(newest.Detail)
            ? newest.Message
            : newest.Message + " — " + newest.Detail;
        HasActiveAlarm = entries.Any(entry => entry.Severity == AlarmSeverity.Error);
    }
}
