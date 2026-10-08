using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 一盏状态灯：名字 + 当前状态的字，开为绿、关为灰、读不到画虚框（修改稿 5.5）。
/// 一盏灯可以由几项合成（内外测量臂合成"测量臂"）。
/// </summary>
public sealed partial class StatusLampViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;
    private readonly IReadOnlyList<StatusIndicator> sources;
    private readonly string onResourceKey;
    private readonly string offResourceKey;

    public StatusLampViewModel(StatusIndicator indicator, IStringLocalizer localizer)
        : this(indicator.LabelResourceKey, indicator.OnResourceKey, indicator.OffResourceKey, new[] { indicator }, localizer)
    {
    }

    public StatusLampViewModel(
        string labelResourceKey,
        string onResourceKey,
        string offResourceKey,
        IReadOnlyList<StatusIndicator> sources,
        IStringLocalizer localizer)
    {
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.onResourceKey = onResourceKey;
        this.offResourceKey = offResourceKey;
        LabelResourceKey = labelResourceKey;
        Label = localizer[labelResourceKey];
        this.stateText = "--";
    }

    public string LabelResourceKey { get; }

    public string Label { get; }

    /// <summary>这盏灯读哪几个变量（诊断、测试用）。</summary>
    public IReadOnlyList<string> TagKeys => this.sources.Select(source => source.TagKey).ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(IsUnknown), nameof(IsMoving), nameof(IsFault))]
    private IndicatorState state;

    [ObservableProperty]
    private string stateText;

    public bool IsOn => State == IndicatorState.On;

    public bool IsUnknown => State == IndicatorState.Unknown;

    public bool IsMoving => State == IndicatorState.Moving;

    public bool IsFault => State == IndicatorState.Fault;

    public void Update(MachineStateSnapshot snapshot)
    {
        State = StatusIndicator.Combine(this.sources.Select(source => source.Read(snapshot)));
        StateText = State switch
        {
            IndicatorState.On => this.localizer[this.onResourceKey],
            IndicatorState.Off => this.localizer[this.offResourceKey],
            IndicatorState.Moving => this.localizer["Lamp_Moving"],
            IndicatorState.Fault => this.localizer["Lamp_Fault"],
            _ => "--",
        };
    }
}

/// <summary>状态带上的一格文字：方式、通道、程序、X、Z、转速。</summary>
public sealed partial class StatusFieldViewModel : ObservableObject
{
    public StatusFieldViewModel(string labelResourceKey, IStringLocalizer localizer, bool isMonospaced = false, bool isPrimary = false)
    {
        LabelResourceKey = labelResourceKey;
        Label = localizer[labelResourceKey];
        IsMonospaced = isMonospaced;
        IsPrimary = isPrimary;
    }

    public string LabelResourceKey { get; }

    public string Label { get; }

    public bool IsMonospaced { get; }

    /// <summary>主读数（状态带的 X、Z）：L2 32 px。</summary>
    public bool IsPrimary { get; }

    [ObservableProperty]
    private string valueText = "--";
}

/// <summary>位置块的一行：轴名、实际位置（L1 大字）、剩余行程。</summary>
public sealed partial class TopAxisViewModel : ObservableObject
{
    public TopAxisViewModel(string name, string roleText)
    {
        Name = name;
        RoleText = roleText;
    }

    public string Name { get; }

    public string RoleText { get; }

    [ObservableProperty]
    private string positionText = "--";

    [ObservableProperty]
    private string remainingText = "--";
}

/// <summary>主轴与进给块的一行：S2 砂轮 / S1 头架 / F 拖板 / X 周期进给，值 + 换算量 + 倍率。</summary>
public sealed partial class DriveReadoutViewModel : ObservableObject
{
    public DriveReadoutViewModel(string symbol, string labelResourceKey, IStringLocalizer localizer)
    {
        Symbol = symbol;
        Label = localizer[labelResourceKey];
    }

    public string Symbol { get; }

    public string Label { get; }

    [ObservableProperty]
    private string valueText = "--";

    [ObservableProperty]
    private string secondaryText = string.Empty;

    [ObservableProperty]
    private string overrideText = string.Empty;

    /// <summary>倍率不是 100 %：琥珀色提醒（不是报警，红色只留给报警）。</summary>
    [ObservableProperty]
    private bool isOverrideOff;

    /// <summary>写倍率；读不到时留空，不提醒。</summary>
    public void SetOverride(double? percent)
    {
        OverrideText = percent is null ? string.Empty : percent.Value.ToString("F0", CultureInfo.CurrentCulture) + " %";
        IsOverrideOff = percent is double value && Math.Abs(value - 100.0) > 0.5;
    }
}

/// <summary>辅助功能块的一组：组名 + 一行灯。</summary>
public sealed record StatusGroupViewModel(string Label, IReadOnlyList<StatusLampViewModel> Lamps);

