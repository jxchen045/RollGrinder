using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Centring;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Units;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Manual;
using RollGrinder.Services.Measurement;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.App.ViewModels;

/// <summary>手动动作页的一页：一组机构动作（竖键）、相关的位置（L1）和这组机构的到位状态灯。</summary>
public sealed partial class ManualGroupViewModel : ObservableObject
{
    public ManualGroupViewModel(
        string key,
        string title,
        IReadOnlyList<AxisReadoutViewModel> axes,
        IReadOnlyList<StatusLampViewModel> lamps,
        IReadOnlyList<FunctionKeyViewModel?> verticalKeys)
    {
        Key = key;
        Title = title;
        Axes = axes;
        Lamps = lamps;
        VerticalKeys = verticalKeys;
    }

    public string Key { get; }

    public string Title { get; }

    /// <summary>本页相关的位置（L1）。</summary>
    public IReadOnlyList<AxisReadoutViewModel> Axes { get; }

    /// <summary>本页机构的到位状态：灯亮 = 到位，虚框 = 读不到。</summary>
    public IReadOnlyList<StatusLampViewModel> Lamps { get; }

    /// <summary>这一页在右侧竖键上的键，一个动作一个键（null = 空键）。</summary>
    public IReadOnlyList<FunctionKeyViewModel?> VerticalKeys { get; }

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>
/// 对中比对表里的一行：一个量，两端各一个读数，外加两端之差。
/// </summary>
/// <param name="QuantityText">量的名字（A 测头、B 测头、(A−B)/2、直径、拖板、进给）。</param>
/// <param name="HeadText">头架侧的读数。</param>
/// <param name="TailText">尾架侧的读数。</param>
/// <param name="DifferenceText">两端之差；位置一类的量没有"差"可言，留空。</param>
/// <param name="IsOutOfTolerance">这一行是不是超差（只有安装偏差那一行会是 true）。</param>
public sealed record CentringRow(
    string QuantityText,
    string HeadText,
    string TailText,
    string DifferenceText,
    bool IsOutOfTolerance = false);

/// <summary>
/// 手动动作页（界面最终稿 5.4、5.5）：机床区 JOG 方式下横键"测量臂""尾架""头架拨盘""托瓦""测量对中""辅助循环 ▸"。
/// 版式：上面是本页相关的位置（L1）和测量，下面是本页机构的状态格，竖键一个动作一个键。
/// 动作只在竖键上（不再在卡片里放一批同样的按钮——双入口，C5）。
/// </summary>
public sealed partial class ManualViewModel : PageViewModelBase
{
    private readonly IMachineMonitor monitor;
    private readonly IMeasurementService measurementService;
    private readonly MachineDescription machine;
    private readonly ICentringService centring;
    private readonly ManualActionKeys actionKeys;

    public ManualViewModel(
        IMachineMonitor monitor,
        IMeasurementService measurementService,
        IManualCommandService commands,
        MachineDescription machine,
        ICentringService centring,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.centring = centring ?? throw new ArgumentNullException(nameof(centring));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.measurementService = measurementService ?? throw new ArgumentNullException(nameof(measurementService));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.actionKeys = new ManualActionKeys(
            commands ?? throw new ArgumentNullException(nameof(commands)),
            interaction,
            localizer,
            alarms,
            CapturePointAsync);

        BuildGroups();
        SetFunctionKeys(MachineAreaKeys.Create(Navigator, localizer, MachineAreaKeys.MeasuringArm));
        Select(Groups[0]);
    }

    public override PageKey Key => PageKey.Manual;

    public override string TitleResourceKey => "Page_Manual";

    /// <summary>手动磨削页有帮助条目；动作页用同一条（位置、测量、状态灯的读法一样）。</summary>
    public override string? HelpTopicKey => "Help_ManualGrinding";

    /// <summary>4 个动作页、测量对中、辅助循环。</summary>
    public ObservableCollection<ManualGroupViewModel> Groups { get; } = new();

    /// <summary>当前那一页。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCentring))]
    private ManualGroupViewModel? selectedGroup;

