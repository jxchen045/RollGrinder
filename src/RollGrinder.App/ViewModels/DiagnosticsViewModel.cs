using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Nc;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Audit;
using RollGrinder.Services.Calibration;
using RollGrinder.Services.Diagnostics;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.App.ViewModels;

/// <summary>变量监视里的一行：逻辑名、原始值、质量位。</summary>
public sealed partial class TagMonitorRowViewModel : ObservableObject
{
    public TagMonitorRowViewModel(string key)
    {
        this.key = key ?? throw new ArgumentNullException(nameof(key));
    }

    [ObservableProperty]
    private string key;

    [ObservableProperty]
    private string valueText = "--";

    [ObservableProperty]
    private bool isGood;
}

/// <summary>诊断页里的一行"名称 → 取值"。</summary>
public sealed partial class DiagnosticRowViewModel : ObservableObject
{
    public DiagnosticRowViewModel(string labelResourceKey, IStringLocalizer localizer)
    {
        Label = localizer[labelResourceKey];
    }

    public string Label { get; }

    [ObservableProperty]
    private string valueText = "--";

    [ObservableProperty]
    private bool isGood;

    [ObservableProperty]
    private bool isBad;
}

/// <summary>机床能力一项：按 machine.json 的选件显示已配置/未配置。</summary>
public sealed record CapabilityRow(string Label, string StateText, bool IsConfigured);

/// <summary>改动记录里的一行（诊断 › 改动记录）。</summary>
public sealed record ChangeLogRowViewModel(string TimeText, string ByText, string AreaText, string Item, string OldText, string NewText);

/// <summary>
/// 诊断区（界面最终稿 5.12）：横键 报警 · 变量监视 · 改动记录 · 运行日志 · 空 · 连接与接口 · 备份与恢复。
/// 这一区只如实显示：拿不到的量写"未配置"，不做假。标题行的报警点一下就到"报警"组。
/// 机床配置与标签映射的编辑在调试区（<see cref="CommissioningViewModel"/>）。
/// </summary>
public sealed partial class DiagnosticsViewModel : PageViewModelBase
{
    /// <summary>横键"报警"（标题行报警点进来的就是它）。</summary>
    public const string AlarmsGroup = "alarms";

    /// <summary>横键"变量监视"。</summary>
    public const string TagMonitorGroup = "tagMonitor";

    /// <summary>横键"改动记录"（补偿页竖键"改动记录"也打开它）。</summary>
    public const string AuditGroup = "audit";

    /// <summary>横键"运行日志"。</summary>
    public const string RunLogGroup = "runLog";

    /// <summary>横键"连接与接口"。</summary>
    public const string ConnectionGroup = "connection";

    /// <summary>横键"备份与恢复"。</summary>
    public const string BackupGroup = "backup";

    private readonly Dictionary<string, FunctionKeyViewModel> groupKeys = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<FunctionKeyViewModel?> alarmKeys;
    private readonly IReadOnlyList<FunctionKeyViewModel?> reloadKeys;
    private readonly IReadOnlyList<FunctionKeyViewModel?> backupKeys;
    private readonly IChangeLog changeLog;
    private readonly IMachineMonitor monitor;
    private readonly MachineDescription machine;
    private readonly ITagMap tagMap;
    private readonly NcJobTranslator translator;
    private readonly IAlarmLog alarmLog;
    private readonly IAppOptions options;
    private readonly ICalibrationService calibration;
    private readonly IDiagnosticsExportService exports;

    private readonly DiagnosticRowViewModel connection;
    private readonly DiagnosticRowViewModel snapshotAge;
    private readonly DiagnosticRowViewModel softwareVersion;
    private readonly DiagnosticRowViewModel machineConfig;
    private readonly DiagnosticRowViewModel tagMapVersion;

    /// <summary>NC 侧报废保护（关系设计 7.3）：报废直径有没有下发给 NC、NC 用没用上。</summary>
    private readonly DiagnosticRowViewModel scrapProtection;
    private readonly DiagnosticRowViewModel machineSerial;

    private readonly DiagnosticRowViewModel measuringChannel;
    private readonly DiagnosticRowViewModel compensationAxis;
    private readonly DiagnosticRowViewModel realtimeOffset;
    private readonly DiagnosticRowViewModel strokeVersion;

    private long lastShownAlarmId = -1;

