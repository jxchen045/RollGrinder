using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Controls;
using RollGrinder.Core.Steps;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Calibration;
using RollGrinder.Core.Parameters;
using RollGrinder.Data;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Calibration;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 设置页：现场标定值。
///
/// 这一页改的是**换一次砂轮就变**的那些值（砂轮直径、探头距离、对刀偏移、各项验收公差），
/// 不是机床固有能力——轴、行程、选装装置仍然由 machine.json 描述，装机时定下就不动。
///
/// 参数格完全由 <see cref="MachineCalibration.Schema"/> 生成：
/// 新增一项标定值只写一行 schema + 一条 resx 文案，这一页与持久化都不用改（架构约束 ④）。
/// </summary>
public sealed partial class SettingsViewModel : PageViewModelBase
{
    private readonly ICalibrationService calibration;
    private readonly IWheelChangeService wheelChange;
    private readonly IUserSession userSession;
    private readonly IWheelHistory wheelHistory;
    private readonly MachineCapability capability;

    /// <summary>进入本页时的取值，"放弃修改"回到这里。</summary>
    private IReadOnlyList<string> committed = Array.Empty<string>();

    public SettingsViewModel(
        ICalibrationService calibration,
        IWheelChangeService wheelChange,
        IUserSession userSession,
        IWheelHistory wheelHistory,
        MachineCapability capability,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.wheelHistory = wheelHistory ?? throw new ArgumentNullException(nameof(wheelHistory));
        this.capability = capability ?? throw new ArgumentNullException(nameof(capability));
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.wheelChange = wheelChange ?? throw new ArgumentNullException(nameof(wheelChange));
        this.userSession = userSession ?? throw new ArgumentNullException(nameof(userSession));

        SetFunctionKeys(new[]
        {
            new FunctionKeyViewModel("Fn_SaveSettings", new AsyncRelayCommand(
                () => SaveAsync(CancellationToken.None)), localizer, FunctionKeyKind.Primary, requiresEditable: true),
            new FunctionKeyViewModel("Fn_ReloadSettings", ReloadCommand, localizer),
            new FunctionKeyViewModel("Fn_NewWheel", StartWheelChangeCommand, localizer, requiresEditable: true),
            new FunctionKeyViewModel("Fn_Wheel", OpenWheelCommand, localizer),
        });

        MaxSurfaceSpeedText = capability.MaxWheelSurfaceSpeedMPerSec is double maxSpeed
            ? localizer.Format("Wheel_MaxSurfaceSpeedFormat", maxSpeed)
            : "--";

        Rebuild();
        this.calibration.Changed += (_, _) => OnUiThread(Rebuild);
        this.wheelChange.Changed += (_, _) => OnUiThread(RefreshWheelChange);

        // 程序里的修整由后台服务记账，事件在后台线程上来：只记一笔"旧了"，由界面节拍去刷。
        this.wheelHistory.Changed += (_, _) => this.wheelHistoryStale = true;
    }

    // ── 砂轮页（修改稿 5.7）：砂轮数据、修整参数、修整与更换记录 ──────────────────

    /// <summary>砂轮子视图的资源键，同时用作面包屑文案。</summary>
    public const string WheelSubView = "SubView_Wheel";

    private static readonly string[] WheelKeys =
    {
        CalibrationKeys.WheelDiameterMm, CalibrationKeys.NewWheelDiameterMm, CalibrationKeys.WheelWidthMm,
    };

    private static readonly string[] DressKeys =
    {
        CalibrationKeys.DressInfeedRadiusMicrometer, CalibrationKeys.DressPassCount,
        CalibrationKeys.DressFeedMmPerMin, CalibrationKeys.DressIntervalRolls,
    };

    /// <summary>砂轮数据的参数格（和标定值同一批格子：在这里改、按"保存"一起存）。</summary>
    public ObservableCollection<ParameterRowViewModel> WheelRows { get; } = new();

    /// <summary>修整参数的参数格。程序里插"砂轮修整"时照这里的值填。</summary>
    public ObservableCollection<ParameterRowViewModel> DressRows { get; } = new();

    /// <summary>修整与更换记录，新的在前。</summary>
    public ObservableCollection<WheelEventRowViewModel> WheelHistory { get; } = new();

    /// <summary>机床允许的砂轮最高线速度（machine.json）；没配为"--"。</summary>
    public string MaxSurfaceSpeedText { get; }

    /// <summary>光标所在的砂轮 / 修整参数：简图上亮它，说明行写它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WheelHelpText))]
    private string focusedWheelKey = CalibrationKeys.WheelDiameterMm;