    /// <summary>当前是测量对中（版式不同：测点表 + 对中比对）。</summary>
    public bool IsCentring => SelectedGroup?.Key == ManualPageLayout.MeasureAndCentringKey;

    private void BuildGroups()
    {
        foreach (ManualPage page in ManualPageLayout.Pages)
        {
            IReadOnlyList<FunctionKeyViewModel?> keys = page.Key == ManualPageLayout.MeasureAndCentringKey
                ? MeasureAndCentringKeys()
                : page.ActionKeys.Select(this.actionKeys.Create).ToArray();
            Groups.Add(new ManualGroupViewModel(
                page.Key,
                Localizer["ManualPage_" + page.Key],
                AxisReadoutViewModel.For(this.machine, Localizer, AxisRoles(page.Key)),
                page.Indicators.Select(indicator => new StatusLampViewModel(indicator, Localizer)).ToArray(),
                keys));
        }

        // 辅助循环 ▸：跑一段 NC 程序的循环，全要确认（最终稿 4.5）。回参考点在按钮板上。
        Groups.Add(new ManualGroupViewModel(
            MachineAreaKeys.Cycles,
            Localizer["Fn_AuxCycles"],
            AxisReadoutViewModel.For(this.machine, Localizer, AxisRoles(MachineAreaKeys.Cycles)),
            MachineStatusCatalog.All.Select(indicator => new StatusLampViewModel(indicator, Localizer)).ToArray(),
            ManualPageLayout.AuxiliaryKeys.Select(this.actionKeys.Create).ToArray()));
    }

    /// <summary>每页上面显示哪几根轴的位置。</summary>
    private static string[] AxisRoles(string group) => group switch
    {
        "measuringArm" => new[] { MachineAxisRoles.MeasuringCarriage, MachineAxisRoles.Carriage },
        "tailstock" or "driver" => new[] { MachineAxisRoles.Carriage },
        "steadyRest" => new[] { MachineAxisRoles.RollProfile, MachineAxisRoles.Carriage },
        ManualPageLayout.MeasureAndCentringKey => new[] { MachineAxisRoles.Carriage, MachineAxisRoles.InfeedRadius },
        _ => new[] { MachineAxisRoles.InfeedRadius, MachineAxisRoles.MeasuringCarriage, MachineAxisRoles.Carriage, MachineAxisRoles.RollProfile },
    };

    /// <summary>
    /// 测量对中的竖键（最终稿 5.5）：采集测点、清空测点…、归档测量、空、记录头架侧、记录尾架侧、清除对中…。
    /// 动作是上位机自己的，不写机床。
    /// </summary>
    private IReadOnlyList<FunctionKeyViewModel?> MeasureAndCentringKeys() => new FunctionKeyViewModel?[]
    {
        new FunctionKeyViewModel("Measurement_CaptureButton", CapturePointCommand, Localizer),
        new FunctionKeyViewModel("Measurement_ClearButton", new RelayCommand(() => Ask("Measurement_AskClear", ClearPoints)), Localizer),
        new FunctionKeyViewModel("Measurement_SaveButton", SaveMeasurementCommand, Localizer),
        null,
        new FunctionKeyViewModel("Centring_CaptureHead", CaptureHeadCommand, Localizer),
        new FunctionKeyViewModel("Centring_CaptureTail", CaptureTailCommand, Localizer),
        new FunctionKeyViewModel("Centring_ClearButton", new RelayCommand(() => Ask("Centring_AskClear", ClearCentring)), Localizer),
    };

    /// <summary>横键选页（测量臂、尾架……）：外壳按组键调进来。</summary>
    public override bool ShowGroup(string groupKey)
    {
        ManualGroupViewModel? group = Groups.FirstOrDefault(g => string.Equals(g.Key, groupKey, StringComparison.Ordinal));
        if (group is null)
        {
            return false;
        }

        Select(group);
        return true;
    }