/// <summary>
/// 自动页顶上常驻的状态带（修改稿 3③、5.5）：方式、通道、回参考点、程序名，
/// 以及切削液、中心架、尾架、测量臂四盏灯。机构状态位没映射（Q7 地址未到）时灯画虚框。
/// </summary>
public sealed class StatusBandViewModel
{
    private readonly IStringLocalizer localizer;
    private readonly MachineDescription machine;
    private readonly StatusFieldViewModel mode;
    private readonly StatusFieldViewModel channel;
    private readonly StatusFieldViewModel referenced;
    private readonly StatusFieldViewModel program;

    public StatusBandViewModel(MachineDescription machine, IStringLocalizer localizer)
    {
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));

        this.mode = new StatusFieldViewModel("Band_Mode", localizer);
        this.channel = new StatusFieldViewModel("Band_Channel", localizer);
        this.referenced = new StatusFieldViewModel("Band_Reference", localizer);
        this.program = new StatusFieldViewModel("Band_Program", localizer, isMonospaced: true);

        // 方式 · 通道 · 参考点 · 程序（方案 F：自动页不画通道行，这三样挪到位置块顶上；轴位置与转速下面各有大字，不重复）。
        Fields = new ObservableCollection<StatusFieldViewModel>
        {
            this.mode, this.channel, this.referenced, this.program,
        };

        Lamps = new ObservableCollection<StatusLampViewModel>
        {
            new(MachineStatusCatalog.Coolant, localizer),
            new(MachineStatusCatalog.SteadyRest, localizer),
            new(MachineStatusCatalog.Tailstock, localizer),
            new(
                "Status_measuringArm",
                "Status_measuringArm_On",
                "Status_measuringArm_Off",
                new[] { MachineStatusCatalog.OuterArm, MachineStatusCatalog.InnerArm },
                localizer),
        };

        Overview = new ObservableCollection<StatusLampViewModel>(
            MachineStatusCatalog.All.Select(indicator => new StatusLampViewModel(indicator, localizer)));

        // 上排三块（界面修订稿 v3 1.1，自动页与 JOG 页同一套）：位置 · 主轴与进给 · 辅助功能。
        Axes = new ObservableCollection<TopAxisViewModel>(machine.Axes
            .Where(axis => axis.IsPresent && IsPositionAxis(axis.Role))
            .Select(axis => new TopAxisViewModel(axis.Name, localizer["AxisRole_" + axis.Role])));
        this.wheelDrive = new DriveReadoutViewModel("S2", "Drive_Wheel", localizer);
        this.headstockDrive = new DriveReadoutViewModel("S1", "Drive_Headstock", localizer);
        this.carriageDrive = new DriveReadoutViewModel("F", "Drive_Carriage", localizer);
        this.infeedDrive = new DriveReadoutViewModel("X", "Drive_Infeed", localizer);
        Drives = new ObservableCollection<DriveReadoutViewModel> { this.wheelDrive, this.headstockDrive, this.carriageDrive, this.infeedDrive };
        Groups = new ObservableCollection<StatusGroupViewModel>(MachineStatusCatalog.Groups.Select(group => new StatusGroupViewModel(
            localizer["StatusGroup_" + group.Key],
            group.Indicators.Select(indicator => new StatusLampViewModel(indicator, localizer)).ToArray())));
    }

    private readonly DriveReadoutViewModel wheelDrive;
    private readonly DriveReadoutViewModel headstockDrive;
    private readonly DriveReadoutViewModel carriageDrive;
    private readonly DriveReadoutViewModel infeedDrive;

    /// <summary>位置块：X · X1 · Z · U……（在本机装着的直线 / 回转定位轴）。</summary>
    public ObservableCollection<TopAxisViewModel> Axes { get; }

    /// <summary>主轴与进给块。</summary>
    public ObservableCollection<DriveReadoutViewModel> Drives { get; }

    /// <summary>辅助功能块：冷却 · 装夹 · 支承 · 测量 · 机床。</summary>
    public ObservableCollection<StatusGroupViewModel> Groups { get; }

    private static bool IsPositionAxis(string role) =>
        role is not (MachineAxisRoles.WorkpieceSpindle or MachineAxisRoles.WheelSpindle);

    public ObservableCollection<StatusFieldViewModel> Fields { get; }

    public ObservableCollection<StatusLampViewModel> Lamps { get; }

    /// <summary>状态总览（最终稿 F3）：全部机构到位灯。</summary>
    public ObservableCollection<StatusLampViewModel> Overview { get; }

    public void Update(MachineStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        this.mode.ValueText = snapshot.GetNumberOrNull(MachineTagKeys.OperatingMode) switch
        {
            0.0 => this.localizer["Mode_Jog"],
            1.0 => this.localizer["Mode_Mda"],
            2.0 => this.localizer["Mode_Auto"],
            _ => "--",
        };

        double? channelState = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState);
        this.channel.ValueText = channelState is null
            ? "--"
            : this.localizer["ChannelState_" + ((int)channelState.Value is var code && Enum.IsDefined(typeof(NcChannelState), code)
                ? ((NcChannelState)code).ToString()
                : "Unknown")];

        this.referenced.ValueText = snapshot.GetNumberOrNull(MachineTagKeys.Referenced) switch
        {
            null => "--",
            0 => this.localizer["Band_ReferenceMissing"],
            _ => this.localizer["Band_ReferenceDone"],
        };

        string? programName = snapshot.GetTextOrNull(MachineTagKeys.ProgramName);
        this.program.ValueText = string.IsNullOrEmpty(programName) ? "--" : programName;

        foreach (StatusLampViewModel lamp in Lamps.Concat(Overview).Concat(Groups.SelectMany(group => group.Lamps)))
        {
            lamp.Update(snapshot);
        }

        foreach (TopAxisViewModel axis in Axes)
        {
            axis.PositionText = Format(snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(axis.Name)), "+0.000;-0.000;0.000");
            axis.RemainingText = Format(snapshot.GetNumberOrNull(MachineTagKeys.AxisDistanceToGoMm(axis.Name)), "+0.000;-0.000;0.000");
        }

        // S2 砂轮：r/min + 线速 m/s（按砂轮直径换算）。
        double? wheelRpm = snapshot.GetNumberOrNull(MachineTagKeys.WheelSpeedRpm);
        double? wheelDiameter = snapshot.GetNumberOrNull(MachineTagKeys.WheelDiameterMm);
        this.wheelDrive.ValueText = Format(wheelRpm, "F0") + " r/min";
        this.wheelDrive.SecondaryText = wheelRpm is double n2 && wheelDiameter is double d2
            ? (Math.PI * d2 * n2 / 60000.0).ToString("F1", CultureInfo.CurrentCulture) + " m/s"
            : string.Empty;
        this.wheelDrive.SetOverride(snapshot.GetNumberOrNull(MachineTagKeys.WheelOverridePercent));

        // S1 头架：r/min + 工件线速 m/min（按实测直径，没有就按作业半径）。
        double? headRpm = Axis(snapshot, MachineAxisRoles.WorkpieceSpindle, speed: true);
        double? rollDiameter = snapshot.GetNumberOrNull(MachineTagKeys.MeasuredDiameterMm)
            ?? snapshot.GetNumberOrNull(MachineTagKeys.JobRollRadiusMm) * 2.0;
        this.headstockDrive.ValueText = Format(headRpm, "F1") + " r/min";
        this.headstockDrive.SecondaryText = headRpm is double n1 && rollDiameter is double d1 && d1 > 0.0
            ? (Math.PI * d1 * n1 / 1000.0).ToString("F0", CultureInfo.CurrentCulture) + " m/min"
            : string.Empty;
        this.headstockDrive.SetOverride(snapshot.GetNumberOrNull(MachineTagKeys.SpindleOverridePercent));

        // F 拖板：实际进给；没映射时显示本工序设定的进给。
        AxisDescription? carriage = this.machine.Axes.FirstOrDefault(axis => axis.IsPresent && axis.Role == MachineAxisRoles.Carriage);
        double? feed = (carriage is null ? null : snapshot.GetNumberOrNull(MachineTagKeys.AxisActualFeedMmPerMin(carriage.Name)))
            ?? snapshot.GetNumberOrNull(MachineTagKeys.JobStepFeedMmPerMin);
        this.carriageDrive.ValueText = Format(feed, "F0") + " mm/min";
        this.carriageDrive.SetOverride(snapshot.GetNumberOrNull(MachineTagKeys.FeedOverridePercent));

        // X 周期进给：本工序每道进给（半径 µm / 次）。
        double? infeed = snapshot.GetNumberOrNull(MachineTagKeys.JobStepInfeedPerPassRadiusMm);
        this.infeedDrive.ValueText = (infeed is double mm ? (mm * 1000.0).ToString("F1", CultureInfo.CurrentCulture) : "--") + " µm/" + this.localizer["Drive_PerPass"];
    }

    private double? Axis(MachineStateSnapshot snapshot, string role, bool speed)
    {
        AxisDescription? axis = this.machine.Axes.FirstOrDefault(candidate =>
            candidate.IsPresent && string.Equals(candidate.Role, role, StringComparison.Ordinal));
        if (axis is null)
        {
            return null;
        }

        return snapshot.GetNumberOrNull(speed
            ? MachineTagKeys.AxisActualSpeedRpm(axis.Name)
            : MachineTagKeys.AxisActualPositionMm(axis.Name));
    }

    private static string Format(double? value, string format) =>
        value is null ? "--" : value.Value.ToString(format, CultureInfo.CurrentCulture);
}
