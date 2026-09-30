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
using RollGrinder.App.Interaction;
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
/// 参数区（界面最终稿 5.10）：横键 砂轮 · 标定 · 空 · 标定审计。
///
/// 这一区改的是**换一次砂轮就变**的那些值（砂轮直径、探头距离、对刀偏移、各项验收公差），
/// 不是机床固有能力——轴、行程、选装装置仍然由 machine.json 描述，装机时定下就不动（那是调试区的事）。
///
/// 参数格完全由 <see cref="MachineCalibration.Schema"/> 生成：
/// 新增一项标定值只写一行 schema + 一条 resx 文案，这一页与持久化都不用改（架构约束 ④）。
/// 有改动时竖键 7 / 8 变成"✕ 放弃改动 / ✓ 保存"；权限不够时路径条写"只读 · 要管理员权限"。
/// </summary>
public sealed partial class ParametersViewModel : PageViewModelBase
{
    /// <summary>横键"砂轮"（左栏的"砂轮"入口也打开它）。</summary>
    public const string WheelGroup = QuickBarCatalog.WheelGroup;

    /// <summary>横键"标定"。</summary>
    public const string CalibrationGroup = "calibration";

    /// <summary>横键"标定审计"。</summary>
    public const string AuditGroup = "audit";

    private readonly ICalibrationService calibration;
    private readonly IWheelChangeService wheelChange;
    private readonly IUserSession userSession;
    private readonly IWheelHistory wheelHistory;
    private readonly MachineCapability capability;
    private readonly Dictionary<string, FunctionKeyViewModel> groupKeys = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<FunctionKeyViewModel?> wheelKeys;
    private readonly IReadOnlyList<FunctionKeyViewModel?> calibrationKeys;
    private readonly IReadOnlyList<FunctionKeyViewModel?> auditKeys;
    private readonly IReadOnlyList<FunctionKeyViewModel?> wizardKeys;
    private readonly FunctionKeyViewModel discardKey;
    private readonly FunctionKeyViewModel saveKey;

    /// <summary>进入本页时的取值，"放弃改动"回到这里。</summary>
    private IReadOnlyList<string> committed = Array.Empty<string>();