    private void Select(ManualGroupViewModel group)
    {
        foreach (ManualGroupViewModel other in Groups)
        {
            other.IsSelected = ReferenceEquals(other, group);
        }

        SelectedGroup = group;
        MachineAreaKeys.MarkActive(FunctionKeys, group.Key);
        SetVerticalKeys(group.VerticalKeys);
    }

    [ObservableProperty]
    private string probeAText = "--";

    [ObservableProperty]
    private string probeBText = "--";

    [ObservableProperty]
    private string measuredDiameterText = "--";

    [ObservableProperty]
    private string mountingDeviationText = "--";

    /// <summary>对中比对里两端之差（L1 显示，最终稿 5.5）。</summary>
    [ObservableProperty]
    private string centringDifferenceText = "--";

    [ObservableProperty]
    private string alignmentHintText = string.Empty;

    [ObservableProperty]
    private bool hasAlignmentProblem;

    [ObservableProperty]
    private string jobId = string.Empty;

    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : Localizer[StatusResourceKey];

    partial void OnStatusResourceKeyChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText));
        if (value.Length > 0)
        {
            Interaction.Say(Localizer[value]);
        }
    }

    public override void OnTick(DateTimeOffset nowUtc)
    {
        MachineStateSnapshot snapshot = this.monitor.Current;

        double? probeA = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeAMm);
        double? probeB = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeBMm);
        ProbeAText = Format(probeA, "F4", showSign: true);
        ProbeBText = Format(probeB, "F4", showSign: true);
        MeasuredDiameterText = Format(snapshot.GetNumberOrNull(MachineTagKeys.MeasuredDiameterMm), "F3");
        MountingDeviationText = probeA is null || probeB is null
            ? "--"
            : Format((probeA.Value - probeB.Value) / 2.0, "F4", showSign: true);

        this.actionKeys.Refresh(Block);

        if (SelectedGroup is { } group)
        {
            foreach (AxisReadoutViewModel axis in group.Axes)
            {
                axis.Update(snapshot);
            }

            foreach (StatusLampViewModel lamp in group.Lamps)
            {
                lamp.Update(snapshot);
            }
        }
    }

    /// <summary>手动采下的测点。</summary>
    public ObservableCollection<MeasurementRowViewModel> Points { get; } = new();

    /// <summary>
    /// 对中比对表：头架侧与尾架侧各记一组，逐项比。
    ///
    /// 量的是**装夹**不是辊形——所以比的是两端之差，不是某一端的绝对值。
    /// </summary>
    public ObservableCollection<CentringRow> Centring { get; } = new();

    [RelayCommand]
    private Task CapturePointAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            MeasurementPoint point = await this.measurementService.CapturePointAsync(token).ConfigureAwait(true);
            Points.Add(new MeasurementRowViewModel(point));
            StatusResourceKey = "Measurement_PointCaptured";
            PointsChanged?.Invoke(this, EventArgs.Empty);
        }, cancellationToken);

    private void ClearPoints()
    {
        Points.Clear();
        StatusResourceKey = string.Empty;
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>测点变了：视图重画"沿 Z 的直径"小曲线。</summary>
    public event EventHandler? PointsChanged;

    [RelayCommand]
    private Task SaveMeasurementAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(JobId) || Points.Count < 2)
            {
                StatusResourceKey = "Measurement_NotEnoughPoints";
                return;
            }

            await this.measurementService.SaveAsync(
                JobId, Points.Select(row => row.Point).ToArray(), "manual", token).ConfigureAwait(true);
            StatusResourceKey = "Measurement_Saved";
        }, cancellationToken);

    /// <summary>记下头架侧那一组读数。</summary>
    [RelayCommand]
    private Task CaptureHeadAsync(CancellationToken cancellationToken) =>
        CaptureCentringAsync(RollEnd.Head, cancellationToken);

    /// <summary>记下尾架侧那一组读数。</summary>
    [RelayCommand]
    private Task CaptureTailAsync(CancellationToken cancellationToken) =>
        CaptureCentringAsync(RollEnd.Tail, cancellationToken);

    /// <summary>清掉两端的记录，重新找正。</summary>
    private void ClearCentring()
    {
        this.centring.Clear();
        RefreshCentring();
        StatusResourceKey = string.Empty;
    }

    private Task CaptureCentringAsync(RollEnd end, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            await this.centring.CaptureAsync(end, token).ConfigureAwait(true);
            StatusResourceKey = end == RollEnd.Head ? "Centring_HeadCaptured" : "Centring_TailCaptured";
            RefreshCentring();
        }, cancellationToken);

    /// <summary>
    /// 摊开对中比对表。
    ///
    /// 只记了一端时也照样列出来——让人看见"这一端记过了、另一端还没有"，
    /// 比一张空表清楚。两端都有了才算得出差，也才谈得上超差。
    /// </summary>
    private void RefreshCentring()
    {
        Centring.Clear();
        AlignmentHintText = string.Empty;
        HasAlignmentProblem = false;
        CentringDifferenceText = "--";

        CentringReading? head = this.centring.Reading(RollEnd.Head);
        CentringReading? tail = this.centring.Reading(RollEnd.Tail);
        if (head is null && tail is null)
        {
            return;
        }

        CentringComparison? comparison = this.centring.Compare();
        if (comparison is not null)
        {
            CentringDifferenceText = Localizer.Format("Centring_MicrometerFormat", comparison.DeviationDifferenceMicrometer);
        }

        Centring.Add(Row("Live_ProbeA", head?.ProbeARadiusMm, tail?.ProbeARadiusMm, "F4"));
        Centring.Add(Row("Live_ProbeB", head?.ProbeBRadiusMm, tail?.ProbeBRadiusMm, "F4"));
        Centring.Add(new CentringRow(
            Localizer["Manual_MountingDeviation"],
            Format(head?.MountingDeviationRadiusMm, "F4", showSign: true),
            Format(tail?.MountingDeviationRadiusMm, "F4", showSign: true),
            comparison is null
                ? string.Empty
                : Localizer.Format("Centring_MicrometerFormat", comparison.DeviationDifferenceMicrometer),
            comparison is not null && !comparison.IsWithinTolerance));

        // 直径之差是辊本身的锥度，调中心架调不掉它——所以它只报数，不判超差。
        Centring.Add(new CentringRow(
            Localizer["Manual_RollDiameter"],
            Format(head?.DiameterMm, "F3"),
            Format(tail?.DiameterMm, "F3"),
            comparison is null
                ? string.Empty
                : Localizer.Format("Centring_MicrometerFormat", comparison.DiameterDifferenceMicrometer)));

        // 轴名来自 machine.json：列头写的就是现场面板上那个字母。
        Centring.Add(Row(this.centring.CarriageAxisName, head?.CarriagePositionMm, tail?.CarriagePositionMm, "F2"));
        Centring.Add(Row(this.centring.InfeedAxisName, head?.InfeedPositionMm, tail?.InfeedPositionMm, "F4"));

        if (comparison is null || comparison.IsWithinTolerance)
        {
            return;
        }

        HasAlignmentProblem = true;
        AlignmentHintText = Localizer.Format(
            comparison.Adjustment == CentringAdjustment.HeadInward
                ? "Manual_AlignHeadInward"
                : "Manual_AlignTailInward",
            Math.Abs(UnitConversion.MicrometerToMm(comparison.DeviationDifferenceMicrometer)));
    }

    private CentringRow Row(string quantity, double? headValue, double? tailValue, string format)
    {
        string localized = Localizer[quantity];

        // 轴名（Z、X）不是资源键，取不到就照原样写——那本来就是面板上的字母。
        return new CentringRow(
            localized.StartsWith('!') ? quantity : localized,
            Format(headValue, format),
            Format(tailValue, format),
            string.Empty);
    }


    private static string Format(double? value, string format, bool showSign = false)
    {
        if (value is null)
        {
            return "--";
        }

        string text = value.Value.ToString(format, CultureInfo.CurrentCulture);
        return showSign && value.Value >= 0.0 ? "+" + text : text;
    }
}
