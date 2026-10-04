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
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;
using RollGrinder.Services.Measurement;
using RollGrinder.Services.Calibration;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Manual;
using RollGrinder.Services.Session;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.App.ViewModels;

/// <summary>工序序列里一行的状态。</summary>
public enum StepRowState
{
    /// <summary>还没轮到。</summary>
    Pending = 0,

    /// <summary>正在执行。</summary>
    Current = 1,

    /// <summary>下一道。</summary>
    Next = 2,

    /// <summary>已完成。</summary>
    Done = 3,
}

/// <summary>
/// 参数矩阵里的一列：一道工序的列头。状态（当前/下一道/已完成）跟着机床走，
/// 与左侧工序序列用的是同一套 <see cref="StepRowState"/> 与同一套配色。
/// </summary>
public sealed partial class MatrixColumnViewModel : ObservableObject
{
    public MatrixColumnViewModel(int order, string displayName)
    {
        Order = order;
        OrderText = order.ToString("00", CultureInfo.InvariantCulture);
        DisplayName = displayName;
    }

    public int Order { get; }

    public string OrderText { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private StepRowState state = StepRowState.Pending;
}

/// <summary>参数矩阵里的一格。</summary>
public sealed partial class MatrixCellViewModel : ObservableObject
{
    private readonly string original;

    public MatrixCellViewModel(
        int stepOrder,
        string parameterKey,
        string text,
        bool isApplicable,
        bool isLiveEditable)
    {
        StepOrder = stepOrder;
        ParameterKey = parameterKey;
        IsApplicable = isApplicable;
        IsLiveEditable = isLiveEditable;
        this.original = text;
        this.text = text;
    }

    public int StepOrder { get; }

    public string ParameterKey { get; }

    /// <summary>显示/编辑文本；这道工序没有这个参数时是空串。</summary>
    [ObservableProperty]
    private string text;

    /// <summary>这道工序有没有这个参数。没有就留空——空格的含义不是"值为 0"。</summary>
    public bool IsApplicable { get; }

    /// <summary>这个参数能不能在**正在跑的那道工序**上改。</summary>
    public bool IsLiveEditable { get; }

    /// <summary>这一格所属的工序是不是正在跑的那一道。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private StepRowState state = StepRowState.Pending;

    /// <summary>
    /// 这一格现在改不改得动。
    ///
    /// 已经磨完的工序不给改（改了也没用，还会让记录对不上实际磨的东西）；
    /// 正在跑的那一道只给改标了可在线调整的参数；还没轮到的工序随便改。
    /// </summary>
    public bool CanEdit => IsApplicable && State switch
    {
        StepRowState.Done => false,
        StepRowState.Current => IsLiveEditable,
        _ => true,
    };

    /// <summary>改过还没下发：界面上标红，与 RGI 的做法一致。</summary>
    public bool IsModified => !string.Equals(Text, this.original, StringComparison.Ordinal);

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(IsModified));
}

/// <summary>参数矩阵里的一行：一个参数横着看过去。</summary>
public sealed class MatrixRowViewModel
{
    public MatrixRowViewModel(string label, string unitText, IReadOnlyList<MatrixCellViewModel> cells)
    {
        Label = label;
        UnitText = unitText;
        Cells = cells;
    }

    public string Label { get; }

    /// <summary>单位后缀，无量纲时为空。</summary>
    public string UnitText { get; }

    public IReadOnlyList<MatrixCellViewModel> Cells { get; }
}

/// <summary>
/// 参数区"看一道"时的一格：参数名、单位，以及矩阵里那一格本身（同一个对象——这里改了，总表里也是改过的样子，
/// 下发 / 放弃按同一套走）。
/// </summary>
public sealed record FocusCellViewModel(string Label, string UnitText, MatrixCellViewModel Cell);

/// <summary>工序序列里的一行。</summary>
public sealed partial class SequenceRowViewModel : ObservableObject
{
    public SequenceRowViewModel(int order, string displayName, string durationText)
    {
        OrderText = order.ToString("00", CultureInfo.InvariantCulture);
        Order = order;
        DisplayName = displayName;
        DurationText = durationText;
    }

    public int Order { get; }

    public string OrderText { get; }

    public string DisplayName { get; }

    /// <summary>预计时长，例如"约 211 min"。</summary>
    public string DurationText { get; }

    [ObservableProperty]
    private StepRowState state;

    [ObservableProperty]
    private string passText = string.Empty;

    /// <summary>参数区正在看这一道（左侧描一道竖条）。</summary>
    [ObservableProperty]
    private bool isFocused;
}

/// <summary>右侧实时数据里的一行。</summary>
public sealed partial class LiveValueViewModel : ObservableObject
{
    public LiveValueViewModel(string labelResourceKey, IStringLocalizer localizer, bool highlight = false)
    {
        Label = localizer[labelResourceKey];
        Highlight = highlight;
    }

    public string Label { get; }

    public bool Highlight { get; }

    [ObservableProperty]
    private string valueText = "--";
}

/// <summary>主界面可切换的曲线。</summary>
public enum CurveKind
{
    Error = 0,
    Reference = 1,
    Roundness = 2,
    Eccentricity = 3,
    GrindingCurrent = 4,
}

/// <summary>结果横幅的一行判定依据：项目、实测、允许、结论。</summary>
public sealed record VerdictRowViewModel(string ItemText, string MeasuredText, string AllowedText, string ResultText, bool IsFail);

/// <summary>要确认的流程动作。</summary>
internal enum StepFlowAction
{
    /// <summary>跳到指定工序。</summary>
    Jump = 0,

    /// <summary>当前工序提前结束。</summary>
    EndEarly = 1,

    /// <summary>请 NC 启动循环。</summary>
    CycleStart = 2,
}

/// <summary>
/// 自动磨削（主界面）。版面见 docs/design/B-Light-自动磨削.html。
/// 只读取监视服务发布的快照与数据库里的作业，不直接碰网关。
/// </summary>
public sealed partial class AutoGrindingViewModel : PageViewModelBase
{
    private readonly IMachineMonitor monitor;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;
    private readonly IGrindingRecordRepository records;
    private readonly IJobRepository jobs;
    private readonly IMeasurementRepository measurements;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly ICalibrationService calibration;
    private readonly IStepParameterUpdateService stepUpdates;
    private readonly IStepFlowControlService stepFlow;
    private readonly ISurfaceTraceService traces;
    private readonly IManualCommandService manualCommands;
    private readonly IUserSession userSession;
    private readonly IMeasurementNotifications measurementNotifications;

    /// <summary>上一次按哪个测量计数刷新的误差曲线与 RMS；计数变了说明后台又存下了一次测量。</summary>
    private long seenMeasurementVersion;

    private readonly LiveValueViewModel probeA;
    private readonly LiveValueViewModel probeB;
    private readonly LiveValueViewModel centringDeviation;
    private readonly LiveValueViewModel wheelDiameter;
    private readonly LiveValueViewModel grindingCurrent;
    private readonly LiveValueViewModel currentPass;

    private readonly FunctionKeyViewModel compensationKey;
    private readonly FunctionKeyViewModel parameterTableKey;
    private readonly FunctionKeyViewModel overviewKey;
    private readonly FunctionKeyViewModel jumpKey;
    private readonly FunctionKeyViewModel endEarlyKey;
    private readonly FunctionKeyViewModel coolantKey;
    private readonly FunctionKeyViewModel discardMatrixKey;
    private readonly FunctionKeyViewModel downloadMatrixKey;
    private readonly Dictionary<CurveKind, FunctionKeyViewModel> curveKeys = new();

    private GrindingJob? activeJob;
    private readonly IRecordService recordService;
    private readonly IRollPlanningService planning;
    private bool lastCycleComplete;
    private string? finishJobId;

    private readonly JobDraft jobDraft;