    /// <summary>简图下面那一行：参数名 — 说明　单位　范围。</summary>
    public string WheelHelpText
    {
        get
        {
            ParameterRowViewModel? row = Row(FocusedWheelKey);
            if (row is null)
            {
                return string.Empty;
            }

            var parts = new List<string> { Localizer.Format("Steps_HelpHeadFormat", row.Label, Localizer["ParamHelp_" + row.Key]) };
            if (row.UnitText.Length > 0)
            {
                parts.Add(Localizer.Format("Steps_HelpUnitFormat", row.UnitText));
            }

            if (row.RangeText.Length > 0)
            {
                parts.Add(Localizer.Format("Steps_HelpRangeFormat", row.RangeText));
            }

            return string.Join(Localizer["Steps_HelpSeparator"], parts);
        }
    }

    [RelayCommand]
    private void OpenWheel()
    {
        FocusedWheelKey = CalibrationKeys.WheelDiameterMm;
        Navigator.OpenSubView(WheelSubView);
        _ = RunGuardedAsync(RefreshWheelHistoryAsync, CancellationToken.None);
    }

    private async Task RefreshWheelHistoryAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Data.Model.WheelEvent> events =
            await this.wheelHistory.ListAsync(WheelHistoryLimit, cancellationToken).ConfigureAwait(true);
        WheelHistory.Clear();
        foreach (Data.Model.WheelEvent entry in events)
        {
            WheelHistory.Add(new WheelEventRowViewModel(entry, Localizer));
        }
    }

    private volatile bool wheelHistoryStale;

    public override void OnTick(DateTimeOffset nowUtc)
    {
        base.OnTick(nowUtc);
        if (this.wheelHistoryStale && !IsBusy)
        {
            this.wheelHistoryStale = false;
            _ = RunGuardedAsync(RefreshWheelHistoryAsync, CancellationToken.None);
        }
    }

    /// <summary>砂轮记录列多少条。</summary>
    private const int WheelHistoryLimit = 100;

    /// <summary>换砂轮向导每一步配的图：画哪一幅、亮哪个量（修改稿 5②）。</summary>
    public WheelDiagramMode WizardDiagramMode => WheelChangeStage is WheelChangeStage.EnterNewWheel or WheelChangeStage.Verify or WheelChangeStage.Done
        ? WheelDiagramMode.Wheel
        : WheelDiagramMode.Trial;

    /// <summary>这一步要量、要填的是哪个尺寸。</summary>
    public string WizardHighlightKey => WheelChangeStage switch
    {
        WheelChangeStage.EnterNewWheel => CalibrationKeys.NewWheelDiameterMm,
        WheelChangeStage.SwitchToManualTouch or WheelChangeStage.RestoreTouchMode => WheelDiagram.TouchKey,
        WheelChangeStage.TrialGrind => WheelDiagram.TrialRollKey,
        WheelChangeStage.Verify => CalibrationKeys.WheelDiameterMm,
        _ => string.Empty,
    };

    partial void OnWheelChangeStageChanged(WheelChangeStage value)
    {
        OnPropertyChanged(nameof(WizardDiagramMode));
        OnPropertyChanged(nameof(WizardHighlightKey));
    }

    public override PageKey Key => PageKey.Settings;

    public override string TitleResourceKey => "Page_Settings";

    public override string MenuHintResourceKey => "Menu_SettingsHint";

    /// <summary>自动循环挂着程序时落只读锁：标定值一改，正在跑的程序算出来的位置就变了。</summary>
    public override bool LocksDuringRun => true;

    /// <summary>离线可用：只和数据库与配置打交道，不碰机床。</summary>
    public override bool WorksOffline => true;

    /// <summary>标定值的参数格。</summary>
    public ObservableCollection<ParameterRowViewModel> Values { get; } = new();

    /// <summary>每一项最后是谁在什么时候改的。</summary>
    public ObservableCollection<LabelValueViewModel> Audit { get; } = new();

    /// <summary>砂轮已经磨掉多少（直径量 mm），顶部提示用。</summary>
    [ObservableProperty]
    private string wheelWearText = "--";

    /// <summary>页面状态提示的资源键。</summary>
    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    /// <summary>
    /// 管理员以上才改得动。操作工看得见但改不了——
    /// 藏起来只会让人以为软件少做，标出来才知道是权限不够。
    /// </summary>
    public bool CanEdit => this.userSession.HasAtLeast(UserRole.Administrator) && !IsReadOnly;

    public override bool CanSave => CanEdit && IsDirty;

    public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        if (!CanEdit)
        {
            StatusResourceKey = "Settings_NeedsAdministrator";
            return false;
        }

        var pairs = new List<KeyValuePair<string, ParameterValue>>(Values.Count);
        foreach (ParameterRowViewModel row in Values)
        {
            ParameterValue? value = row.ToParameterValue();
            if (value is null)
            {
                StatusResourceKey = "Settings_ValueInvalid";
                return false;
            }

            pairs.Add(new KeyValuePair<string, ParameterValue>(row.Key, value));
        }

        try
        {
            await this.calibration.SaveAsync(
                new ParameterSet(pairs),
                this.userSession.CurrentUser?.UserName ?? string.Empty,
                cancellationToken).ConfigureAwait(true);

            IsDirty = false;
            StatusResourceKey = "Settings_Saved";
            await RefreshAuditAsync(cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
            return false;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
            return false;
        }
    }

    /// <summary>重新从库里读一遍，丢掉没存的改动。</summary>
    [RelayCommand]
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.calibration.LoadAsync(cancellationToken).ConfigureAwait(true);
            StatusResourceKey = string.Empty;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    /// <summary>换砂轮向导子视图的资源键，同时用作面包屑文案。</summary>
    public const string WheelChangeSubView = "SubView_WheelChange";

    /// <summary>向导走到哪一步。</summary>
    [ObservableProperty]
    private WheelChangeStage wheelChangeStage = WheelChangeStage.EnterNewWheel;

    /// <summary>这一步该做什么，一句话写在向导上。</summary>
    [ObservableProperty]
    private string wheelChangeHint = string.Empty;

    /// <summary>新砂轮直径（mm）的输入格。</summary>
    [ObservableProperty]
    private string newWheelDiameterText = string.Empty;

    /// <summary>试磨那一刀，上位机以为会磨成多少（mm）。</summary>
    [ObservableProperty]
    private string trialExpectedDiameterText = string.Empty;

    /// <summary>试磨那一刀，实际量出来是多少（mm）。</summary>
    [ObservableProperty]
    private string trialMeasuredDiameterText = string.Empty;

    /// <summary>反推出来的砂轮直径误差与修正值，摆给人看再决定接不接受。</summary>
    [ObservableProperty]
    private string wheelChangeResultText = string.Empty;

    /// <summary>
    /// 开始换砂轮。
    ///
    /// 新砂轮的直径只是个标称值，与真实直径差几毫米很常见，而这个误差会原样
    /// 变成辊径误差。所以不是"填个数就完"：先切手动对刀、试磨一刀，
    /// 用磨出来的实际直径反推砂轮直径，修正之后再把对刀方式改回去。
    /// </summary>
    [RelayCommand]
    private void StartWheelChange()
    {
        this.wheelChange.Begin();
        NewWheelDiameterText = Row(CalibrationKeys.NewWheelDiameterMm)?.Text ?? string.Empty;
        TrialExpectedDiameterText = string.Empty;
        TrialMeasuredDiameterText = string.Empty;
        RefreshWheelChange();
        Navigator.OpenSubView(WheelChangeSubView);
    }

    /// <summary>向导的"下一步"。每一步各自做各自的事，做不成就停在原地说为什么。</summary>
    [RelayCommand]
    private Task WheelChangeNextAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            string changedBy = this.userSession.CurrentUser?.UserName ?? string.Empty;

            switch (this.wheelChange.Current?.Stage)
            {
                case WheelChangeStage.EnterNewWheel:
                    if (!TryParse(NewWheelDiameterText, out double diameterMm))
                    {
                        StatusResourceKey = "WheelChange_NeedDiameter";
                        return;
                    }

                    this.wheelChange.SetNewWheelDiameter(diameterMm);
                    break;

                case WheelChangeStage.SwitchToManualTouch:
                    await this.wheelChange.SwitchToManualTouchAsync(changedBy, token).ConfigureAwait(true);
                    break;

                case WheelChangeStage.TrialGrind:
                    if (!TryParse(TrialExpectedDiameterText, out double expectedMm)
                        || !TryParse(TrialMeasuredDiameterText, out double measuredMm))
                    {
                        StatusResourceKey = "WheelChange_NeedTrialDiameters";
                        return;
                    }

                    this.wheelChange.RecordTrial(expectedMm, measuredMm);
                    break;

                case WheelChangeStage.Verify:
                    this.wheelChange.AcceptCorrection();
                    break;

                case WheelChangeStage.RestoreTouchMode:
                    await this.wheelChange.FinishAsync(changedBy, token).ConfigureAwait(true);
                    StatusResourceKey = "WheelChange_Done";
                    Navigator.CloseSubView();
                    return;

                default:
                    return;
            }

            StatusResourceKey = string.Empty;
            RefreshWheelChange();
        }, cancellationToken);

    /// <summary>这一刀不作数：不改砂轮直径，但对刀方式照样要改回去。</summary>
    [RelayCommand]
    private void SkipWheelCorrection()
    {
        if (this.wheelChange.Current?.Stage != WheelChangeStage.Verify)
        {
            return;
        }

        this.wheelChange.SkipCorrection();
        RefreshWheelChange();
    }

    /// <summary>中途放弃：已经切成手动对刀的话，退出前切回去。</summary>
    [RelayCommand]
    private Task CancelWheelChangeAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            await this.wheelChange
                .CancelAsync(this.userSession.CurrentUser?.UserName ?? string.Empty, token)
                .ConfigureAwait(true);

            Navigator.CloseSubView();
        }, cancellationToken);

    private void RefreshWheelChange()
    {
        WheelChangeWizard? wizard = this.wheelChange.Current;
        if (wizard is null)
        {
            WheelChangeResultText = string.Empty;
            return;
        }

        WheelChangeStage = wizard.Stage;
        WheelChangeHint = Localizer["WheelChange_Hint_" + wizard.Stage];

        // 误差与修正值都摆出来：人得能看出"这个数像不像砂轮直径误差"，
        // 而不是被告知一个结论。对错刀、测错值、辊装歪了都会长成这样。
        WheelChangeResultText = wizard.WheelDiameterErrorMm is double error
            ? Localizer.Format(
                "WheelChange_ResultFormat", error, wizard.CorrectedWheelDiameterMm ?? 0.0)
            : string.Empty;
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public override void DiscardChanges()
    {
        for (int i = 0; i < Values.Count && i < this.committed.Count; i++)
        {
            Values[i].Text = this.committed[i];
        }

        base.DiscardChanges();
        RefreshWheelWear();
    }

    public override void OnActivated()
    {
        Capture();
        OnPropertyChanged(nameof(CanEdit));
        _ = RefreshAuditAsync(CancellationToken.None);
    }

    private ParameterRowViewModel? Row(string key) =>
        Values.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.Ordinal));

    private void Rebuild()
    {
        Values.Clear();
        ParameterSet values = this.calibration.Current.Values;

        foreach (ParameterDescriptor descriptor in MachineCalibration.Schema.Descriptors)
        {
            var row = new ParameterRowViewModel(descriptor, values.Get(descriptor.Key), Localizer);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ParameterRowViewModel.Text))
                {
                    return;
                }

                MarkDirty();
                RefreshWheelWear();
            };
            Values.Add(row);
        }

        WheelRows.Clear();
        foreach (string key in WheelKeys)
        {
            if (Row(key) is { } row)
            {
                WheelRows.Add(row);
            }
        }

        DressRows.Clear();
        foreach (string key in DressKeys)
        {
            if (Row(key) is { } row)
            {
                DressRows.Add(row);
            }
        }

        Capture();
        IsDirty = false;
        RefreshWheelWear();
    }

    private void Capture() => this.committed = Values.Select(row => row.Text).ToArray();

    private void RefreshWheelWear()
    {
        if (!TryRead(CalibrationKeys.WheelDiameterMm, out double current)
            || !TryRead(CalibrationKeys.NewWheelDiameterMm, out double fresh))
        {
            WheelWearText = "--";
            return;
        }

        WheelWearText = string.Create(
            CultureInfo.CurrentCulture, $"{Math.Max(0.0, fresh - current):F1} mm");
    }

    private bool TryRead(string key, out double value)
    {
        value = 0.0;
        ParameterRowViewModel? row = Row(key);
        return row is not null
            && (double.TryParse(row.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                || double.TryParse(row.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value));
    }

    private async Task RefreshAuditAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<CalibrationAudit> entries =
                await this.calibration.LoadAuditAsync(cancellationToken).ConfigureAwait(true);

            Audit.Clear();
            foreach (CalibrationAudit entry in entries.OrderByDescending(item => item.ChangedAtUtc))
            {
                Audit.Add(new LabelValueViewModel(
                    "Parameter_" + entry.ParameterKey,
                    string.Create(
                        CultureInfo.CurrentCulture,
                        $"{entry.ChangedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {entry.ChangedBy}"),
                    Localizer));
            }
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }
}

/// <summary>砂轮修整与更换记录里的一行。</summary>
public sealed class WheelEventRowViewModel
{
    public WheelEventRowViewModel(Data.Model.WheelEvent entry, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localizer);
        TimeText = entry.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        KindText = localizer["WheelEvent_" + entry.Kind];
        SourceText = localizer["WheelSource_" + entry.Source];
        DiameterText = entry.WheelDiameterMm is double diameter
            ? diameter.ToString("F1", CultureInfo.CurrentCulture)
            : "--";
        DetailText = string.Join(" · ", new[] { entry.ChangedBy, entry.Detail }.Where(part => part.Length > 0));
    }

    public string TimeText { get; }

    public string KindText { get; }

    public string SourceText { get; }

    public string DiameterText { get; }

    public string DetailText { get; }
}
