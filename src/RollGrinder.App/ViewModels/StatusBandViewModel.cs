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
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(IsUnknown))]
    private IndicatorState state;

    [ObservableProperty]
    private string stateText;

    public bool IsOn => State == IndicatorState.On;

    public bool IsUnknown => State == IndicatorState.Unknown;

    public void Update(MachineStateSnapshot snapshot)
    {
        State = StatusIndicator.Combine(this.sources.Select(source => source.Read(snapshot)));
        StateText = State switch
        {
            IndicatorState.On => this.localizer[this.onResourceKey],
            IndicatorState.Off => this.localizer[this.offResourceKey],
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

/// <summary>
/// 自动页顶上常驻的状态带（修改稿 3③、5.5）：方式、通道、程序名、X、Z、砂轮与头架转速，
/// 以及切削液、中心架、尾架、测量臂四盏灯。机构状态位没映射（Q7 地址未到）时灯画虚框。
/// </summary>
public sealed class StatusBandViewModel
{
    private readonly IStringLocalizer localizer;
    private readonly MachineDescription machine;
    private readonly StatusFieldViewModel mode;
    private readonly StatusFieldViewModel channel;
    private readonly StatusFieldViewModel program;
    private readonly StatusFieldViewModel x;
    private readonly StatusFieldViewModel z;
    private readonly StatusFieldViewModel wheel;
    private readonly StatusFieldViewModel headstock;

    public StatusBandViewModel(MachineDescription machine, IStringLocalizer localizer)
    {
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));

        this.mode = new StatusFieldViewModel("Band_Mode", localizer);
        this.channel = new StatusFieldViewModel("Band_Channel", localizer);
        this.program = new StatusFieldViewModel("Band_Program", localizer, isMonospaced: true);
        this.x = new StatusFieldViewModel("Band_X", localizer, isMonospaced: true, isPrimary: true);
        this.z = new StatusFieldViewModel("Band_Z", localizer, isMonospaced: true, isPrimary: true);
        this.wheel = new StatusFieldViewModel("Band_WheelRpm", localizer, isMonospaced: true);
        this.headstock = new StatusFieldViewModel("Band_HeadstockRpm", localizer, isMonospaced: true);
        Fields = new ObservableCollection<StatusFieldViewModel>
        {
            this.mode, this.channel, this.program, this.x, this.z, this.wheel, this.headstock,
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
    }

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

        string? programName = snapshot.GetTextOrNull(MachineTagKeys.ProgramName);
        this.program.ValueText = string.IsNullOrEmpty(programName) ? "--" : programName;

        this.x.ValueText = Format(Axis(snapshot, MachineAxisRoles.InfeedRadius, speed: false), "+0.000;-0.000;0.000");
        this.z.ValueText = Format(Axis(snapshot, MachineAxisRoles.Carriage, speed: false), "F2");
        this.wheel.ValueText = Format(snapshot.GetNumberOrNull(MachineTagKeys.WheelSpeedRpm), "F0");
        this.headstock.ValueText = Format(Axis(snapshot, MachineAxisRoles.WorkpieceSpindle, speed: true), "F1");

        foreach (StatusLampViewModel lamp in Lamps.Concat(Overview))
        {
            lamp.Update(snapshot);
        }
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
