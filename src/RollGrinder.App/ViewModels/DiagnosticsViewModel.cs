using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Nc;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.App.ViewModels;

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
            FunctionKeyViewModel.Placeholder("Fn_ExportSnapshot", localizer, () => NotImplementedYet("Fn_ExportSnapshot"), FunctionKeyKind.Primary),
            FunctionKeyViewModel.Placeholder("Fn_RunLog", localizer, () => NotImplementedYet("Fn_RunLog")),
            FunctionKeyViewModel.Placeholder("Fn_TagMonitor", localizer, () => NotImplementedYet("Fn_TagMonitor")),
            FunctionKeyViewModel.Placeholder("Fn_MachineConfig", localizer, () => NotImplementedYet("Fn_MachineConfig")),
            FunctionKeyViewModel.Placeholder("Fn_TagMapping", localizer, () => NotImplementedYet("Fn_TagMapping")),
            FunctionKeyViewModel.Placeholder("Fn_AuditLog", localizer, () => NotImplementedYet("Fn_AuditLog")),
            FunctionKeyViewModel.Placeholder("Fn_BackupRestore", localizer, () => NotImplementedYet("Fn_BackupRestore")),
        });
    }

    public override PageKey Key => PageKey.Diagnostics;

    public override string TitleResourceKey => "Page_Diagnostics";

    public ObservableCollection<DiagnosticRowViewModel> ConnectionRows { get; }

    public ObservableCollection<DiagnosticRowViewModel> CompensationRows { get; }

    public ObservableCollection<CapabilityRow> Capabilities { get; }

    public ObservableCollection<AlarmRowViewModel> Events { get; } = new();

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