    public ParametersViewModel(
        ICalibrationService calibration,
        IWheelChangeService wheelChange,
        IUserSession userSession,
        IWheelHistory wheelHistory,
        MachineCapability capability,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.wheelHistory = wheelHistory ?? throw new ArgumentNullException(nameof(wheelHistory));
        this.capability = capability ?? throw new ArgumentNullException(nameof(capability));
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.wheelChange = wheelChange ?? throw new ArgumentNullException(nameof(wheelChange));
        this.userSession = userSession ?? throw new ArgumentNullException(nameof(userSession));

        foreach ((string group, string label) in new[]
        {
            (WheelGroup, "Fn_Wheel"), (CalibrationGroup, "Fn_Calibration"), (AuditGroup, "Fn_CalibrationAudit"),
        })
        {
            string target = group;
            this.groupKeys[group] = FunctionKeyViewModel.ForAction(label, localizer, () => ShowGroup(target));
        }

        SetFunctionKeys(new FunctionKeyViewModel?[]
        {
            this.groupKeys[WheelGroup], this.groupKeys[CalibrationGroup], null, this.groupKeys[AuditGroup],
        });

        var reload = new FunctionKeyViewModel("Vk_Reload", ReloadCommand, localizer);
        this.wheelKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_ChangeWheel", StartWheelChangeCommand, localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_RegisterWheel", RegisterNewWheelCommand, localizer, requiresEditable: true),
            reload,
        };
        // 最终稿 5.10：重新读取在竖键 3，与砂轮组同位；7 / 8 留给放弃 / 保存。
        this.calibrationKeys = new FunctionKeyViewModel?[] { null, null, reload };
        this.auditKeys = new FunctionKeyViewModel?[] { new FunctionKeyViewModel("Vk_Reload", new AsyncRelayCommand(RefreshAuditAsync), localizer) };
        this.wizardKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_WizardNext", WheelChangeNextCommand, localizer, FunctionKeyKind.Primary, requiresEditable: true),
            new FunctionKeyViewModel("Vk_WizardSkip", SkipWheelCorrectionCommand, localizer, requiresEditable: true)
            {
                PreconditionResourceKey = "WheelChange_SkipOnlyAtVerify",
            },
            null,
            null,
            null,
            null,
            null,
            new FunctionKeyViewModel("Vk_WizardCancel", new RelayCommand(AskCancelWheelChange), localizer, FunctionKeyKind.Danger),
        };
        this.discardKey = new FunctionKeyViewModel("Vk_DiscardEdits", new RelayCommand(DiscardChanges), localizer, FunctionKeyKind.Cancel);
        this.saveKey = new FunctionKeyViewModel(
            "Vk_Save", new AsyncRelayCommand(() => SaveAsync(CancellationToken.None)), localizer, FunctionKeyKind.Confirm, requiresEditable: true);

        MaxSurfaceSpeedText = capability.MaxWheelSurfaceSpeedMPerSec is double maxSpeed
            ? localizer.Format("Wheel_MaxSurfaceSpeedFormat", maxSpeed)
            : "--";

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsDirty) or nameof(ActiveSubViewKey))
            {
                RefreshCommitPair();
            }
        };

        Rebuild();
        ShowGroup(WheelGroup);
        this.loadOnShow = true;
        this.calibration.Changed += (_, _) => OnUiThread(Rebuild);
        this.wheelChange.Changed += (_, _) => OnUiThread(RefreshWheelChange);

        // 程序里的修整由后台服务记账，事件在后台线程上来：只记一笔"旧了"，由界面节拍去刷。
        this.wheelHistory.Changed += (_, _) => this.wheelHistoryStale = true;
    }

    /// <summary>当前是哪一组。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWheelGroup), nameof(IsCalibrationGroup), nameof(IsAuditGroup))]
    private string group = WheelGroup;

    public bool IsWheelGroup => Group == WheelGroup;

    public bool IsCalibrationGroup => Group == CalibrationGroup;

    public bool IsAuditGroup => Group == AuditGroup;

    /// <summary>构造时只摆好键、不读库 / 文件（那时数据库和配置可能还没就绪）；读在切到本页时做。</summary>
    private readonly bool loadOnShow;

    public override bool ShowGroup(string groupKey)
    {
        if (!this.groupKeys.TryGetValue(groupKey, out FunctionKeyViewModel? key))
        {
            return false;
        }

        if (ActiveSubViewKey is not null)
        {
            Navigator.CloseSubView();
        }

        Group = groupKey;
        MarkActiveFunctionKey(key);
        SetVerticalKeys(GroupKeys());
        if (!this.loadOnShow)
        {
            return true;
        }

        if (groupKey == WheelGroup)
        {
            FocusedWheelKey = CalibrationKeys.WheelDiameterMm;
            _ = RunGuardedAsync(RefreshWheelHistoryAsync, CancellationToken.None);
        }
        else if (groupKey == AuditGroup)
        {
            _ = RefreshAuditAsync(CancellationToken.None);
        }

        return true;
    }

    private IReadOnlyList<FunctionKeyViewModel?> GroupKeys() => Group switch
    {
        CalibrationGroup => this.calibrationKeys,
        AuditGroup => this.auditKeys,
        _ => this.wheelKeys,
    };

    /// <summary>有没存的改动、又不在向导里：竖键 7 / 8 是"✕ 放弃改动 / ✓ 保存"。</summary>
    private void RefreshCommitPair()
    {
        bool show = IsDirty && ActiveSubViewKey is null;
        SetCommitPair(show ? this.discardKey : null, show ? this.saveKey : null);
    }

    // ── 砂轮（最终稿 5.10）：砂轮数据、修整参数、修整与更换记录 ──────────────────

    private static readonly string[] WheelKeys =
    {
        CalibrationKeys.WheelDiameterMm, CalibrationKeys.NewWheelDiameterMm, CalibrationKeys.WheelWidthMm,
    };

    private static readonly string[] DressKeys =
    {
        CalibrationKeys.DressInfeedRadiusMicrometer, CalibrationKeys.DressPassCount,
        CalibrationKeys.DressFeedMmPerMin, CalibrationKeys.DressIntervalRolls,
    };

    /// <summary>砂轮数据的参数格（和标定值同一批格子：在这里改、按"✓ 保存"一起存）。</summary>
    public ObservableCollection<ParameterRowViewModel> WheelRows { get; } = new();

    /// <summary>修整参数的参数格。程序里插"砂轮修整"时照这里的值填。</summary>
    public ObservableCollection<ParameterRowViewModel> DressRows { get; } = new();

    /// <summary>修整与更换记录，新的在前。</summary>
    public ObservableCollection<WheelEventRowViewModel> WheelHistory { get; } = new();

    /// <summary>机床允许的砂轮最高线速度（machine.json）；没配为"--"。</summary>
    public string MaxSurfaceSpeedText { get; }

    /// <summary>光标所在的砂轮 / 修整参数：简图上亮它。</summary>
    [ObservableProperty]
    private string focusedWheelKey = CalibrationKeys.WheelDiameterMm;

    /// <summary>
    /// 竖键"新砂轮登记"：新砂轮到货先把标称直径登上（还没装上去）。光标落到"新砂轮直径"格、对话行说怎么做；
    /// 装上以后再走"换砂轮 ▸"向导试磨反推真实直径。
    /// </summary>
    [RelayCommand]
    private void RegisterNewWheel()
    {
        FocusedWheelKey = CalibrationKeys.NewWheelDiameterMm;
        if (Row(CalibrationKeys.NewWheelDiameterMm) is { } row)
        {
            Interaction.Hint(row.HintText);
        }

        Say("Wheel_RegisterHint");
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
        SkipWheelCorrectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(WizardDiagramMode));
        OnPropertyChanged(nameof(WizardHighlightKey));
    }

    public override PageKey Key => PageKey.Parameters;

    public override string TitleResourceKey => "Page_Parameters";


    /// <summary>自动循环挂着程序时落只读锁：标定值一改，正在跑的程序算出来的位置就变了。</summary>
    public override bool LocksDuringRun => true;

    /// <summary>标定、砂轮与修整参数归管理员（Q9）。</summary>
    public override Permission? EditPermission => Permission.EditCalibration;

    protected override void OnAccessChanged() => OnPropertyChanged(nameof(CanEdit));

    /// <summary>离线可用：只和数据库与配置打交道，不碰机床。</summary>
    public override bool WorksOffline => true;

    /// <summary>标定值的参数格。</summary>
    public ObservableCollection<ParameterRowViewModel> Values { get; } = new();

    /// <summary>
    /// 标定值按部件分组（修改稿 5③）：砂轮、金刚笔与修整、测头、基准盘、对刀、验收公差。
    /// 和 <see cref="Values"/> 是同一批格子，只是分组摆；新加的标定项没归组时落在"其他"。
    /// </summary>
    public ObservableCollection<CalibrationGroupViewModel> CalibrationGroups { get; } = new();

    private static readonly (string TitleKey, string[] Keys)[] CalibrationLayout =
    {
        ("CalGroup_Wheel", new[] { CalibrationKeys.WheelDiameterMm, CalibrationKeys.NewWheelDiameterMm, CalibrationKeys.WheelWidthMm }),
        ("CalGroup_Dresser", new[]
        {
            CalibrationKeys.DresserOffsetMm, CalibrationKeys.DresserReferenceOffsetMm, CalibrationKeys.DressInfeedRadiusMicrometer,
            CalibrationKeys.DressPassCount, CalibrationKeys.DressFeedMmPerMin, CalibrationKeys.DressIntervalRolls,
        }),
        ("CalGroup_Probe", new[] { CalibrationKeys.ProbeBToWheelCentreMm, CalibrationKeys.ProbeBToWheelSurfaceMm }),
        ("CalGroup_ReferenceDisc", new[] { CalibrationKeys.ReferenceDiscDiameterMm, CalibrationKeys.ReferenceDiscOffsetMm }),
        ("CalGroup_Touch", new[] { CalibrationKeys.TouchOffsetMm, CalibrationKeys.TouchMode, CalibrationKeys.ShortStrokeCurrentA }),
        ("CalGroup_Tolerance", new[]
        {
            CalibrationKeys.ProfileToleranceMicrometer, CalibrationKeys.RoundnessToleranceMicrometer, CalibrationKeys.CentringToleranceMicrometer,
        }),
    };

    private void BuildCalibrationGroups()
    {
        CalibrationGroups.Clear();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string titleKey, string[] keys) in CalibrationLayout)
        {
            ParameterRowViewModel[] rows = keys.Select(Row).OfType<ParameterRowViewModel>().ToArray();
            placed.UnionWith(rows.Select(row => row.Key));
            if (rows.Length > 0)
            {
                CalibrationGroups.Add(new CalibrationGroupViewModel(Localizer[titleKey], rows));
            }
        }

        ParameterRowViewModel[] others = Values.Where(row => !placed.Contains(row.Key)).ToArray();
        if (others.Length > 0)
        {
            CalibrationGroups.Add(new CalibrationGroupViewModel(Localizer["CalGroup_Other"], others));
        }
    }

    /// <summary>每一项最后是谁在什么时候改的。</summary>
    public ObservableCollection<LabelValueViewModel> Audit { get; } = new();

    /// <summary>砂轮已经磨掉多少（直径量 mm），顶部提示用。</summary>
    [ObservableProperty]
    private string wheelWearText = "--";

    /// <summary>
    /// 管理员以上才改得动。操作工看得见但改不了——
    /// 藏起来只会让人以为软件少做，标出来才知道是权限不够。
    /// </summary>
    public bool CanEdit => this.userSession.Can(Permission.EditCalibration) && !IsRunLocked;

    public override bool CanSave => CanEdit && IsDirty;

    public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        if (!CanEdit)
        {
            Interaction.Refuse(Localizer["Settings_NeedsAdministrator"]);
            return false;
        }

        var pairs = new List<KeyValuePair<string, ParameterValue>>(Values.Count);
        foreach (ParameterRowViewModel row in Values)
        {
            ParameterValue? value = row.ToParameterValue();
            if (value is null)
            {
                Interaction.Refuse(Localizer["Settings_ValueInvalid"]);
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
            Say("Settings_Saved");
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
            Say("Settings_Reloaded");
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
        SetVerticalKeys(this.wizardKeys);
    }

    /// <summary>向导收起：竖键回到本组。</summary>
    private void CloseWizard()
    {
        Navigator.CloseSubView();
        SetVerticalKeys(GroupKeys());
    }

    /// <summary>竖键"放弃换砂轮…"：已经切成手动对刀的话会切回去，所以先问一句。</summary>
    private void AskCancelWheelChange() => Ask("WheelChange_AskCancel", () => CancelWheelChangeAsync(CancellationToken.None));

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
                        Interaction.Refuse(Localizer["WheelChange_NeedDiameter"]);
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
                        Interaction.Refuse(Localizer["WheelChange_NeedTrialDiameters"]);
                        return;
                    }

                    this.wheelChange.RecordTrial(expectedMm, measuredMm);
                    break;

                case WheelChangeStage.Verify:
                    this.wheelChange.AcceptCorrection();
                    break;

                case WheelChangeStage.RestoreTouchMode:
                    await this.wheelChange.FinishAsync(changedBy, token).ConfigureAwait(true);
                    Say("WheelChange_Done");
                    CloseWizard();
                    return;

                default:
                    return;
            }

            RefreshWheelChange();
        }, cancellationToken);

    /// <summary>这一刀不作数：不改砂轮直径，但对刀方式照样要改回去。</summary>
    [RelayCommand(CanExecute = nameof(CanSkipWheelCorrection))]
    private void SkipWheelCorrection()
    {
        if (this.wheelChange.Current?.Stage != WheelChangeStage.Verify)
        {
            return;
        }

        this.wheelChange.SkipCorrection();
        RefreshWheelChange();
    }

    private bool CanSkipWheelCorrection() => WheelChangeStage == WheelChangeStage.Verify;

    /// <summary>中途放弃：已经切成手动对刀的话，退出前切回去。</summary>
    private Task CancelWheelChangeAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            await this.wheelChange
                .CancelAsync(this.userSession.CurrentUser?.UserName ?? string.Empty, token)
                .ConfigureAwait(true);

            CloseWizard();
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
        if (!IsDirty)
        {
            Capture();
        }

        OnPropertyChanged(nameof(CanEdit));
        _ = RefreshAuditAsync(CancellationToken.None);
        if (IsWheelGroup)
        {
            _ = RunGuardedAsync(RefreshWheelHistoryAsync, CancellationToken.None);
        }
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

        BuildCalibrationGroups();

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

/// <summary>设置页里一组标定值（一个部件）。</summary>
public sealed class CalibrationGroupViewModel
{
    public CalibrationGroupViewModel(string title, IReadOnlyList<ParameterRowViewModel> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string Title { get; }

    public IReadOnlyList<ParameterRowViewModel> Rows { get; }
}