    /// <summary>横键"磨削记录"（最终稿：并入原"测量记录"）：打开记录区，选中正在磨的这支辊。</summary>
    private void OpenRecordsOfThisRoll()
    {
        this.jobDraft.RecordsJobId = this.activeJob?.JobId;
        Navigator.StartTask(PageKey.Records, PageKey.AutoGrinding);
    }
    private IReadOnlyList<GrindingStepPlan> activePlans = Array.Empty<GrindingStepPlan>();

    public AutoGrindingViewModel(
        IMachineMonitor monitor,
        MachineDescription machine,
        HmiSettings settings,
        IGrindingRecordRepository records,
        IJobRepository jobs,
        IMeasurementRepository measurements,
        GrindingStepTypeRegistry stepTypes,
        RollProfileTypeRegistry profileTypes,
        ICalibrationService calibration,
        IStepParameterUpdateService stepUpdates,
        IStepFlowControlService stepFlow,
        ISurfaceTraceService traces,
        IManualCommandService manualCommands,
        IUserSession userSession,
        IMeasurementNotifications measurementNotifications,
        ICompensationTuningService compensationTuning,
        IStrokeCompensationLog strokeCompensationLog,
        IRecordService recordService,
        IRollPlanningService planning,
        JobDraft jobDraft,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.jobDraft = jobDraft ?? throw new ArgumentNullException(nameof(jobDraft));
        this.recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        this.planning = planning ?? throw new ArgumentNullException(nameof(planning));
        this.measurementNotifications = measurementNotifications ?? throw new ArgumentNullException(nameof(measurementNotifications));
        this.seenMeasurementVersion = measurementNotifications.Version;
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.stepUpdates = stepUpdates ?? throw new ArgumentNullException(nameof(stepUpdates));
        this.stepFlow = stepFlow ?? throw new ArgumentNullException(nameof(stepFlow));
        this.traces = traces ?? throw new ArgumentNullException(nameof(traces));
        this.manualCommands = manualCommands ?? throw new ArgumentNullException(nameof(manualCommands));
        this.userSession = userSession ?? throw new ArgumentNullException(nameof(userSession));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.measurements = measurements ?? throw new ArgumentNullException(nameof(measurements));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));

        this.probeA = new LiveValueViewModel("Live_ProbeA", localizer);
        this.probeB = new LiveValueViewModel("Live_ProbeB", localizer);
        this.centringDeviation = new LiveValueViewModel("Live_CentringDeviation", localizer, highlight: true);
        this.wheelDiameter = new LiveValueViewModel("Live_WheelDiameter", localizer);
        this.grindingCurrent = new LiveValueViewModel("Live_GrindingCurrent", localizer);
        this.currentPass = new LiveValueViewModel("Live_CurrentPass", localizer);

        // X、Z 挪到了顶上的状态带里（修改稿 5.5），右栏只留测量与过程量。
        StatusBand = new StatusBandViewModel(machine, localizer);

        LiveValues = new ObservableCollection<LiveValueViewModel>
        {
            this.probeA, this.probeB, this.centringDeviation,
            this.wheelDiameter, this.grindingCurrent, this.currentPass,
        };

        RefreshTolerance();
        calibration.Changed += (_, _) => OnUiThread(RefreshTolerance);
        InitializeCompensation(compensationTuning, strokeCompensationLog);

        // 横键 = 功能组（最终稿 5.2）：补偿 · 磨削记录 · 状态总览 · 工序跳转… · 提前结束… · 空 · 作业 · 冷却液。
        // 循环启动、暂停在按钮板上（machine.json panelActions），屏幕上不放——停止类不依赖上位机（C4、C7）。
        // 按钮板没装的现场才把它们放回来：启动占"空"那一格，暂停排到第二页。
        this.compensationKey = FunctionKeyViewModel.ForAction("Fn_Compensation", localizer, ToggleCompensation);
        this.overviewKey = FunctionKeyViewModel.ForAction("Fn_ProgramBlock", localizer, ToggleStatusOverview);
        this.jumpKey = new FunctionKeyViewModel("Fn_JumpToStep", new RelayCommand(OpenJumpMenu), localizer)
        {
            IsMachineCommand = true,
            RequiredPermission = Permission.RunMachine,
        };
        this.endEarlyKey = new FunctionKeyViewModel("Fn_EndEarly", EndStepEarlyCommand, localizer)
        {
            IsMachineCommand = true,
            RequiredPermission = Permission.RunMachine,
        };
        this.coolantKey = new FunctionKeyViewModel("Vk_Coolant", ToggleCoolantCommand, localizer)
        {
            RequiredPermission = Permission.RunMachine,
        };

        // 横键（界面修订稿 v3 5.1）：补偿 · 程序段 · 工序跳转… · 提前结束… · 磨削记录 · 作业；
        // 按钮板上没有的循环启动 / 暂停补在后面。冷却挪到竖键 6（运行中随手开关，不占横键）。
        this.parameterTableKey = FunctionKeyViewModel.ForAction("Fn_ParameterTable", localizer, () => IsMatrixOverview = !IsMatrixOverview);
        var functionKeys = new List<FunctionKeyViewModel?>
        {
            this.compensationKey,
            this.overviewKey,
            this.jumpKey,
            this.endEarlyKey,
            FunctionKeyViewModel.ForAction("Fn_GrindingRecords", localizer, OpenRecordsOfThisRoll),
            FunctionKeyViewModel.ForAction("Fn_Job", localizer, () => Navigator.GoTo(PageKey.Job)),
            this.parameterTableKey,
        };
        if (!machine.IsOnPanel(MachineDescription.PanelCycleStart))
        {
            functionKeys.Add(new FunctionKeyViewModel("Fn_CycleStart", RequestCycleStartCommand, localizer, FunctionKeyKind.Start)
            {
                IsMachineCommand = true,
                RequiredPermission = Permission.RunMachine,
            });
        }

        if (!machine.IsOnPanel(MachineDescription.PanelFeedHold))
        {
            functionKeys.Add(new FunctionKeyViewModel("Fn_Pause", RequestFeedHoldCommand, localizer)
            {
                RequiredPermission = Permission.RunMachine,
            });
        }

        SetFunctionKeys(functionKeys);

        // 参数矩阵改了：竖键 7 / 8 = 放弃改动 / 下发改动（最终稿 5.2）。下发就是确认，不再多问一句。
        this.discardMatrixKey = new FunctionKeyViewModel("Vk_DiscardEdits", DiscardMatrixEditsCommand, localizer, FunctionKeyKind.Cancel);
        this.downloadMatrixKey = new FunctionKeyViewModel("Vk_DownloadEdits", SaveMatrixCommand, localizer, FunctionKeyKind.Confirm)
        {
            IsMachineCommand = true,
            RequiredPermission = Permission.RunMachine,
        };
        foreach (CurveKind kind in Enum.GetValues<CurveKind>())
        {
            CurveKind chosen = kind;
            this.curveKeys[kind] = FunctionKeyViewModel.ForAction("Curve_" + kind, localizer, () => SelectCurve(chosen));
        }

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveSubViewKey))
            {
                ApplyVerticalKeys();
            }
        };
        ApplyVerticalKeys();
    }

    /// <summary>磨完的结果横幅（界面修订稿 v3 U7）：✓ 合格 / ✗ 不合格 + 判定依据。</summary>
    public const string FinishSubView = "SubView_Finish";

    public bool IsFinishOpen => ActiveSubViewKey == FinishSubView;

    /// <summary>判定：true 合格、false 不合格、null 没量到（不下结论）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinishPassedTrue), nameof(FinishPassedFalse))]
    private bool? finishPassed;

    public bool FinishPassedTrue => FinishPassed == true;

    public bool FinishPassedFalse => FinishPassed == false;

    [ObservableProperty]
    private string finishTitle = string.Empty;

    public ObservableCollection<VerdictRowViewModel> FinishRows { get; } = new();

    /// <summary>NC 报"循环正常结束"的那一拍：等记录收尾（合格判定写进去）再打开横幅。</summary>
    private async Task ShowFinishAsync(string jobId, CancellationToken cancellationToken)
    {
        GrindingRecord? record = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            record = await this.records.GetLatestByJobAsync(jobId, cancellationToken).ConfigureAwait(true);
            if (record?.FinishedAtUtc is not null)
            {
                break;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(true);
        }

        if (record?.FinishedAtUtc is null)
        {
            return;
        }

        GrindingVerdict? verdict = await this.recordService.LoadVerdictAsync(record.RecordId, cancellationToken).ConfigureAwait(true);
        this.finishJobId = jobId;
        FinishPassed = verdict?.Passed;
        FinishTitle = Localizer.Format(
            FinishPassed switch { true => "Finish_PassedFormat", false => "Finish_FailedFormat", _ => "Finish_UnknownFormat" },
            this.activeJob?.RollId ?? jobId);
        FinishRows.Clear();
        foreach (VerdictItem item in verdict?.Items ?? Array.Empty<VerdictItem>())
        {
            FinishRows.Add(new VerdictRowViewModel(
                Localizer["Verdict_" + item.ItemKey],
                item.MeasuredMicrometer?.ToString("F1", CultureInfo.CurrentCulture) ?? "--",
                item.AllowedMicrometer.ToString("F1", CultureInfo.CurrentCulture),
                item.Passed switch { true => Localizer["Result_Pass"], false => Localizer["Result_Fail"], _ => "--" },
                item.Passed == false));
        }

        Navigator.OpenSubView(FinishSubView);
    }

    /// <summary>返磨…：问一句，建一份返磨草稿（同计划、标"返磨"），到作业页它排在待磨清单最前。</summary>
    private void AskRegrind()
    {
        if (this.finishJobId is not { } jobId)
        {
            return;
        }

        Ask(
            "Finish_AskRegrind",
            async () => await RunGuardedAsync(
                async token =>
                {
                    await this.planning.CreateRegrindAsync(jobId, token).ConfigureAwait(true);
                    Say("Finish_RegrindCreated");
                    Navigator.CloseSubView();
                    Navigator.GoTo(PageKey.Job);
                },
                CancellationToken.None).ConfigureAwait(true));
    }

    private void OpenFinishRecords()
    {
        this.jobDraft.RecordsJobId = this.finishJobId;
        Navigator.CloseSubView();
        Navigator.StartTask(PageKey.Records, PageKey.AutoGrinding);
    }

    /// <summary>程序段子功能（界面修订稿 v3：取代"状态总览"，机构灯已在上排）：NC 当前 / 下一程序段、工序与道次。</summary>
    public const string StatusOverviewSubView = "SubView_ProgramBlock";

    /// <summary>程序段开着没有。</summary>
    public bool IsStatusOverviewOpen => ActiveSubViewKey == StatusOverviewSubView;

    /// <summary>程序段子视图：NC 程序名、当前段、下一段、工序与道次。</summary>
    public ObservableCollection<LabelValueViewModel> BlockRows { get; } = new();

    [ObservableProperty]
    private string currentBlockText = "--";

    [ObservableProperty]
    private string nextBlockText = "--";

    private void UpdateBlocks(MachineStateSnapshot snapshot)
    {
        CurrentBlockText = snapshot.GetTextOrNull(MachineTagKeys.CurrentBlock) is { Length: > 0 } current ? current : "--";
        NextBlockText = snapshot.GetTextOrNull(MachineTagKeys.NextBlock) is { Length: > 0 } next ? next : "--";
        if (!IsStatusOverviewOpen)
        {
            return;
        }

        int order = (int)(snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentStepOrder) ?? 0);
        string stepName = this.activeJob?.Steps.FirstOrDefault(step => step.Order == order) is { } step
            ? Localizer["StepType_" + step.StepTypeKey]
            : "--";
        BlockRows.Clear();
        BlockRows.Add(new LabelValueViewModel("Block_Program", snapshot.GetTextOrNull(MachineTagKeys.ProgramName) ?? "--", Localizer));
        BlockRows.Add(new LabelValueViewModel("Block_Job", this.activeJob is { } job ? job.JobId + " · " + job.RollId : "--", Localizer));
        BlockRows.Add(new LabelValueViewModel("Block_Step", order > 0 ? order.ToString(CultureInfo.InvariantCulture) + " · " + stepName : "--", Localizer));
        BlockRows.Add(new LabelValueViewModel("Block_Pass", this.currentPass.ValueText, Localizer));
    }

    /// <summary>
    /// 竖键随子功能换（最终稿 5.2、5.3）：基本画面是 5 条曲线；补偿里是保存 / 恢复 / 改动记录 / 返回；
    /// 状态总览里只有返回。
    /// </summary>
    private void ApplyVerticalKeys()
    {
        this.compensationKey.IsActive = IsCompensationOpen;
        this.overviewKey.IsActive = IsStatusOverviewOpen;
        OnPropertyChanged(nameof(IsStatusOverviewOpen));
        OnPropertyChanged(nameof(IsFinishOpen));

        if (IsCompensationOpen)
        {
            SetVerticalKeys(new FunctionKeyViewModel?[]
            {
                new FunctionKeyViewModel("Vk_SaveTuning", this.saveTuningCommand, Localizer)
                {
                    RequiredPermission = Permission.EditCompensation,
                },
                new FunctionKeyViewModel("Vk_ResetTuning", this.resetTuningCommand, Localizer)
                {
                    RequiredPermission = Permission.EditCompensation,
                },
                FunctionKeyViewModel.ForAction("Vk_ChangeLog", Localizer, () => Navigator.GoToArea(AreaKey.Diagnostics, "audit")),
                null, null, null, null,
                BackKey(),
            });
            return;
        }

        if (IsStatusOverviewOpen)
        {
            SetVerticalKeys(new FunctionKeyViewModel?[] { null, null, null, null, null, this.coolantKey, null, BackKey() });
            return;
        }

        if (IsFinishOpen)
        {
            // 结果横幅（界面修订稿 v3 U7）：磨削记录 · 返磨… · 打印… · 下一支辊 ▸。
            SetVerticalKeys(new FunctionKeyViewModel?[]
            {
                FunctionKeyViewModel.ForAction("Vk_FinishRecords", Localizer, OpenFinishRecords),
                new FunctionKeyViewModel("Vk_Regrind", new RelayCommand(AskRegrind), Localizer, requiresEditable: false)
                {
                    RequiredPermission = Permission.EditJobs,
                },
                FunctionKeyViewModel.ForAction("Vk_FinishPrint", Localizer, OpenFinishRecords),
                FunctionKeyViewModel.ForAction("Vk_NextRoll", Localizer, () =>
                {
                    Navigator.CloseSubView();
                    Navigator.GoTo(PageKey.Job);
                }),
                null,
                this.coolantKey,
                null,
                BackKey(),
            });
            return;
        }

        // 竖键 1–5 选曲线，6 冷却（界面修订稿 v3 5.1）。
        SetVerticalKeys(this.curveKeys.Values.Cast<FunctionKeyViewModel?>().Append(this.coolantKey));
        MarkSelectedCurve();
    }

    private FunctionKeyViewModel BackKey() =>
        new("Vk_Back", new RelayCommand(Navigator.CloseSubView), Localizer, FunctionKeyKind.Navigation);

    private void MarkSelectedCurve()
    {
        foreach ((CurveKind kind, FunctionKeyViewModel key) in this.curveKeys)
        {
            key.IsActive = kind == SelectedCurve;
        }
    }

    private void ToggleCompensation()
    {
        if (IsCompensationOpen)
        {
            Navigator.CloseSubView();
        }
        else
        {
            OpenCompensation();
        }
    }

    private void ToggleStatusOverview()
    {
        if (IsStatusOverviewOpen)
        {
            Navigator.CloseSubView();
        }
        else
        {
            Navigator.OpenSubView(StatusOverviewSubView);
        }
    }

    partial void OnHasPendingEditsChanged(bool value) =>
        SetCommitPair(value ? this.discardMatrixKey : null, value ? this.downloadMatrixKey : null);

    public override PageKey Key => PageKey.AutoGrinding;

    public override string TitleResourceKey => "Page_AutoGrinding";


    /// <summary>左栏：工序序列。</summary>
    public ObservableCollection<SequenceRowViewModel> Sequence { get; } = new();

    /// <summary>右栏：实时数据。</summary>
    public ObservableCollection<LiveValueViewModel> LiveValues { get; }

    /// <summary>顶上常驻的状态带：方式、通道、程序、X、Z、转速与四盏机构灯（修改稿 3③）。</summary>
    public StatusBandViewModel StatusBand { get; }

    /// <summary>
    /// 参数矩阵的列头：一道工序一列，红色是正在跑的那一道，黄色是下一道。
    /// </summary>
    public ObservableCollection<MatrixColumnViewModel> MatrixColumns { get; } = new();

    /// <summary>
    /// 参数矩阵：行 = 参数，列 = 工序，一屏看完整支程序。
    ///
    /// 自动磨削时操作工要看的是"各道的拖板速度是怎么一路降下来的"这种横向对比，
    /// 一次只显示一道工序的话，这些都得靠翻页在脑子里拼。
    /// 编程时相反——一次专心改一道，所以工序编程页仍然是单工序视图。
    /// </summary>
    public ObservableCollection<MatrixRowViewModel> MatrixRows { get; } = new();

    /// <summary>矩阵里有东西可看没有。没装载作业时整块收起来。</summary>
    [ObservableProperty]
    private bool hasMatrix;

    /// <summary>矩阵里有改过还没下发的格子。"保存参数"按它点亮。</summary>
    [ObservableProperty]
    private bool hasPendingEdits;

    /// <summary>改参数的结果提示（下发了 / 为什么被挡）。</summary>
    [ObservableProperty]
    private string matrixMessage = string.Empty;

    /// <summary>机床当前跑到第几道；0 表示还没开始。</summary>
    private int currentStepOrder;

    private void RefreshMatrixDirty() =>
        HasPendingEdits = MatrixRows.Any(row => row.Cells.Any(cell => cell.IsModified));

    /// <summary>
    /// 把矩阵里改过的格子下发下去。
    ///
    /// 这不是一条实时通道：新值写进那一道工序的 R 参数，NC 在下一道次读取；
    /// 上位机写完就脱手，被强制结束时 NC 拿最后收到的值把这支辊磨完（最高原则）。
    /// 规则（哪一道能改、哪个参数能改、改完还站不站得住）全在
    /// <see cref="IStepParameterUpdateService"/> 里，界面只负责把值收上来。
    /// </summary>
    [RelayCommand]
    private async Task SaveMatrixAsync(CancellationToken cancellationToken)
    {
        if (this.activeJob is null || !HasPendingEdits)
        {
            return;
        }

        GrindingJob? edited = CollectEditedJob();
        if (edited is null)
        {
            MatrixMessage = Localizer["Auto_MatrixValueInvalid"];
            return;
        }

        try
        {
            StepUpdateResult result = await this.stepUpdates.UpdateAsync(
                this.activeJob,
                edited,
                this.currentStepOrder,
                this.userSession.CurrentUser?.UserName ?? string.Empty,
                cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                MatrixMessage = Describe(result);
                Interaction.Fail(MatrixMessage);
                return;
            }

            this.activeJob = edited;
            this.activePlans = edited.Steps
                .Select(step => this.stepTypes.Get(step.StepTypeKey).CreatePlan(edited.Geometry, step.Parameters))
                .ToArray();

            BuildMatrix(edited);
            UpdateMatrixState(this.currentStepOrder);
            MatrixMessage = Localizer["Auto_MatrixSaved"];
            Interaction.Say(MatrixMessage);
        }
        catch (GatewayException ex)
        {
            Alarms.RaiseException(ex);
            MatrixMessage = Localizer["Auto_MatrixWriteFailed"];
        }
    }

    /// <summary>丢掉没下发的改动，回到机床里那一份。</summary>
    [RelayCommand]
    private void DiscardMatrixEdits()
    {
        if (this.activeJob is not null)
        {
            BuildMatrix(this.activeJob);
            UpdateMatrixState(this.currentStepOrder);
        }

        MatrixMessage = string.Empty;
    }

    private string Describe(StepUpdateResult result) => result.Refusal switch
    {
        StepUpdateRefusal.NothingChanged => Localizer["Auto_MatrixNothingChanged"],
        StepUpdateRefusal.NotJustParameters => Localizer["Auto_MatrixNotJustParameters"],
        StepUpdateRefusal.StepAlreadyDone => Localizer["Auto_MatrixStepDone"],
        StepUpdateRefusal.NotLiveEditable => Localizer.Format(
            "Auto_MatrixNotLiveEditable",
            string.Join("、", result.BlockedParameterKeys.Select(key => Localizer["Parameter_" + key]))),
        StepUpdateRefusal.Invalid => Localizer.Format(
            "Auto_MatrixInvalid",
            string.Join("、", result.Violations.Select(violation => Localizer["Parameter_" + violation.ParameterKey]))),
        _ => string.Empty,
    };

    /// <summary>把矩阵里的文本收成一份改过的作业；有格子填得不成立就返回 null。</summary>
    private GrindingJob? CollectEditedJob()
    {
        GrindingJob job = this.activeJob!;
        var steps = new List<GrindingJobStep>(job.Steps.Count);

        foreach (GrindingJobStep step in job.Steps)
        {
            Core.Parameters.ParameterSet parameters = step.Parameters;
            IGrindingStepType stepType = this.stepTypes.Get(step.StepTypeKey);

            foreach (MatrixRowViewModel row in MatrixRows)
            {
                MatrixCellViewModel? cell = row.Cells.FirstOrDefault(candidate => candidate.StepOrder == step.Order);
                if (cell is null || !cell.IsApplicable || !cell.IsModified)
                {
                    continue;
                }

                Core.Parameters.ParameterDescriptor? descriptor = stepType.Schema.Descriptors
                    .FirstOrDefault(candidate => string.Equals(candidate.Key, cell.ParameterKey, StringComparison.Ordinal));
                if (descriptor is null)
                {
                    continue;
                }

                Core.Parameters.ParameterValue? value = Parse(descriptor, cell.Text);
                if (value is null)
                {
                    return null;
                }

                parameters = parameters.With(cell.ParameterKey, value);
            }

            steps.Add(step with { Parameters = parameters });
        }

        return job with { Steps = steps };
    }

    private static Core.Parameters.ParameterValue? Parse(
        Core.Parameters.ParameterDescriptor descriptor,
        string text)
    {
        switch (descriptor.Kind)
        {
            case Core.Parameters.ParameterValueKind.Number:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed)
                       || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                    ? Core.Parameters.ParameterValue.FromNumber(parsed)
                    : null;

            default:
                // 开关与选项在矩阵里是只读的：一排分段按钮塞不进一个格子，
                // 要改去工序编程页改。
                return null;
        }
    }

    /// <summary>
    /// 曲线数据：横坐标恒为辊身坐标（mm），纵坐标的单位随曲线变
    /// （辊形三条是直径量 µm，电流是 A），由 <see cref="CurveYAxisLabel"/> 标出来。
    /// </summary>
    public IReadOnlyList<(double BodyPositionMm, double Value)> CurvePoints { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>曲线有更新。</summary>
    public event EventHandler? CurveChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurveTitle))]
    private CurveKind selectedCurve = CurveKind.Error;

    /// <summary>曲线窗标题：现在看的是哪一条（由竖键 1–5 选）。</summary>
    public string CurveTitle => Localizer["Curve_" + SelectedCurve];

    /// <summary>砂轮当前 Z 位置（辊身坐标，mm）；读不到为 null。误差曲线上画成橙色竖线（最终稿 D4）。</summary>
    public double? WheelPositionMm { get; private set; }

    /// <summary>砂轮位置挪了（超过 1 mm 才报，免得每拍重画）。</summary>
    public event EventHandler? WheelPositionChanged;

    private void UpdateWheelPosition(MachineStateSnapshot snapshot)
    {
        double? z = null;
        foreach (AxisDescription axis in this.machine.Axes)
        {
            if (axis.IsPresent && string.Equals(axis.Role, MachineAxisRoles.Carriage, StringComparison.Ordinal))
            {
                z = snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(axis.Name));
                break;
            }
        }

        if (z is null == WheelPositionMm is null && (z is null || Math.Abs(z.Value - WheelPositionMm!.Value) < 1.0))
        {
            return;
        }

        WheelPositionMm = z;
        WheelPositionChanged?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty]
    private string measuredDiameterText = "--";

    [ObservableProperty]
    private string targetDiameterText = "--";

    [ObservableProperty]
    private string remainingStockText = "--";

    [ObservableProperty]
    private string toleranceText = string.Empty;

    [ObservableProperty]
    private string rmsText = "--";

    [ObservableProperty]
    private string progressText = "--";

    [ObservableProperty]
    private string remainingTimeText = "--";

    [ObservableProperty]
    private double progressFraction;

    [ObservableProperty]
    private string feedForwardText = "--";

    [ObservableProperty]
    private string strokeVersionText = "--";

    [ObservableProperty]
    private string realtimeOffsetText = "--";

    [ObservableProperty]
    private bool curveHasData;

    /// <summary>曲线没有数据时给出的原因（例如通道未配置）。</summary>
    [ObservableProperty]
    private string curveEmptyText = string.Empty;

    /// <summary>
    /// 纵轴标题，随曲线切换。五条线里三条是 µm、一条是 A——
    /// 不标出来的话，换条线看数量级会当成同一个东西。
    /// </summary>
    [ObservableProperty]
    private string curveYAxisLabel = string.Empty;

    /// <summary>画不画公差带。只有误差曲线有"合格范围"可言。</summary>
    [ObservableProperty]
    private bool curveShowsTolerance;

    /// <summary>公差带的半宽（µm，直径量）。</summary>
    public double ToleranceMicrometer => this.calibration.Current.ProfileToleranceMicrometer;

    public override void OnActivated()
    {
        _ = RunGuardedAsync(LoadActiveJobAsync, CancellationToken.None);
    }

    public override void OnTick(DateTimeOffset nowUtc)
    {
        MachineStateSnapshot snapshot = this.monitor.Current;

        this.probeA.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeAMm), "F4", showSign: true);
        this.probeB.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeBMm), "F4", showSign: true);

        double? probeAValue = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeAMm);
        double? probeBValue = snapshot.GetNumberOrNull(MachineTagKeys.MeasureProbeBMm);
        this.centringDeviation.ValueText = probeAValue is null || probeBValue is null
            ? "--"
            : FormatOrDash((probeAValue.Value - probeBValue.Value) / 2.0, "F4", showSign: true);

        StatusBand.Update(snapshot);
        this.wheelDiameter.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.WheelDiameterMm), "F2");
        this.grindingCurrent.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.GrindingCurrentA), "F1");

        double? pass = snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentPass);
        double? totalPasses = snapshot.GetNumberOrNull(MachineTagKeys.JobTotalPasses);
        this.currentPass.ValueText = pass is null || totalPasses is null
            ? "--"
            : string.Create(CultureInfo.InvariantCulture, $"{(int)pass.Value} / {(int)totalPasses.Value}");

        UpdateDiameters(snapshot);
        UpdateSequence(snapshot);
        UpdateBlocks(snapshot);

        // 循环正常结束的上升沿：等收尾写完，弹结果横幅。
        bool complete = snapshot.GetNumberOrNull(MachineTagKeys.JobCycleComplete) is double flag && flag > 0.5;
        if (complete && !this.lastCycleComplete && this.activeJob is { } finished)
        {
            _ = RunGuardedAsync(token => ShowFinishAsync(finished.JobId, token), CancellationToken.None);
        }

        this.lastCycleComplete = complete;
        UpdateCompensation(snapshot);
        TickCompensation();
        UpdateStepFlow();
        UpdateWheelPosition(snapshot);
        RefreshAfterNewMeasurement();
    }

    /// <summary>
    /// 测量工序走完（或手动采点）后台存下了一次测量：RMS 跟着换，正在看误差曲线的话曲线也换。
    /// 不然一支辊磨完、量完，屏幕上还是开磨前的样子，要人点一下曲线键才更新。
    /// </summary>
    private void RefreshAfterNewMeasurement()
    {
        long version = this.measurementNotifications.Version;
        if (version == this.seenMeasurementVersion)
        {
            return;
        }

        this.seenMeasurementVersion = version;
        if (this.activeJob is not null)
        {
            _ = RunGuardedAsync(
                SelectedCurve == CurveKind.Error ? RefreshCurveAsync : RefreshRmsAsync,
                CancellationToken.None);
        }
    }

    /// <summary>
    /// 每一拍刷新流程键能不能按：全按机床的当前快照算，不缓存判断——工序一变，键跟着变。
    /// 按不了的留在原位变灰，点它时对话行说原因（服务给的那句）。
    /// </summary>
    private void UpdateStepFlow()
    {
        Block(this.endEarlyKey, FlowBlocker(this.activeJob is null
            ? null
            : this.stepFlow.CanEndStepEarly(this.activeJob)));
        Block(this.jumpKey, FlowBlocker(this.activeJob is null
            ? null
            : UpcomingSteps().Any()
                ? StepFlowResult.Sent
                : this.stepFlow.CanJumpTo(this.activeJob, CurrentStepOrder() + 1)));
        this.coolantKey.IsActive = this.monitor.Current.GetNumberOrNull(MachineTagKeys.ManualCommandState(CoolantKey)) is { } on && on != 0;
    }

    private string? FlowBlocker(StepFlowResult? result) => result switch
    {
        null => Localizer["Auto_NoActiveJob"],
        { Succeeded: true } => null,
        { MessageResourceKey: { } key } => Localizer[key],
        _ => Localizer["Key_NotNow"],
    };

    /// <summary>还能跳过去的工序（最多 7 道，竖键子菜单放得下）。</summary>
    private IEnumerable<SequenceRowViewModel> UpcomingSteps() => this.activeJob is null
        ? Enumerable.Empty<SequenceRowViewModel>()
        : Sequence.Where(row => this.stepFlow.CanJumpTo(this.activeJob, row.Order).Succeeded)
            .Take(SoftKeyMenu<FunctionKeyViewModel>.SubMenuCapacity);

    /// <summary>"工序跳转…"：竖键列出后面能跳到的工序，选一道再确认（取代工序序列里每行一个的"跳到此工序"）。</summary>
    private void OpenJumpMenu() => OpenVerticalMenu(
        "Vk_JumpTitle",
        UpcomingSteps().Select(row => MenuChoice(
            "Vk_JumpToFormat",
            () => _ = RequestFlowAsync(StepFlowAction.Jump, row.Order, CancellationToken.None),
            requiresEditable: false,
            labelArgument: row.OrderText + " " + row.DisplayName)));

    /// <summary>当前工序提前结束，进入下一道。</summary>
    [RelayCommand]
    private Task EndStepEarlyAsync(CancellationToken cancellationToken) =>
        RequestFlowAsync(StepFlowAction.EndEarly, 0, cancellationToken);

    /// <summary>
    /// 流程动作（跳转、提前结束、循环启动）：先问服务能不能做，能做才在对话行提问，
    /// 竖键 7 / 8 取消 / 确认，5 秒不答作废（最终稿 D5，取代"再按一次确认"）。
    /// </summary>
    private async Task RequestFlowAsync(StepFlowAction action, int targetOrder, CancellationToken cancellationToken)
    {
        if (this.activeJob is null)
        {
            Interaction.Refuse(Localizer["Auto_NoActiveJob"]);
            return;
        }

        // 循环启动是个例外——想启动的时候本来就还没在跑，没有"当前工序"可查。
        StepFlowResult permission = action switch
        {
            StepFlowAction.Jump => this.stepFlow.CanJumpTo(this.activeJob, targetOrder),
            StepFlowAction.EndEarly => this.stepFlow.CanEndStepEarly(this.activeJob),
            _ => this.stepFlow.CanRequestCycleControl
                ? StepFlowResult.Sent
                : StepFlowResult.Refused(
                    StepFlowRefusal.NotMapped, StepFlowControlService.NotMappedResourceKey),
        };
        if (!permission.Succeeded)
        {
            Interaction.Refuse(Localizer[permission.MessageResourceKey!]);
            return;
        }

        string question = action switch
        {
            StepFlowAction.Jump => Localizer.Format(
                "Auto_AskJump",
                Sequence.FirstOrDefault(row => row.Order == targetOrder) is { } row ? row.OrderText + " " + row.DisplayName : targetOrder.ToString(CultureInfo.CurrentCulture)),
            StepFlowAction.EndEarly => Localizer["Auto_AskEndEarly"],
            _ => Localizer["Auto_AskCycleStart"],
        };

        Interaction.Ask(question, () => SendFlowAsync(action, targetOrder, cancellationToken));
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private Task SendFlowAsync(StepFlowAction action, int targetOrder, CancellationToken cancellationToken)
    {
        string requestedBy = this.userSession.CurrentUser?.UserName ?? string.Empty;
        return RunGuardedAsync(async token =>
        {
            StepFlowResult result = action switch
            {
                StepFlowAction.Jump => await this.stepFlow
                    .JumpToStepAsync(this.activeJob!, targetOrder, requestedBy, token).ConfigureAwait(true),
                StepFlowAction.EndEarly => await this.stepFlow
                    .EndStepEarlyAsync(this.activeJob!, requestedBy, token).ConfigureAwait(true),
                _ => await this.stepFlow
                    .RequestCycleStartAsync(requestedBy, token).ConfigureAwait(true),
            };

            if (result.Succeeded)
            {
                Say("Common_Sent");
            }
            else
            {
                Alarms.Raise(
                    AlarmSeverity.Warning, result.MessageResourceKey!, detail: null, code: AlarmCodes.Unspecified);
            }
        }, cancellationToken);
    }

    private int CurrentStepOrder() =>
        (int)(this.monitor.Current.GetNumberOrNull(MachineTagKeys.JobCurrentStepOrder) ?? 0);

    /// <summary>
    /// 请 NC 启动循环（只在按钮板没装循环启动的现场出现）。会让机床动起来，要确认。
    /// </summary>
    [RelayCommand]
    private Task RequestCycleStartAsync(CancellationToken cancellationToken) =>
        RequestFlowAsync(StepFlowAction.CycleStart, 0, cancellationToken);

    /// <summary>请 NC 进给保持（只在按钮板没装暂停的现场出现）。往安全那一侧走，不问。</summary>
    [RelayCommand]
    private Task RequestFeedHoldAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            StepFlowResult result = await this.stepFlow
                .RequestFeedHoldAsync(this.userSession.CurrentUser?.UserName ?? string.Empty, token)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                Alarms.Raise(
                    AlarmSeverity.Warning, result.MessageResourceKey!, detail: null, code: AlarmCodes.Unspecified);
            }
        }, cancellationToken);

    /// <summary>
    /// 冷却水开关。就是手动页那一个动作，换个地方按——
    /// 磨着磨着要开关冷却水，不该为此切到手动页去。
    /// </summary>
    [RelayCommand]
    private Task ToggleCoolantAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            ManualCommandDescriptor coolant = ManualCommandCatalog.All
                .Single(command => string.Equals(command.Key, CoolantKey, StringComparison.Ordinal));

            ManualCommandResult result = await this.manualCommands
                .ExecuteAsync(coolant, desiredState: null, token).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                Alarms.Raise(
                    AlarmSeverity.Warning, result.ReasonResourceKey!, detail: null, code: AlarmCodes.Unspecified);
            }
        }, cancellationToken);

    /// <summary>冷却水那个动作的键。手动目录里就叫这个。</summary>
    private const string CoolantKey = "coolant";

    [RelayCommand]
    private void SelectCurve(CurveKind kind)
    {
        SelectedCurve = kind;
        MarkSelectedCurve();
        _ = RunGuardedAsync(RefreshCurveAsync, CancellationToken.None);
    }

    private void UpdateDiameters(MachineStateSnapshot snapshot)
    {
        double? measuredDiameterMm = snapshot.GetNumberOrNull(MachineTagKeys.MeasuredDiameterMm);
        MeasuredDiameterText = FormatOrDash(measuredDiameterMm, "F3");

        if (this.activeJob is null)
        {
            TargetDiameterText = "--";
            RemainingStockText = "--";
            return;
        }

        double targetDiameterMm = this.activeJob.Geometry.NominalDiameterMm;
        TargetDiameterText = targetDiameterMm.ToString("F3", CultureInfo.CurrentCulture);
        RemainingStockText = measuredDiameterMm is null
            ? "--"
            : (measuredDiameterMm.Value - targetDiameterMm).ToString("F3", CultureInfo.CurrentCulture);
    }

    private void UpdateSequence(MachineStateSnapshot snapshot)
    {
        if (Sequence.Count == 0)
        {
            return;
        }

        int currentOrder = (int)(snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentStepOrder) ?? 0);
        double? pass = snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentPass);
        double? totalPasses = snapshot.GetNumberOrNull(MachineTagKeys.JobTotalPasses);

        foreach (SequenceRowViewModel row in Sequence)
        {
            row.State = StateOf(row.Order, currentOrder);

            row.PassText = row.State == StepRowState.Current && pass is not null && totalPasses is not null
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)pass.Value}/{(int)totalPasses.Value}")
                : string.Empty;
        }

        bool stepChanged = this.currentStepOrder != currentOrder;
        this.currentStepOrder = currentOrder;
        UpdateMatrixState(currentOrder);
        UpdateProgress(currentOrder, pass, totalPasses);

        // 参数区跟着走：换了一道就看新的那一道；人点开看别的那一道，留到下一次换道为止。
        if (stepChanged || FocusedStep is null)
        {
            FocusOn(currentOrder);
        }
        else
        {
            RefreshFocusTitle();
        }
    }

    /// <summary>
    /// 把"哪一道在跑、下一道是哪个"同步到矩阵的列头与每一格上，
    /// 与左侧工序序列用的是同一条判断，不会出现两处说法不一致。
    /// </summary>
    private void UpdateMatrixState(int currentOrder)
    {
        if (MatrixColumns.Count == 0)
        {
            return;
        }

        foreach (MatrixColumnViewModel column in MatrixColumns)
        {
            column.State = StateOf(column.Order, currentOrder);
        }

        foreach (MatrixRowViewModel row in MatrixRows)
        {
            foreach (MatrixCellViewModel cell in row.Cells)
            {
                cell.State = StateOf(cell.StepOrder, currentOrder);
            }
        }
    }

    private static StepRowState StateOf(int order, int currentOrder) =>
        order < currentOrder ? StepRowState.Done
        : order == currentOrder ? StepRowState.Current
        : order == currentOrder + 1 ? StepRowState.Next
        : StepRowState.Pending;

    private void UpdateProgress(int currentOrder, double? pass, double? totalPasses)
    {
        if (Sequence.Count == 0 || currentOrder <= 0)
        {
            ProgressFraction = 0.0;
            ProgressText = "--";
            RemainingTimeText = "--";
            return;
        }

        double withinStep = pass is not null && totalPasses is > 0 ? pass.Value / totalPasses.Value : 0.0;
        double completed = Math.Min(currentOrder - 1 + withinStep, Sequence.Count);
        ProgressFraction = completed / Sequence.Count;
        ProgressText = (ProgressFraction * 100.0).ToString("F0", CultureInfo.CurrentCulture) + "%";

        if (this.activeJob is null || this.activePlans.Count == 0)
        {
            RemainingTimeText = "--";
            return;
        }

        TimeSpan remaining = TimeSpan.Zero;
        for (int i = currentOrder - 1; i < this.activePlans.Count; i++)
        {
            TimeSpan stepDuration = this.activePlans[i].EstimateDuration(this.activeJob.Geometry);
            remaining += i == currentOrder - 1 ? stepDuration * (1.0 - withinStep) : stepDuration;
        }

        RemainingTimeText = remaining.TotalHours >= 1.0
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}")
            : string.Create(CultureInfo.InvariantCulture, $"00:{(int)remaining.TotalMinutes:00}");
    }

    private void UpdateCompensation(MachineStateSnapshot snapshot)
    {
        double? feedForwardA = snapshot.GetNumberOrNull(MachineTagKeys.CompensationFeedForwardA);
        double? feedForwardB = snapshot.GetNumberOrNull(MachineTagKeys.CompensationFeedForwardB);
        FeedForwardText = feedForwardA is null || feedForwardB is null
            ? "--"
            : string.Create(CultureInfo.InvariantCulture, $"a {feedForwardA.Value:F3} / b {feedForwardB.Value:0.0e+0}");

        double? version = snapshot.GetNumberOrNull(MachineTagKeys.CompensationStrokeVersion);
        StrokeVersionText = version is null ? "--" : "v " + ((int)version.Value).ToString(CultureInfo.InvariantCulture);

        RealtimeOffsetText = FormatOrDash(
            snapshot.GetNumberOrNull(MachineTagKeys.CompensationRealtimeOffsetMm), "F4", showSign: true);
    }

    private async Task LoadActiveJobAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GrindingRecord> recent = await this.records
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), 1, cancellationToken)
            .ConfigureAwait(true);

        Sequence.Clear();
        this.activeJob = null;

        // 换了一支辊（新作业）：参数区回到"看一道"，不沿用上一支辊时切到的总表。
        IsMatrixOverview = false;
        MatrixRows.Clear();
        MatrixColumns.Clear();
        HasMatrix = false;
        this.activePlans = Array.Empty<GrindingStepPlan>();

        if (recent.Count == 0)
        {
            CurveHasData = false;
            CurveEmptyText = Localizer["Auto_NoActiveJob"];
            return;
        }

        (GrindingJob Job, JobState State)? stored = await this.jobs
            .GetAsync(recent[0].JobId, cancellationToken).ConfigureAwait(true);
        if (stored is null)
        {
            return;
        }

        this.activeJob = stored.Value.Job;
        this.activePlans = this.activeJob.Steps
            .Select(step => this.stepTypes.Get(step.StepTypeKey).CreatePlan(this.activeJob.Geometry, step.Parameters))
            .ToArray();

        for (int i = 0; i < this.activeJob.Steps.Count; i++)
        {
            GrindingJobStep step = this.activeJob.Steps[i];
            TimeSpan duration = this.activePlans[i].EstimateDuration(this.activeJob.Geometry);
            Sequence.Add(new SequenceRowViewModel(
                step.Order,
                Localizer["StepType_" + step.StepTypeKey],
                duration > TimeSpan.Zero
                    ? Localizer.Format("Auto_StepDurationFormat", (int)duration.TotalMinutes)
                    : string.Empty));
        }

        // 换了一支辊：之前那支收来的圆度/偏心/电流轨迹与这支无关，清掉重收。
        this.traces.Reset(this.activeJob.Geometry.BodyLengthMm);

        BuildMatrix(this.activeJob);
        await RefreshCurveAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 按当前作业摊出参数矩阵。投影本身在 <see cref="StepParameterMatrix"/> 里，
    /// 这里只负责把取值变成显示文本。
    /// </summary>
    private void BuildMatrix(GrindingJob job)
    {
        MatrixColumns.Clear();
        MatrixRows.Clear();

        StepParameterMatrix matrix = StepParameterMatrix.Build(job, this.stepTypes);

        foreach (GrindingJobStep step in matrix.Steps)
        {
            MatrixColumns.Add(new MatrixColumnViewModel(step.Order, Localizer["StepType_" + step.StepTypeKey]));
        }

        foreach (StepMatrixRow row in matrix.Rows)
        {
            MatrixRows.Add(new MatrixRowViewModel(
                LabelOf(row.Descriptor),
                UnitOf(row.Descriptor),
                row.Cells
                    .Select(cell =>
                    {
                        var vm = new MatrixCellViewModel(
                            cell.StepOrder,
                            row.Descriptor.Key,
                            Format(row.Descriptor, cell.Value),
                            cell.IsApplicable,
                            row.Descriptor.IsLiveEditable);
                        vm.PropertyChanged += (_, e) =>
                        {
                            if (e.PropertyName == nameof(MatrixCellViewModel.IsModified))
                            {
                                RefreshMatrixDirty();
                            }
                        };
                        return vm;
                    })
                    .ToArray()));
        }

        HasMatrix = MatrixRows.Count > 0;
        RefreshMatrixDirty();

        // 矩阵重建（换作业、下发改动、放弃改动）：格子是新对象，参数区也要重新取。
        FocusOn(FocusedStep?.Order ?? this.currentStepOrder);
    }

    /// <summary>参数区现在看的那一道。默认跟着正在跑的那一道；点左边工序序列的某一道就看那一道。</summary>
    [ObservableProperty]
    private SequenceRowViewModel? focusedStep;

    /// <summary>参数区显示全部工序的总表（横键"参数总表"切换）；默认只看一道，参数一个不落、不用横着滚。</summary>
    [ObservableProperty]
    private bool isMatrixOverview;

    /// <summary>参数区标题，例如"03 粗磨 · 正在磨 · 4/10"。</summary>
    [ObservableProperty]
    private string focusTitle = string.Empty;

    /// <summary>参数区底部一行：下一道是什么、大约多久。</summary>
    [ObservableProperty]
    private string nextStepText = string.Empty;

    /// <summary>看的那一道的全部参数。</summary>
    public ObservableCollection<FocusCellViewModel> FocusCells { get; } = new();

    partial void OnIsMatrixOverviewChanged(bool value) => this.parameterTableKey.IsActive = value;

    partial void OnFocusedStepChanged(SequenceRowViewModel? oldValue, SequenceRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsFocused = false;
        }

        if (newValue is not null)
        {
            newValue.IsFocused = true;
        }

        RebuildFocus();
    }

    /// <summary>点左边工序序列的一行：参数区改看那一道（看下一道、提前改它的参数）。</summary>
    [RelayCommand]
    private void FocusStep(SequenceRowViewModel? row)
    {
        if (row is not null)
        {
            FocusedStep = row;
            IsMatrixOverview = false;
        }
    }

    private void FocusOn(int order)
    {
        SequenceRowViewModel? row = Sequence.FirstOrDefault(candidate => candidate.Order == order) ?? Sequence.FirstOrDefault();
        if (ReferenceEquals(row, FocusedStep))
        {
            RebuildFocus();
        }
        else
        {
            FocusedStep = row;
        }
    }

    private void RebuildFocus()
    {
        FocusCells.Clear();
        if (FocusedStep is { } step)
        {
            foreach (MatrixRowViewModel row in MatrixRows)
            {
                if (row.Cells.FirstOrDefault(cell => cell.StepOrder == step.Order && cell.IsApplicable) is { } cell)
                {
                    FocusCells.Add(new FocusCellViewModel(row.Label, row.UnitText, cell));
                }
            }
        }

        RefreshFocusTitle();
    }

    private void RefreshFocusTitle()
    {
        if (FocusedStep is not { } step)
        {
            FocusTitle = string.Empty;
            NextStepText = string.Empty;
            return;
        }

        FocusTitle = string.Join(
            " · ",
            new[] { step.OrderText + " " + step.DisplayName, Localizer["Auto_FocusState_" + step.State], step.PassText }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        SequenceRowViewModel? next = Sequence.FirstOrDefault(candidate => candidate.Order == step.Order + 1);
        NextStepText = next is null
            ? Localizer["Auto_FocusLastStep"]
            : Localizer.Format("Auto_FocusNextFormat", next.OrderText, next.DisplayName, next.DurationText);
    }

    private string LabelOf(Core.Parameters.ParameterDescriptor descriptor)
    {
        string localized = Localizer[descriptor.ResourceKey];
        return localized.StartsWith('!') ? descriptor.Key : localized;
    }

    private string UnitOf(Core.Parameters.ParameterDescriptor descriptor) =>
        descriptor.Unit == ParameterUnit.None ? string.Empty : Localizer["Unit_" + descriptor.Unit];

    /// <summary>
    /// 一格的显示文本。留空的含义是"这类工序没有这个参数"，
    /// 与"值是 0"不是一回事，所以 null 才返回空串。
    /// </summary>
    private string Format(Core.Parameters.ParameterDescriptor descriptor, Core.Parameters.ParameterValue? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value.Kind switch
        {
            Core.Parameters.ParameterValueKind.Number =>
                value.Number.ToString("0.###", CultureInfo.CurrentCulture),
            Core.Parameters.ParameterValueKind.Boolean =>
                Localizer[value.Boolean ? "Common_On" : "Common_Off"],
            Core.Parameters.ParameterValueKind.Choice =>
                Localizer[descriptor.ChoiceResourceKey(value.Choice)],
            _ => value.ToInvariantString(),
        };
    }

    private async Task RefreshCurveAsync(CancellationToken cancellationToken)
    {
        CurvePoints = Array.Empty<(double, double)>();
        CurveHasData = false;
        CurveEmptyText = string.Empty;
        CurveShowsTolerance = SelectedCurve == CurveKind.Error;
        CurveYAxisLabel = Localizer[SelectedCurve == CurveKind.GrindingCurrent
            ? "Unit_Ampere"
            : "Unit_Micrometer"];

        if (this.activeJob is null)
        {
            RmsText = "--";
            CurveEmptyText = Localizer["Auto_NoActiveJob"];
            CurveChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // RMS 是这支辊的状态量，不随看哪条曲线变：切到电流曲线时也照样显示。
        await RefreshRmsAsync(cancellationToken).ConfigureAwait(true);

        switch (SelectedCurve)
        {
            case CurveKind.Reference:
                BuildReferenceCurve();
                break;

            case CurveKind.Error:
                await BuildErrorCurveAsync(cancellationToken).ConfigureAwait(true);
                break;

            case CurveKind.Roundness:
                BuildTraceCurve(SurfaceTraceKind.Roundness);
                break;

            case CurveKind.Eccentricity:
                BuildTraceCurve(SurfaceTraceKind.Eccentricity);
                break;

            case CurveKind.GrindingCurrent:
                BuildTraceCurve(SurfaceTraceKind.GrindingCurrent);
                break;

            default:
                CurveEmptyText = Localizer["Auto_CurveChannelNotConfigured"];
                break;
        }

        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 参考曲线 = 把作业里各段辊形合成之后的整条目标曲线。
    /// 端部锥度与倒角也画在里面——只画主辊形，两端就跟实测对不上。
    /// </summary>
    private RollProfile TargetProfile() => this.activeJob!.Profile.Compose(
        this.activeJob.Geometry, this.profileTypes, this.settings.ProfileSampleCount);

    /// <summary>公差是现场标定值，设置页上改完这里跟着变。</summary>
    private void RefreshTolerance() => ToleranceText = Localizer.Format(
        "Auto_ToleranceFormat", this.calibration.Current.ProfileToleranceMicrometer);

    private void BuildReferenceCurve()
    {
        RollProfile target = TargetProfile();

        CurvePoints = target.Points
            .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();
        CurveHasData = true;
    }

    private async Task BuildErrorCurveAsync(CancellationToken cancellationToken)
    {
        RollProfile? deviation = await LatestDeviationAsync(cancellationToken).ConfigureAwait(true);
        if (deviation is null)
        {
            CurveEmptyText = Localizer["Auto_NoMeasurementYet"];
            return;
        }

        CurvePoints = deviation.Points
            .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();
        CurveHasData = CurvePoints.Count > 0;
    }

    /// <summary>最近一次测量相对目标辊形的均方根（直径量 µm）；还没量过显示"--"。</summary>
    private async Task RefreshRmsAsync(CancellationToken cancellationToken)
    {
        RollProfile? deviation = this.activeJob is null
            ? null
            : await LatestDeviationAsync(cancellationToken).ConfigureAwait(true);
        if (deviation is null || deviation.Points.Count == 0)
        {
            RmsText = "--";
            return;
        }

        double sumOfSquares = deviation.Points.Sum(point =>
        {
            double micrometer = UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm);
            return micrometer * micrometer;
        });
        RmsText = Localizer.Format("Auto_RmsFormat", Math.Sqrt(sumOfSquares / deviation.Points.Count));
    }

    /// <summary>这支辊最近一次测量减目标辊形；还没量过为 null。</summary>
    private async Task<RollProfile?> LatestDeviationAsync(CancellationToken cancellationToken)
    {
        GrindingJob job = this.activeJob!;
        MeasurementRecord? measurement = await this.measurements
            .GetLatestByJobAsync(job.JobId, cancellationToken).ConfigureAwait(true);
        return measurement is null
            ? null
            : CompensationCalculator.ComputeDeviation(measurement.Profile, TargetProfile(), job.Geometry);
    }

    /// <summary>
    /// 沿辊身收来的一条轨迹（圆度 / 偏心 / 电流）。
    ///
    /// 这三条线上位机不参与计算：数是测量系统与驱动报上来的，
    /// 这里只是把"拖板走到哪、报了多少"按位置摆出来。tagmap 里没登记
    /// 就如实说通道未配置；登记了但还没走过就说还没有数据——
    /// 两种情况对现场是两回事，不能混成一句"没有曲线"。
    /// </summary>
    private void BuildTraceCurve(SurfaceTraceKind kind)
    {
        if (!this.traces.IsAvailable(kind))
        {
            CurveEmptyText = Localizer["Auto_CurveChannelNotConfigured"];
            return;
        }

        IReadOnlyList<SurfaceTracePoint> trace = this.traces.Trace(kind);
        if (trace.Count < 2)
        {
            CurveEmptyText = Localizer["Auto_CurveNoTraceYet"];
            return;
        }

        CurvePoints = trace.Select(point => (point.BodyPositionMm, point.Value)).ToArray();
        CurveHasData = true;
    }

    private static string FormatOrDash(double? value, string format, bool showSign = false)
    {
        if (value is null)
        {
            return "--";
        }

        string text = value.Value.ToString(format, CultureInfo.CurrentCulture);
        return showSign && value.Value >= 0.0 ? "+" + text : text;
    }
}
