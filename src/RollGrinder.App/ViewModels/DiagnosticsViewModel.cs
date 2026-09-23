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
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data;
using RollGrinder.Nc;
using RollGrinder.Services.Alarms;
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

/// <summary>
/// 诊断。版面见 docs/design/B-Diag-诊断.html。
/// 这一页只如实显示：拿不到的量写"未配置"，不做假。
/// </summary>
public sealed partial class DiagnosticsViewModel : PageViewModelBase
{
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
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.translator = translator ?? throw new ArgumentNullException(nameof(translator));
        this.alarmLog = alarmLog ?? throw new ArgumentNullException(nameof(alarmLog));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.exports = exports ?? throw new ArgumentNullException(nameof(exports));

        this.connection = new DiagnosticRowViewModel("Diag_Connection", localizer);
        this.snapshotAge = new DiagnosticRowViewModel("Diag_SnapshotAge", localizer);
        this.softwareVersion = new DiagnosticRowViewModel("Diag_SoftwareVersion", localizer);
        this.machineConfig = new DiagnosticRowViewModel("Diag_MachineConfig", localizer);
        this.tagMapVersion = new DiagnosticRowViewModel("Diag_TagMap", localizer);
        this.machineSerial = new DiagnosticRowViewModel("Diag_MachineSerial", localizer);

        ConnectionRows = new ObservableCollection<DiagnosticRowViewModel>
        {
            this.connection, this.snapshotAge, this.softwareVersion,
            this.machineConfig, this.tagMapVersion, this.machineSerial,
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

        SetFunctionKeys(new[]
        {
            // 导出诊断快照要挑一个文件路径，对话框在视图里；这个键只负责触发。
            new FunctionKeyViewModel("Fn_ExportSnapshot", RequestSnapshotExportCommand, localizer, FunctionKeyKind.Primary),
            new FunctionKeyViewModel("Fn_RunLog", OpenRunLogCommand, localizer),
            // 二级子视图：打开后导航槽变成"返回 诊断"。
            FunctionKeyViewModel.ForAction("Fn_TagMonitor", localizer, () => Navigator.OpenSubView(TagMonitorSubView)),
            new FunctionKeyViewModel("Fn_MachineConfig", OpenMachineConfigCommand, localizer),
            new FunctionKeyViewModel("Fn_TagMapping", OpenTagMappingCommand, localizer),
            new FunctionKeyViewModel("Fn_AuditLog", OpenAuditLogCommand, localizer),
            new FunctionKeyViewModel("Fn_BackupRestore", RequestBackupCommand, localizer),
        });
    }

    public override PageKey Key => PageKey.Diagnostics;

    public override string TitleResourceKey => "Page_Diagnostics";

    public override string MenuHintResourceKey => "Menu_DiagnosticsHint";

    /// <summary>变量监视子视图的资源键，同时用作面包屑文案。</summary>
    public const string TagMonitorSubView = "SubView_TagMonitor";

    /// <summary>机床配置子视图。</summary>
    public const string MachineConfigSubView = "SubView_MachineConfig";

    /// <summary>运行日志子视图：与机床配置共用检视面板，但面包屑要说清楚看的是什么。</summary>
    public const string RunLogSubView = "SubView_RunLog";

    /// <summary>变量映射子视图。</summary>
    public const string TagMappingSubView = "SubView_TagMapping";

    /// <summary>标定审计子视图。</summary>
    public const string AuditLogSubView = "SubView_AuditLog";

    /// <summary>
    /// 只读的文本视图：机床配置、变量映射、运行日志都摆在这里。
    ///
    /// 只显示不编辑：这三样东西改错了机床就动不了，改它们得开文件——
    /// 界面上能看见是为了现场能对着电话把值念给人听，不是为了在这里改。
    /// </summary>
    [ObservableProperty]
    private string inspectorText = string.Empty;

    /// <summary>标定审计的行：每一项最后一次是谁改的。</summary>
    public ObservableCollection<LabelValueViewModel> AuditRows { get; } = new();

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

    /// <summary>机床配置：把 machine.json 原样摆出来。</summary>
    [RelayCommand]
    private Task OpenMachineConfigAsync(CancellationToken cancellationToken) =>
        ShowFileAsync(this.options.MachineConfigFilePath, MachineConfigSubView, cancellationToken);

    /// <summary>变量映射：把 tagmap.json 原样摆出来。</summary>
    [RelayCommand]
    private Task OpenTagMappingAsync(CancellationToken cancellationToken) =>
        ShowFileAsync(this.options.TagMapFilePath, TagMappingSubView, cancellationToken);

    /// <summary>运行日志：日志目录里最新的那一个文件。</summary>
    [RelayCommand]
    private Task OpenRunLogAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            string? newest = NewestLogFile(this.options.LogDirectory);
            if (newest is null)
            {
                InspectorText = Localizer["Diag_NoRunLog"];
            }
            else
            {
                // 只看末尾：日志一天能长到几十兆，全读进来界面就卡住了。
                InspectorText = await TailAsync(newest, RunLogTailLines, token).ConfigureAwait(true);
            }

            Navigator.OpenSubView(RunLogSubView);
        }, cancellationToken);

    /// <summary>标定审计：每一项最后一次是谁在什么时候改的。</summary>
    [RelayCommand]
    private Task OpenAuditLogAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            AuditRows.Clear();
            foreach (CalibrationAudit entry in await this.calibration
                .LoadAuditAsync(token).ConfigureAwait(true))
            {
                AuditRows.Add(new LabelValueViewModel(
                    "Parameter_" + entry.ParameterKey,
                    Localizer.Format(
                        "Diag_AuditEntryFormat",
                        entry.ChangedBy,
                        entry.ChangedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                    Localizer));
            }

            Navigator.OpenSubView(AuditLogSubView);
        }, cancellationToken);

    /// <summary>运行日志一次看多少行。</summary>
    private const int RunLogTailLines = 400;

    private Task ShowFileAsync(string path, string subViewKey, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            InspectorText = File.Exists(path)
                ? await File.ReadAllTextAsync(path, token).ConfigureAwait(true)
                : Localizer.Format("Diag_FileMissingFormat", path);

            Navigator.OpenSubView(subViewKey);
        }, cancellationToken);

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

    /// <summary>变量监视子视图的行。只在子视图打开时刷新。</summary>
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

        if (ActiveSubViewKey == TagMonitorSubView)
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
            row.ValueText = value.Raw?.ToString() ?? "--";
            row.IsGood = value.IsGood;
        }
    }

    private void RefreshEvents()
    {
        IReadOnlyList<AlarmEntry> entries = this.alarmLog.Snapshot();
        if (entries.Count == 0)
        {
            Events.Clear();
            this.lastShownAlarmId = -1;
            return;
        }

        if (entries[0].Id == this.lastShownAlarmId)
        {
            return;
        }

        this.lastShownAlarmId = entries[0].Id;
        Events.Clear();
        foreach (AlarmEntry entry in entries)
        {
            Events.Add(new AlarmRowViewModel(entry, Localizer));
        }
    }

    private string OptionLabel(string optionKey)
    {
        string localized = Localizer["Option_" + optionKey];
        return localized.StartsWith('!') ? optionKey : localized;
    }
}
