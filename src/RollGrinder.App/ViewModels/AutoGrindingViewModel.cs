using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
using RollGrinder.Services.Calibration;
using RollGrinder.Services.Jobs;
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
    private readonly IUserSession userSession;

    private readonly LiveValueViewModel probeA;
    private readonly LiveValueViewModel probeB;
    private readonly LiveValueViewModel centringDeviation;
    private readonly LiveValueViewModel xPosition;
    private readonly LiveValueViewModel zPosition;
    private readonly LiveValueViewModel wheelDiameter;
    private readonly LiveValueViewModel grindingCurrent;
    private readonly LiveValueViewModel currentPass;

    private GrindingJob? activeJob;
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
        IUserSession userSession,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.stepUpdates = stepUpdates ?? throw new ArgumentNullException(nameof(stepUpdates));
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
        this.xPosition = new LiveValueViewModel("Live_XPosition", localizer);
        this.zPosition = new LiveValueViewModel("Live_ZPosition", localizer);
        this.wheelDiameter = new LiveValueViewModel("Live_WheelDiameter", localizer);
        this.grindingCurrent = new LiveValueViewModel("Live_GrindingCurrent", localizer);
        this.currentPass = new LiveValueViewModel("Live_CurrentPass", localizer);

        LiveValues = new ObservableCollection<LiveValueViewModel>
        {
            this.probeA, this.probeB, this.centringDeviation,
            this.xPosition, this.zPosition,
            this.wheelDiameter, this.grindingCurrent, this.currentPass,
        };

        RefreshTolerance();
        calibration.Changed += (_, _) => RefreshTolerance();

        SetFunctionKeys(new[]
        {
            FunctionKeyViewModel.Placeholder("Fn_Start", localizer, () => NotImplementedYet("Fn_Start"), FunctionKeyKind.Start),
            FunctionKeyViewModel.Placeholder("Fn_Pause", localizer, () => NotImplementedYet("Fn_Pause")),
            FunctionKeyViewModel.Placeholder("Fn_SkipStep", localizer, () => NotImplementedYet("Fn_SkipStep")),
            FunctionKeyViewModel.Placeholder("Fn_EndEarly", localizer, () => NotImplementedYet("Fn_EndEarly")),
            FunctionKeyViewModel.Placeholder("Fn_Coolant", localizer, () => NotImplementedYet("Fn_Coolant")),
            // 补偿设置住在工序编程页：派过去，导航槽会显示"返回 自动磨削"。
            FunctionKeyViewModel.Placeholder(
                "Fn_CompensationSettings", localizer, () => Navigator.StartTask(PageKey.Steps, PageKey.AutoGrinding)),
            FunctionKeyViewModel.Placeholder("Fn_Records", localizer, () => Navigator.GoToArea(PageKey.Records)),
        });
    }

    public override PageKey Key => PageKey.AutoGrinding;

    public override string TitleResourceKey => "Page_AutoGrinding";

    public override string MenuHintResourceKey => "Menu_AutoGrindingHint";

    /// <summary>左栏：工序序列。</summary>
    public ObservableCollection<SequenceRowViewModel> Sequence { get; } = new();

    /// <summary>右栏：实时数据。</summary>
    public ObservableCollection<LiveValueViewModel> LiveValues { get; }

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
                return;
            }

            this.activeJob = edited;
            this.activePlans = edited.Steps
                .Select(step => this.stepTypes.Get(step.StepTypeKey).CreatePlan(edited.Geometry, step.Parameters))
                .ToArray();

            BuildMatrix(edited);
            UpdateMatrixState(this.currentStepOrder);
            MatrixMessage = Localizer["Auto_MatrixSaved"];
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

    /// <summary>曲线数据（辊身坐标 mm，直径量 µm）。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> CurvePoints { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>曲线有更新。</summary>
    public event EventHandler? CurveChanged;

    [ObservableProperty]
    private CurveKind selectedCurve = CurveKind.Error;

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

        this.xPosition.ValueText = FormatOrDash(AxisPosition(snapshot, MachineAxisRoles.InfeedRadius), "F3", showSign: true);
        this.zPosition.ValueText = FormatOrDash(AxisPosition(snapshot, MachineAxisRoles.Carriage), "F2");
        this.wheelDiameter.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.WheelDiameterMm), "F2");
        this.grindingCurrent.ValueText = FormatOrDash(snapshot.GetNumberOrNull(MachineTagKeys.GrindingCurrentA), "F1");

        double? pass = snapshot.GetNumberOrNull(MachineTagKeys.JobCurrentPass);
        double? totalPasses = snapshot.GetNumberOrNull(MachineTagKeys.JobTotalPasses);
        this.currentPass.ValueText = pass is null || totalPasses is null
            ? "--"
            : string.Create(CultureInfo.InvariantCulture, $"{(int)pass.Value} / {(int)totalPasses.Value}");

        UpdateDiameters(snapshot);
        UpdateSequence(snapshot);
        UpdateCompensation(snapshot);
    }

    [RelayCommand]
    private void SelectCurve(CurveKind kind)
    {
        SelectedCurve = kind;
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

        this.currentStepOrder = currentOrder;
        UpdateMatrixState(currentOrder);
        UpdateProgress(currentOrder, pass, totalPasses);
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

    private double? AxisPosition(MachineStateSnapshot snapshot, string role)
    {
        AxisDescription? axis = this.machine.Axes.FirstOrDefault(candidate =>
            candidate.IsPresent && string.Equals(candidate.Role, role, StringComparison.Ordinal));

        return axis is null ? null : snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(axis.Name));
    }

    private async Task LoadActiveJobAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GrindingRecord> recent = await this.records
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), 1, cancellationToken)
            .ConfigureAwait(true);

        Sequence.Clear();
        this.activeJob = null;
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
        RmsText = "--";

        if (this.activeJob is null)
        {
            CurveEmptyText = Localizer["Auto_NoActiveJob"];
            CurveChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (SelectedCurve)
        {
            case CurveKind.Reference:
                BuildReferenceCurve();
                break;

            case CurveKind.Error:
                await BuildErrorCurveAsync(cancellationToken).ConfigureAwait(true);
                break;

            default:
                // 圆度、偏心度、磨削电流曲线需要机床侧对应的测量通道，
                // machine.json 里没有描述就如实说明，不画一条编出来的线。
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
        MeasurementRecord? measurement = await this.measurements
            .GetLatestByJobAsync(this.activeJob!.JobId, cancellationToken).ConfigureAwait(true);
        if (measurement is null)
        {
            CurveEmptyText = Localizer["Auto_NoMeasurementYet"];
            return;
        }

        RollProfile deviation = CompensationCalculator.ComputeDeviation(
            measurement.Profile, TargetProfile(), this.activeJob.Geometry);

        CurvePoints = deviation.Points
            .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();
        CurveHasData = true;

        double sumOfSquares = CurvePoints.Sum(point => point.DiameterMicrometer * point.DiameterMicrometer);
        double rms = Math.Sqrt(sumOfSquares / CurvePoints.Count);
        RmsText = Localizer.Format("Auto_RmsFormat", rms);
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