    public DiagnosticsViewModel(
        IMachineMonitor monitor,
        MachineDescription machine,
        ITagMap tagMap,
        NcJobTranslator translator,
        IAlarmLog alarmLog,
        IAppOptions options,
        IStringLocalizer localizer,
        ICalibrationService calibration,
        IDiagnosticsExportService exports,
        IChangeLog changeLog,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.translator = translator ?? throw new ArgumentNullException(nameof(translator));
        this.alarmLog = alarmLog ?? throw new ArgumentNullException(nameof(alarmLog));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.exports = exports ?? throw new ArgumentNullException(nameof(exports));
        this.changeLog = changeLog ?? throw new ArgumentNullException(nameof(changeLog));

        this.connection = new DiagnosticRowViewModel("Diag_Connection", localizer);
        this.snapshotAge = new DiagnosticRowViewModel("Diag_SnapshotAge", localizer);
        this.softwareVersion = new DiagnosticRowViewModel("Diag_SoftwareVersion", localizer);
        this.machineConfig = new DiagnosticRowViewModel("Diag_MachineConfig", localizer);
        this.tagMapVersion = new DiagnosticRowViewModel("Diag_TagMap", localizer);
        this.machineSerial = new DiagnosticRowViewModel("Diag_MachineSerial", localizer);
        this.scrapProtection = new DiagnosticRowViewModel("Diag_ScrapProtection", localizer);

        ConnectionRows = new ObservableCollection<DiagnosticRowViewModel>
        {
            this.connection, this.snapshotAge, this.softwareVersion,
            this.machineConfig, this.tagMapVersion, this.machineSerial, this.scrapProtection,
        };

        this.measuringChannel = new DiagnosticRowViewModel("Diag_MeasuringChannel", localizer);
        this.compensationAxis = new DiagnosticRowViewModel("Diag_CompensationAxis", localizer);
        this.realtimeOffset = new DiagnosticRowViewModel("Diag_RealtimeOffset", localizer);
        this.strokeVersion = new DiagnosticRowViewModel("Diag_StrokeVersion", localizer);

        CompensationRows = new ObservableCollection<DiagnosticRowViewModel>
        {
            this.measuringChannel, this.compensationAxis, this.realtimeOffset, this.strokeVersion,
        };

        Capabilities = new ObservableCollection<CapabilityRow>(
            machine.Options.Select(option => new CapabilityRow(
                OptionLabel(option.Key),
                localizer[option.Value ? "Common_Configured" : "Common_NotConfigured"],
                option.Value)));

        foreach ((string group, string label) in new[]
        {
            (AlarmsGroup, "Fn_Alarms"), (TagMonitorGroup, "Fn_TagMonitor"), (AuditGroup, "Fn_AuditLog"), (RunLogGroup, "Fn_RunLog"),
            (ConnectionGroup, "Fn_Connection"), (BackupGroup, "Fn_BackupRestore"),
        })
        {
            string target = group;
            this.groupKeys[group] = FunctionKeyViewModel.ForAction(label, localizer, () => ShowGroup(target));
        }

        SetFunctionKeys(new FunctionKeyViewModel?[]
        {
            this.groupKeys[AlarmsGroup], this.groupKeys[TagMonitorGroup], this.groupKeys[AuditGroup], this.groupKeys[RunLogGroup],
            null, this.groupKeys[ConnectionGroup], this.groupKeys[BackupGroup],
        });

        this.alarmKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_ClearHmiAlarms", new RelayCommand(AskClearHmiAlarms), localizer, FunctionKeyKind.Danger),
            // 导出故障快照要挑一个文件路径，对话框在视图里；这个键只负责触发。
            new FunctionKeyViewModel("Vk_ExportSnapshot", RequestSnapshotExportCommand, localizer),
        };
        this.reloadKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_Reload", new AsyncRelayCommand(() => ReloadGroupAsync(CancellationToken.None)), localizer),
        };
        this.backupKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_Backup", RequestBackupCommand, localizer, FunctionKeyKind.Primary),
            new FunctionKeyViewModel("Vk_ExportSnapshot", RequestSnapshotExportCommand, localizer),
        };

        ShowGroup(AlarmsGroup);
    }

    /// <summary>当前是哪一组。</summary>
    [ObservableProperty]
    private string group = AlarmsGroup;

    public override bool ShowGroup(string groupKey)
    {
        if (!this.groupKeys.TryGetValue(groupKey, out FunctionKeyViewModel? key))
        {
            return false;
        }

        Group = groupKey;
        MarkActiveFunctionKey(key);
        SetVerticalKeys(groupKey switch
        {
            AlarmsGroup => this.alarmKeys,
            AuditGroup or RunLogGroup => this.reloadKeys,
            BackupGroup => this.backupKeys,
            _ => Array.Empty<FunctionKeyViewModel?>(),
        });
        _ = ReloadGroupAsync(CancellationToken.None);
        return true;
    }

    private Task ReloadGroupAsync(CancellationToken cancellationToken) => Group switch
    {
        AuditGroup => RunGuardedAsync(LoadChangeLogAsync, cancellationToken),
        RunLogGroup => LoadRunLogAsync(cancellationToken),
        _ => Task.CompletedTask,
    };

    /// <summary>选中的报警：右边写详情与消除方法。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlarmResetText), nameof(HasSelectedAlarm))]
    private AlarmRowViewModel? selectedAlarm;

    public bool HasSelectedAlarm => SelectedAlarm is not null;

    /// <summary>选中报警怎么消：PLC 报警按按钮板"故障复位"，NC 报警按"复位"，上位机报警在这里清除。</summary>
    public string AlarmResetText => SelectedAlarm is null ? string.Empty : Localizer[ResetResourceKey(SelectedAlarm.Code)];

    /// <summary>按报警号认来源、决定怎么消（纯函数，单测覆盖）。</summary>
    public static string ResetResourceKey(int code) => code switch
    {
        >= AlarmCodes.RangeStart and <= AlarmCodes.RangeEnd => "Alarm_ResetHmi",
        >= 500000 => "Alarm_ResetPlc",
        AlarmCodes.Unspecified => "Alarm_ResetHmi",
        _ => "Alarm_ResetNc",
    };

    /// <summary>竖键"清除上位机报警…"：只清上位机自己的；机床报警由机床消，这里清掉它下个周期又会回来。</summary>
    private void AskClearHmiAlarms() => Ask("Diag_AskClearAlarms", () =>
    {
        this.alarmLog.Clear();
        this.lastShownAlarmId = -1;
        RefreshEvents();
        Say("Diag_AlarmsCleared");
    });

    /// <summary>改动记录（新的在前）。</summary>
    public ObservableCollection<ChangeLogRowViewModel> ChangeLogRows { get; } = new();

    /// <summary>改动记录（补偿设定、机床配置、标签映射、标定）。</summary>
    private async Task LoadChangeLogAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ChangeLogEntry> entries = await this.changeLog.ListAsync(200, cancellationToken).ConfigureAwait(true);
        ChangeLogRows.Clear();
        foreach (ChangeLogEntry entry in entries)
        {
            ChangeLogRows.Add(new ChangeLogRowViewModel(
                entry.ChangedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                entry.ChangedBy,
                Localizer["ChangeArea_" + entry.Area],
                entry.Item,
                entry.OldValue ?? "--",
                entry.NewValue ?? "--"));
        }
    }

    public override PageKey Key => PageKey.Diagnostics;

    public override string TitleResourceKey => "Page_Diagnostics";

    /// <summary>离线也开放：连不上机床时，正是要到这里看报警、连接、运行日志和做备份的时候。</summary>
    public override bool WorksOffline => true;

    /// <summary>运行日志的末尾几百行。</summary>
    [ObservableProperty]
    private string inspectorText = string.Empty;

    /// <summary>界面要导出诊断快照时触发；路径由视图选。</summary>
    public event EventHandler? SnapshotExportRequested;

    /// <summary>界面要备份时触发；路径由视图选。</summary>
    public event EventHandler? BackupRequested;

    [RelayCommand]
    private void RequestSnapshotExport() => SnapshotExportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>导一份诊断快照；路径由界面选。</summary>
    public Task ExportSnapshotAsync(string filePath, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            await this.exports.ExportSnapshotAsync(filePath, token).ConfigureAwait(true);
            Alarms.Raise(AlarmSeverity.Information, "Diag_SnapshotExported", filePath, AlarmCodes.Unspecified);
        }, cancellationToken);

    /// <summary>备份 config/ 与 data/；路径由界面选。</summary>
    public Task BackupAsync(string filePath, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            await this.exports.BackupAsync(filePath, token).ConfigureAwait(true);
            Alarms.Raise(AlarmSeverity.Information, "Diag_BackupWritten", filePath, AlarmCodes.Unspecified);
        }, cancellationToken);

    [RelayCommand]
    private void RequestBackup() => BackupRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>运行日志：日志目录里最新的那一个文件。</summary>
    private Task LoadRunLogAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            string? newest = NewestLogFile(this.options.LogDirectory);
            InspectorText = newest is null
                ? Localizer["Diag_NoRunLog"]
                // 只看末尾：日志一天能长到几十兆，全读进来界面就卡住了。
                : await TailAsync(newest, RunLogTailLines, token).ConfigureAwait(true);
        }, cancellationToken);

    /// <summary>运行日志一次看多少行。</summary>
    private const int RunLogTailLines = 400;

    private static string? NewestLogFile(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string? newest = null;
        DateTime newestAt = DateTime.MinValue;
        foreach (string path in Directory.EnumerateFiles(directory, "*.log"))
        {
            DateTime at = File.GetLastWriteTimeUtc(path);
            if (at > newestAt)
            {
                newestAt = at;
                newest = path;
            }
        }

        return newest;
    }

    private static async Task<string> TailAsync(string path, int lines, CancellationToken cancellationToken)
    {
        // Serilog 还开着这个文件，所以要按共享读取打开，不能独占。
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        var tail = new Queue<string>(lines);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            if (tail.Count == lines)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return string.Join(Environment.NewLine, tail);
    }

    public ObservableCollection<DiagnosticRowViewModel> ConnectionRows { get; }

    public ObservableCollection<DiagnosticRowViewModel> CompensationRows { get; }

    public ObservableCollection<CapabilityRow> Capabilities { get; }

    public ObservableCollection<AlarmRowViewModel> Events { get; } = new();

    /// <summary>变量监视的行。只在这一组开着时刷新。</summary>
    public ObservableCollection<TagMonitorRowViewModel> TagMonitorRows { get; } = new();

    /// <summary>tagmap 缺失的必需变量；为空表示契约校验通过。</summary>
    public ObservableCollection<string> MissingTags { get; } = new();

    [ObservableProperty]
    private DegradationLevel degradationLevel = DegradationLevel.Full;

    [ObservableProperty]
    private string tagMapCheckText = "--";

    [ObservableProperty]
    private bool tagMapIsValid;

    [ObservableProperty]
    private string alarmRangeNoteText = string.Empty;

    public override void OnActivated()
    {
        this.softwareVersion.ValueText =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "--";
        this.machineConfig.ValueText = string.Create(
            CultureInfo.InvariantCulture, $"{this.machine.MachineId} · v{this.machine.SchemaVersion}");
        this.machineSerial.ValueText = this.machine.MachineId;

        MissingTags.Clear();
        foreach (string missing in this.translator.FindMissingRequiredTags())
        {
            MissingTags.Add(missing);
        }

        TagMapIsValid = MissingTags.Count == 0;
        this.tagMapVersion.ValueText = string.Create(
            CultureInfo.InvariantCulture, $"{this.tagMap.Tags.Count} tags");
        TagMapCheckText = Localizer[TagMapIsValid ? "Diag_TagMapValid" : "Diag_TagMapIncomplete"];

        this.measuringChannel.ValueText = this.machine.MeasurementChannels.Count == 0
            ? Localizer["Common_NotConfigured"]
            : string.Join(" · ", this.machine.MeasurementChannels
                .Where(channel => channel.IsPresent)
                .Select(channel => channel.Name));

        AxisDescription? crownAxis = this.machine.Axes.FirstOrDefault(axis =>
            axis.IsPresent && string.Equals(axis.Role, ProfileViewModel.CrownAxisRole, StringComparison.Ordinal));
        this.compensationAxis.ValueText = crownAxis?.Name ?? Localizer["Common_NotConfigured"];

        AlarmRangeNoteText = Localizer.Format(
            "Diag_AlarmRangeNote", AlarmCodes.RangeStart, AlarmCodes.RangeEnd);
    }

    public override void OnTick(DateTimeOffset nowUtc)
    {
        MachineStateSnapshot snapshot = this.monitor.Current;

        // 报废直径映射了才下发；NC 回报"已启用"就是双保险，没回报只能说"已下发"。
        bool scrapMapped = this.tagMap.TryResolve(MachineTagKeys.JobScrapDiameterMm, out _);
        this.scrapProtection.ValueText = !scrapMapped
            ? Localizer["Diag_ScrapProtectionOff"]
            : snapshot.GetNumberOrNull(MachineTagKeys.ScrapProtectionActive) switch
            {
                double active when active != 0.0 => Localizer["Diag_ScrapProtectionActive"],
                double => Localizer["Diag_ScrapProtectionInactive"],
                _ => Localizer["Diag_ScrapProtectionSent"],
            };

        this.connection.ValueText = Localizer["ConnectionState_" + snapshot.ConnectionState];
        this.connection.IsGood = snapshot.ConnectionState == GatewayConnectionState.Connected;
        this.connection.IsBad = snapshot.ConnectionState == GatewayConnectionState.Faulted;

        this.snapshotAge.ValueText =
            (nowUtc - snapshot.CapturedAtUtc).TotalSeconds.ToString("F2", CultureInfo.CurrentCulture) + " s";

        double? offset = snapshot.GetNumberOrNull(MachineTagKeys.CompensationRealtimeOffsetMm);
        this.realtimeOffset.ValueText = offset is null
            ? "--"
            : (offset.Value >= 0.0 ? "+" : string.Empty) + offset.Value.ToString("F4", CultureInfo.CurrentCulture) + " mm";

        double? version = snapshot.GetNumberOrNull(MachineTagKeys.CompensationStrokeVersion);
        this.strokeVersion.ValueText = version is null
            ? "--"
            : "v " + ((int)version.Value).ToString(CultureInfo.InvariantCulture);

        double? level = snapshot.GetNumberOrNull(MachineTagKeys.CompensationDegradationLevel);
        if (level is not null && Enum.IsDefined((DegradationLevel)(int)level.Value))
        {
            DegradationLevel = (DegradationLevel)(int)level.Value;
        }

        RefreshEvents();

        if (Group == TagMonitorGroup)
        {
            RefreshTagMonitor(snapshot);
        }
    }

    /// <summary>变量监视：把当前快照里的每个变量原样列出来，不做单位换算也不补默认值。</summary>
    private void RefreshTagMonitor(MachineStateSnapshot snapshot)
    {
        // 快照里的变量数以十计，条数稳定，整行重建比逐行比对更简单也不会闪。
        if (TagMonitorRows.Count != snapshot.Values.Count)
        {
            TagMonitorRows.Clear();
            foreach (TagValue value in snapshot.Values)
            {
                TagMonitorRows.Add(new TagMonitorRowViewModel(value.Key));
            }
        }

        for (int i = 0; i < snapshot.Values.Count; i++)
        {
            TagValue value = snapshot.Values[i];
            TagMonitorRowViewModel row = TagMonitorRows[i];
            row.Key = value.Key;
            row.ValueText = FormatRaw(value.Raw);
            row.IsGood = value.IsGood;
        }
    }

    /// <summary>变量监视里的值：浮点数最多 6 位小数（原样显示是 -0.0001372096851435361，列宽装不下也没人看得清）。</summary>
    private static string FormatRaw(object? raw) => raw switch
    {
        null => "--",
        double number => number.ToString("0.######", CultureInfo.InvariantCulture),
        float number => number.ToString("0.######", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => raw.ToString() ?? "--",
    };

    private void RefreshEvents()
    {
        IReadOnlyList<AlarmEntry> entries = this.alarmLog.Snapshot();
        if (entries.Count == 0)
        {
            Events.Clear();
            SelectedAlarm = null;
            this.lastShownAlarmId = -1;
            return;
        }

        if (entries[0].Id == this.lastShownAlarmId)
        {
            return;
        }

        this.lastShownAlarmId = entries[0].Id;
        long? keep = SelectedAlarm?.Id;
        Events.Clear();
        foreach (AlarmEntry entry in entries)
        {
            Events.Add(new AlarmRowViewModel(entry, Localizer));
        }

        SelectedAlarm = Events.FirstOrDefault(row => row.Id == keep) ?? Events.FirstOrDefault();
    }

    private string OptionLabel(string optionKey)
    {
        string localized = Localizer["Option_" + optionKey];
        return localized.StartsWith('!') ? optionKey : localized;
    }
}
