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
using RollGrinder.Core;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Library;
using RollGrinder.Services.Manual;
using RollGrinder.Services.Monitoring;
using RollGrinder.Services.Records;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>待磨清单的一行。</summary>
public sealed record QueueRowViewModel(
    QueueEntry Entry, string MarkText, string KindText, string PurposeText, string PlanText, string RemainingText, string LastText, bool IsPinned)
{
    public string RollId => Entry.Roll.RollId;
}

/// <summary>核对清单的一行：✓ 通过 / ! 提示 / ✕ 不通过。</summary>
public sealed record CheckRowViewModel(string ItemText, string MessageText, JobCheckStatus Status)
{
    public string Glyph => Status switch
    {
        JobCheckStatus.Pass => "✓",
        JobCheckStatus.Notice => "!",
        _ => "✕",
    };

    public bool IsNotice => Status == JobCheckStatus.Notice;

    public bool IsBlock => Status == JobCheckStatus.Block;
}

/// <summary>作业里那支程序的一道工序（名字与按这支辊估的时长）。</summary>
public sealed record JobProgramStepRowViewModel(string OrderText, string Name, string DurationText);

/// <summary>
/// 作业核对页"本次取舍"里的一行：这一支辊开磨前，走不走某段 NC 子程序（或上位机打不打印）。
/// 只列这份作业用得上的（见 <see cref="ProgramOptionPlanner"/>）；挂着工序的注明是第几道。
/// </summary>
public sealed partial class ProgramOptionRowViewModel : ObservableObject
{
    public ProgramOptionRowViewModel(ProgramOptionDescriptor descriptor, bool isOn, int? linkedStepOrder, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(localizer);

        Descriptor = descriptor;
        Label = localizer[descriptor.ResourceKey];
        Note = linkedStepOrder is { } order ? localizer.Format("Job_OptionStepFormat", order) : string.Empty;
        DefaultOn = isOn;
        this.isOn = isOn;
    }

    /// <summary>默认值（挂工序的看程序里有没有那道工序，其余按目录）。</summary>
    public bool DefaultOn { get; }

    /// <summary>这一支辊改了默认：行尾标"本次改"，核对时一眼看得出哪几项和平常不一样。</summary>
    public bool IsChanged => IsOn != DefaultOn;

    public ProgramOptionDescriptor Descriptor { get; }

    public string Label { get; }

    /// <summary>挂着哪道工序（"第 3 道"）；没挂工序为空。</summary>
    public string Note { get; }

    /// <summary>开关状态。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private bool isOn;
}

/// <summary>
/// 作业（界面修订稿 v3 6.3、关系设计第 6 节）：以轧辊为中心——
/// 根画面是待磨清单（中断待续、不合格待返磨置顶，其余按最近下线），输入尾号就能找到那支辊；
/// "下作业 ▸"打开一页核对：辊形与程序直接取台账里的计划，磨前直径、本次磨削量（mm）就地改，
/// 核对清单一处算全部规则（<see cref="JobChecklist"/>），全部不拦时第 7 / 8 格是"✕ 返回 / ✓ 确认下发"。
///
/// 换辊形 / 换程序打开与轧辊区同一个选择子视图；选了与计划不同的，问"仅本次（选原因）/ 改为计划 / 取消"，不超时。
/// 下发成功后按决定写回台账（改为计划），请 NC 切 AUTO，转到自动磨削。
/// </summary>
public sealed partial class JobViewModel : PageViewModelBase
{
    public const string ReviewSubView = "SubView_JobReview";

    private const int QueueLimit = 500;

    private readonly IRollPlanningService planning;
    private readonly IGrindingPlanService plans;
    private readonly ITemplateService templates;
    private readonly IJobAdjustmentService adjust;
    private IReadOnlyList<AdjustableParameter> adjustable = Array.Empty<AdjustableParameter>();
    private IReadOnlyList<JobAdjustment> lastAdjustments = Array.Empty<JobAdjustment>();
    private readonly List<JobAdjustment> adjustments = new();
    private IReadOnlyList<TemplateUpdateNotice> templateNotices = Array.Empty<TemplateUpdateNotice>();
    private readonly IRollLedgerService ledger;
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IJobRepository jobs;
    private readonly IJobDownloadService downloadService;
    private readonly GrindingJobValidator validator;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly MachineCapability capability;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;
    private readonly IUserSession session;
    private readonly JobDraft draft;
    private readonly IManualGrindingService manualGrinding;
    private readonly IMachineMonitor monitor;
    private readonly ITagMap tagMap;
    private readonly FunctionKeyViewModel cancelReviewKey;
    private readonly FunctionKeyViewModel confirmDownloadKey;
    private readonly FunctionKeyViewModel pickKey;
    private readonly FunctionKeyViewModel makePlanKey;
    private readonly AsyncRelayCommand downloadCommand;

    private RollRecord? roll;
    private GrindingPlanRecord? plan;
    private RollProfileDefinition? profile;
    private GrindingProgram? program;
    private JobCheckResult? result;
    private JobDeviation planDeviation;
    private bool startMeasured;
    private bool grindStartTaught;
    private bool requireMeasure;
    private bool suppressEvaluate;
    private bool choosing;
    private bool makePlanChosen;
    private string? regrindOf;

    public JobViewModel(
        IRollPlanningService planning,
        IGrindingPlanService plans,
        ITemplateService templates,
        IJobAdjustmentService adjust,
        IRollLedgerService ledger,
        IRollProfileRepository profiles,
        IProgramRepository programs,
        IJobRepository jobs,
        IJobDownloadService downloadService,
        GrindingJobValidator validator,
        GrindingStepTypeRegistry stepTypes,
        RollProfileTypeRegistry profileTypes,
        MachineCapability capability,
        MachineDescription machine,
        HmiSettings settings,
        IUserSession session,
        PlanPickerViewModel picker,
        JobDraft draft,
        IManualGrindingService manualGrinding,
        IMachineMonitor monitor,
        ITagMap tagMap,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.planning = planning ?? throw new ArgumentNullException(nameof(planning));
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
        this.templates = templates ?? throw new ArgumentNullException(nameof(templates));
        this.adjust = adjust ?? throw new ArgumentNullException(nameof(adjust));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.capability = capability ?? throw new ArgumentNullException(nameof(capability));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        Picker = picker ?? throw new ArgumentNullException(nameof(picker));
        this.draft = draft ?? throw new ArgumentNullException(nameof(draft));
        this.manualGrinding = manualGrinding ?? throw new ArgumentNullException(nameof(manualGrinding));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));

        this.jobId = NewJobId();

        // 横键：机床区 JOG 那一排（最终稿 5.1），"作业"青底。
        SetFunctionKeys(MachineAreaKeys.Create(Navigator, localizer, MachineAreaKeys.Job));

        this.downloadCommand = new AsyncRelayCommand(DownloadAsync, () => CanDownload);
        this.cancelReviewKey = new FunctionKeyViewModel("Vk_BackToQueue", new RelayCommand(Navigator.CloseSubView), localizer, FunctionKeyKind.Cancel);

        // 下发是唯一的写机床通道；自动循环挂着程序时锁掉，免得把运行中的程序改了。下发前全部核对就是那一次确认，不再多问。
        this.confirmDownloadKey = new FunctionKeyViewModel("Vk_ConfirmDownload", this.downloadCommand, localizer, FunctionKeyKind.Confirm, requiresEditable: true)
        {
            IsMachineCommand = true,
            PreconditionResourceKey = "Job_ChecksBlock",
        };
        this.pickKey = new FunctionKeyViewModel("Vk_PickThis", new RelayCommand(ConfirmPick), localizer, FunctionKeyKind.Confirm);
        this.makePlanKey = new FunctionKeyViewModel("Vk_MakePlan", new RelayCommand(ChooseMakePlan), localizer, requiresEditable: true);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveSubViewKey))
            {
                OnPropertyChanged(nameof(IsReview));
                OnPropertyChanged(nameof(IsQueue));
                if (ActiveSubViewKey is null)
                {
                    IsPicking = false;
                    Loading = RunRefreshAsync(LoadQueueAsync, CancellationToken.None);
                }

                ApplyKeys();
            }
        };
        ApplyKeys();
    }

    public override PageKey Key => PageKey.Job;

    public override string TitleResourceKey => "Page_Job";

    /// <summary>自动循环挂着程序时落只读锁：正在磨的那支辊不能被换掉。</summary>
    public override bool LocksDuringRun => true;

    /// <summary>建作业、下发：操作者就能做（Q9）。</summary>
    public override Permission? EditPermission => Permission.EditJobs;

    /// <summary>离线也能核对；只有下发要机床。</summary>
    public override bool WorksOffline => true;

    /// <summary>选辊形 / 选程序子视图（与轧辊区共用）。</summary>
    public PlanPickerViewModel Picker { get; }

    /// <summary>最近一次刷新的任务（进页时开始）。自检等它读完再往下走。</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public bool IsReview => ActiveSubViewKey == ReviewSubView;

    public bool IsQueue => !IsReview;

    [ObservableProperty]
    private bool isPicking;

    public override bool HasModalPrompt => IsPicking || IsAdjusting;

    public override bool TryDismissPrompt()
    {
        if (!IsPicking && !IsAdjusting)
        {
            return false;
        }

        IsPicking = false;
        IsAdjusting = false;
        ApplyKeys();
        return true;
    }

    // ───────────── 待磨清单 ─────────────

    private readonly List<QueueRowViewModel> allRows = new();

    public ObservableCollection<QueueRowViewModel> Queue { get; } = new();

    [ObservableProperty]
    private QueueRowViewModel? selectedQueueRow;

    /// <summary>辊号或尾号：按尾号 / 包含匹配，只剩一支时直接选上。</summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string queueTitle = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedQueueRowChanged(QueueRowViewModel? value) => ApplyKeys();

    /// <summary>进页：从轧辊区登记回来的那支直接打开核对；在核对里回来（改过辊形 / 程序）重读再核对；否则刷新清单。</summary>
    public override void OnActivated()
    {
        if (this.draft.RegisteredRollId is { } registered)
        {
            this.draft.RegisteredRollId = null;
            Loading = RunGuardedAsync(
                async token =>
                {
                    await LoadQueueAsync(token).ConfigureAwait(true);
                    QueueRowViewModel? row = this.allRows.FirstOrDefault(r => r.RollId == registered);
                    if (row is not null)
                    {
                        SearchText = string.Empty;
                        SelectedQueueRow = row;
                        await OpenReviewAsync(row, token).ConfigureAwait(true);
                    }
                },
                CancellationToken.None);
            return;
        }

        Loading = IsReview
            ? RunGuardedAsync(ReloadChoicesAsync, CancellationToken.None)
            : RunGuardedAsync(LoadQueueAsync, CancellationToken.None);
    }

    private async Task LoadQueueAsync(CancellationToken cancellationToken)
    {
        string? keep = SelectedQueueRow?.RollId;
        IReadOnlyList<QueueEntry> entries = await this.planning.LoadQueueAsync(QueueLimit, cancellationToken).ConfigureAwait(true);

        this.allRows.Clear();
        foreach (QueueEntry entry in entries)
        {
            RollRecord r = entry.Roll;

            // 这支辊的磨法：当前方案（旧库的辊第一次列出来时按原计划建方案 1）。
            GrindingPlanRecord? current = await this.plans.CurrentAsync(r.RollId, cancellationToken).ConfigureAwait(true);
            string plan = current is null
                ? Localizer["Job_NoPlan"]
                : Localizer.Format("Job_QueuePlanFormat", current.PlanNo, current.Name);
            this.allRows.Add(new QueueRowViewModel(
                entry,
                entry.Mark switch
                {
                    QueueMark.Interrupted => Localizer["Mark_Interrupted"],
                    QueueMark.Regrind => Localizer["Mark_Regrind"],
                    _ => r.PlanInferred ? Localizer["Mark_Inferred"] : string.Empty,
                },
                Localizer["RollKind_" + r.Kind],
                r.Purpose ?? "--",
                plan,
                entry.RemainingMm is double remaining ? remaining.ToString("F2", CultureInfo.CurrentCulture) : "--",
                entry.LastGroundAtUtc is { } last ? last.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture) : "--",
                entry.Mark != QueueMark.None));
        }

        ApplyFilter();
        SelectedQueueRow = Queue.FirstOrDefault(row => row.RollId == keep) ?? SelectedQueueRow ?? Queue.FirstOrDefault();
    }

    private void ApplyFilter()
    {
        string search = (SearchText ?? string.Empty).Trim();
        IEnumerable<QueueRowViewModel> rows = search.Length == 0
            ? this.allRows
            : this.allRows
                .Where(row => row.RollId.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => row.RollId.EndsWith(search, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        Queue.Clear();
        foreach (QueueRowViewModel row in rows)
        {
            Queue.Add(row);
        }

        QueueTitle = search.Length == 0
            ? Localizer.Format("Job_QueueTitleFormat", Queue.Count, Queue.Count(row => row.IsPinned))
            : Localizer.Format("Job_QueueMatchFormat", search, Queue.Count);
        if (search.Length > 0)
        {
            SelectedQueueRow = Queue.FirstOrDefault();
        }
    }

    /// <summary>尾号框里按回车：只剩一支就直接下作业；一支都没有就提示去登记。</summary>
    [RelayCommand]
    private async Task SearchEnterAsync()
    {
        if (Queue.Count == 1)
        {
            SelectedQueueRow = Queue[0];
            await RunGuardedAsync(token => OpenReviewAsync(Queue[0], token), CancellationToken.None).ConfigureAwait(true);
        }
        else if (Queue.Count == 0 && !string.IsNullOrWhiteSpace(SearchText))
        {
            Interaction.Refuse(Localizer.Format("Job_NoMatchFormat", SearchText.Trim()));
        }
    }

    private void RegisterRoll()
    {
        this.draft.RegisterNewRollRequested = true;
        this.draft.RegisterRollIdHint = Queue.Count == 0 && !string.IsNullOrWhiteSpace(SearchText) ? SearchText.Trim() : null;
        Navigator.StartTask(PageKey.Rolls, PageKey.Job);
    }

    private void AskEndInterrupted()
    {
        if (SelectedQueueRow is not { Entry: { Mark: QueueMark.Interrupted, JobId: { } jobId } } row)
        {
            return;
        }

        Ask(
            "Job_AskEndInterrupted",
            async () => await RunGuardedAsync(
                async token =>
                {
                    await this.planning.EndInterruptedAsync(jobId, token).ConfigureAwait(true);
                    Say("Job_InterruptedEnded", row.RollId);
                    await LoadQueueAsync(token).ConfigureAwait(true);
                },
                CancellationToken.None).ConfigureAwait(true),
            row.RollId);
    }

    // ───────────── 一页核对 ─────────────

    [ObservableProperty]
    private string jobId;

    [ObservableProperty]
    private string reviewTitle = string.Empty;

    /// <summary>磨前直径（mm）。默认台账当前直径；改了就算实测。</summary>
    [ObservableProperty]
    private string startDiameterText = string.Empty;

    /// <summary>
    /// 磨削起点（机床 Z，mm）：这次装夹辊身头架侧端面在机床上的位置。按"取当前 Z"示教（原机"设置起点"），
    /// 也可以手输；默认带这支辊上一次作业的值（装夹没动就不用再示教）。NC 不要这个数时整行不显示。
    /// </summary>
    [ObservableProperty]
    private string grindStartZText = string.Empty;

    /// <summary>磨削起点这次示教过 / 手输过（否则是沿用上次的值）。</summary>
    [ObservableProperty]
    private string grindStartNote = string.Empty;

    /// <summary>NC 要磨削起点（tagmap 登记了 job.grindStartZMm）。</summary>
    public bool UsesGrindStart => this.tagMap.TryResolve(MachineTagKeys.JobGrindStartZMm, out _);

    /// <summary>本次磨削量（直径量 mm）。默认程序标准余量。</summary>
    [ObservableProperty]
    private string stockText = string.Empty;

    /// <summary>"最多可磨 x mm（到报废直径）"。</summary>
    [ObservableProperty]
    private string maxStockText = string.Empty;

    [ObservableProperty]
    private string targetText = "--";

    [ObservableProperty]
    private string checkSummaryText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool canDownload;

    [ObservableProperty]
    private string totalDurationText = "--";

    [ObservableProperty]
    private string deviationReason = string.Empty;

    /// <summary>这一次与当前方案的关系：按当前方案、仅本次用别的方案（要原因）、设为这支辊的当前方案（下发后生效）。</summary>
    public JobDeviation Deviation => this.planDeviation;

    public IAsyncRelayCommand DownloadCommand => this.downloadCommand;

    /// <summary>计划与这一次：台账计划、这一次用的、与计划的关系。</summary>
    public ObservableCollection<LabelValueViewModel> PlanRows { get; } = new();

    public ObservableCollection<CheckRowViewModel> Checks { get; } = new();

    public ObservableCollection<JobProgramStepRowViewModel> ProgramSteps { get; } = new();

    /// <summary>这一次的程序步骤开关，默认值来自程序。</summary>
    public ObservableCollection<ProgramOptionRowViewModel> ProgramOptions { get; } = new();

    /// <summary>"磨成什么样"：辊形按 2% 规则套在辊身上，横轴辊身坐标、纵轴直径量 µm。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> ReviewCurve { get; private set; } =
        Array.Empty<(double, double)>();

    public event EventHandler? ReviewCurveChanged;

    partial void OnStartDiameterTextChanged(string value)
    {
        if (!this.suppressEvaluate)
        {
            this.startMeasured = true;
        }

        Evaluate();
    }

    partial void OnStockTextChanged(string value) => Evaluate();

    partial void OnGrindStartZTextChanged(string value)
    {
        if (!this.suppressEvaluate)
        {
            this.grindStartTaught = true;
        }

        RefreshGrindStartNote();
        Evaluate();
    }

    private void RefreshGrindStartNote() =>
        GrindStartNote = string.IsNullOrWhiteSpace(GrindStartZText)
            ? Localizer["Job_GrindStartNotSet"]
            : Localizer[this.grindStartTaught ? "Job_GrindStartTaught" : "Job_GrindStartReused"];

    /// <summary>"取当前 Z"：把拖板 Z 现在的位置记成磨削起点（砂轮先对到辊身头架侧端面）。读不到 Z 就在对话行说。</summary>
    [RelayCommand]
    private void TeachGrindStart()
    {
        AxisDescription? carriage = this.machine.Axes.FirstOrDefault(axis => axis.IsPresent && axis.Role == MachineAxisRoles.Carriage);
        double? z = carriage is null ? null : this.monitor.Current.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm(carriage.Name));
        if (z is null)
        {
            Interaction.Hint(Localizer["Job_GrindStartNoZ"]);
            return;
        }

        GrindStartZText = z.Value.ToString("F3", CultureInfo.CurrentCulture);
        this.grindStartTaught = true;
        RefreshGrindStartNote();
    }

    /// <summary>这支辊最近一次作业示教过的磨削起点；没有为 null。</summary>
    private async Task<double?> LastGrindStartAsync(string rollId, CancellationToken cancellationToken)
    {
        foreach (string id in await this.jobs.ListJobIdsByRollAsync(rollId, 10, cancellationToken).ConfigureAwait(true))
        {
            if (await this.jobs.GetAsync(id, cancellationToken).ConfigureAwait(true) is { Job.GrindStartZMm: double z })
            {
                return z;
            }
        }

        return null;
    }

    partial void OnDeviationReasonChanged(string value) => Evaluate();

    [RelayCommand]
    private Task OpenReviewForSelectedAsync() => SelectedQueueRow is { } row
        ? RunGuardedAsync(token => OpenReviewAsync(row, token), CancellationToken.None)
        : Task.CompletedTask;

    /// <summary>
    /// 下作业：取台账计划；中断待续的接着那一份作业（必须重新量磨前直径）；返磨的用那份草稿。
    /// </summary>
    private async Task OpenReviewAsync(QueueRowViewModel row, CancellationToken cancellationToken)
    {
        this.roll = await this.ledger.GetAsync(row.RollId, cancellationToken).ConfigureAwait(true);
        if (this.roll is null)
        {
            return;
        }

        // 标准模板"必须采用"（设计说明 4.6）：要强制的先从新模板复制出新方案设为当前（原方案留作历史），要提示的列出来。
        // 中断续磨、返磨接着那一份作业，不换方案。
        this.templateNotices = row.Entry.JobId is null
            ? await this.templates.ApplyForJobAsync(this.roll.RollId, this.session.CurrentUser?.UserName ?? string.Empty, cancellationToken).ConfigureAwait(true)
            : Array.Empty<TemplateUpdateNotice>();

        // 作业取这支辊的当前方案（设计说明第 5 节）。
        GrindingPlanRecord? current = await this.plans.CurrentAsync(this.roll.RollId, cancellationToken).ConfigureAwait(true);
        this.roll = await this.ledger.GetAsync(row.RollId, cancellationToken).ConfigureAwait(true) ?? this.roll;
        string? planId = current?.PlanId;
        this.requireMeasure = false;
        this.regrindOf = null;
        JobId = NewJobId();
        this.planDeviation = JobDeviation.None;
        DeviationReason = string.Empty;
        (string? ProfileId, string? ProgramId)? legacy = null;
        IReadOnlyList<JobAdjustment> resumed = Array.Empty<JobAdjustment>();

        if (row.Entry.JobId is { } storedId && await this.jobs.GetAsync(storedId, cancellationToken).ConfigureAwait(true) is { } stored)
        {
            // 中断续磨、返磨：接着那一份作业用的方案。
            JobId = storedId;
            this.regrindOf = stored.Job.RegrindOfJobId;
            this.requireMeasure = row.Entry.Mark == QueueMark.Interrupted;
            DeviationReason = stored.Job.DeviationReason ?? string.Empty;
            resumed = stored.Job.Adjustments;
            if (stored.Job.PlanId is { } storedPlan)
            {
                planId = storedPlan;
                this.planDeviation = storedPlan == this.roll.CurrentPlanId ? JobDeviation.None : stored.Job.Deviation;
            }
            else
            {
                // 方案以前的作业：按它当时的辊形与程序。
                legacy = (stored.Job.ProfileId, stored.Job.ProgramId);
                this.planDeviation = stored.Job.Deviation;
            }
        }

        if (legacy is { } old)
        {
            this.plan = null;
            this.profile = old.ProfileId is null ? null : await this.profiles.GetAsync(old.ProfileId, cancellationToken).ConfigureAwait(true);
            this.program = old.ProgramId is null ? null : await this.programs.GetAsync(old.ProgramId, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            this.plan = null;
            await LoadPlanAsync(planId, cancellationToken).ConfigureAwait(true);
        }

        // 中断续磨、返磨：接着那一份作业的调整（含运行中改的）。
        this.adjustments.Clear();
        this.adjustments.AddRange(AdjustmentLists.Latest(resumed));

        ResetOptions();

        this.suppressEvaluate = true;
        this.startMeasured = false;
        StartDiameterText = this.requireMeasure ? string.Empty : this.roll.StartDiameterMm.ToString("F3", CultureInfo.CurrentCulture);
        StockText = DefaultStockMm().ToString("F3", CultureInfo.CurrentCulture);
        this.grindStartTaught = false;
        GrindStartZText = UsesGrindStart && await LastGrindStartAsync(this.roll.RollId, cancellationToken).ConfigureAwait(true) is double lastZ
            ? lastZ.ToString("F3", CultureInfo.CurrentCulture)
            : string.Empty;
        RefreshGrindStartNote();
        this.suppressEvaluate = false;

        ReviewTitle = Localizer.Format("Job_ReviewTitleFormat", this.roll.RollId, Localizer["RollKind_" + this.roll.Kind], this.roll.Purpose ?? "--");
        Navigator.OpenSubView(ReviewSubView);
        Evaluate();
        if (this.requireMeasure)
        {
            Interaction.Hint(Localizer["Job_MeasureAfterInterrupt"]);
        }
    }

    /// <summary>
    /// 从方案区改完回来：重读这一次用的方案。按当前方案的，改了用过的方案会另存为新方案并设为当前——跟着用新的；
    /// 仅本次用别的方案的，还是那一个。
    /// </summary>
    private async Task ReloadChoicesAsync(CancellationToken cancellationToken)
    {
        if (this.roll is null)
        {
            return;
        }

        this.roll = await this.ledger.GetAsync(this.roll.RollId, cancellationToken).ConfigureAwait(true) ?? this.roll;
        if (this.plan is not null)
        {
            string? planId = this.planDeviation == JobDeviation.None ? this.roll.CurrentPlanId ?? this.plan.PlanId : this.plan.PlanId;
            await LoadPlanAsync(planId, cancellationToken).ConfigureAwait(true);
        }

        Evaluate();
    }

    /// <summary>取方案与它的辊形、工艺；缺任何一样时三样都空（核对页拦"没有方案"）。</summary>
    private async Task LoadPlanAsync(string? planId, CancellationToken cancellationToken)
    {
        string? before = this.plan?.PlanId;
        PlanContent? content = planId is null ? null : await this.plans.GetContentAsync(planId, cancellationToken).ConfigureAwait(true);
        this.plan = content?.Plan;
        this.profile = content?.Profile;
        this.program = content?.Program;

        // 两层参数（设计说明第 5 节）：方案的可调范围；换了方案，本次调整归零（下次默认回到方案值）。
        if (content is null)
        {
            this.adjustable = Array.Empty<AdjustableParameter>();
            this.lastAdjustments = Array.Empty<JobAdjustment>();
        }
        else
        {
            this.adjustable = this.adjust.Describe(
                content.Program, await this.plans.GetRangesAsync(content.Plan.PlanId, cancellationToken).ConfigureAwait(true));
            this.lastAdjustments = this.roll is null
                ? Array.Empty<JobAdjustment>()
                : AdjustmentLists.Latest(await this.adjust.LastAdjustmentsAsync(this.roll.RollId, content.Plan.PlanId, JobId, cancellationToken).ConfigureAwait(true));
        }

        if (before != this.plan?.PlanId)
        {
            this.adjustments.Clear();
        }
    }

    private double DefaultStockMm()
    {
        if (this.program is null)
        {
            return 0.0;
        }

        double micrometer = this.program.StandardStockMicrometer
            ?? StockAdjustment.Apply(ProgramFrame.Normalize(this.program.Steps, this.stepTypes), 1.0).ProgramStockMicrometer;
        return micrometer / 1000.0;
    }

    private void ResetOptions()
    {
        if (this.program is null)
        {
            return;
        }

        // 只列这份作业用得上的开关：程序里有那道工序、机床有那个装置。挂工序的默认开（程序排了就是要做）。
        IReadOnlyList<GrindingJobStep> steps = ProgramFrame.Normalize(this.program.Steps, this.stepTypes);
        this.suppressEvaluate = true;
        ProgramOptions.Clear();
        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All
                     .Where(option => ProgramOptionPlanner.AppliesTo(option, steps, this.capability, this.stepTypes)))
        {
            var row = new ProgramOptionRowViewModel(
                option, ProgramOptionPlanner.DefaultFor(option), ProgramOptionPlanner.LinkedStepOrder(option.Key, steps, this.stepTypes), Localizer);
            row.PropertyChanged += (_, _) => Evaluate();
            ProgramOptions.Add(row);
        }

        this.suppressEvaluate = false;
    }

    /// <summary>跑一遍核对清单，拼出这份作业，画"磨成什么样"。</summary>
    private void Evaluate()
    {
        if (this.suppressEvaluate || !IsReview && this.roll is null)
        {
            return;
        }

        Checks.Clear();
        PlanRows.Clear();
        ProgramSteps.Clear();
        this.result = null;
        TotalDurationText = "--";
        FillPlanRows();

        if (this.roll is null)
        {
            Finish(null);
            return;
        }

        if (this.roll.ScrapDiameterMm is double scrap && TryNumber(StartDiameterText, out double startForMax))
        {
            MaxStockText = Localizer.Format("Job_MaxStockFormat", Math.Max(startForMax - scrap, 0.0), scrap);
        }
        else
        {
            MaxStockText = Localizer["Job_MaxStockUnknown"];
        }

        if (this.profile is null || this.program is null)
        {
            Checks.Add(new CheckRowViewModel(Localizer["CheckItem_Library"], Localizer["Check_Plan_Missing"], JobCheckStatus.Block));
            Finish(null);
            return;
        }

        bool startOk = TryNumber(StartDiameterText, out double start) && start > 0.0;
        bool stockOk = TryNumber(StockText, out double stockMm);
        if (!startOk || !stockOk)
        {
            Checks.Add(new CheckRowViewModel(
                Localizer[startOk ? "CheckItem_Stock" : "CheckItem_StartDiameter"],
                Localizer[!startOk && this.requireMeasure ? "Check_StartDiameter_MeasureAfterInterrupt" : "Check_NotANumber"],
                JobCheckStatus.Block));
            Finish(null);
            return;
        }

        TargetText = (start - stockMm).ToString("F3", CultureInfo.CurrentCulture);
        GrindingProgram normalized = this.program with { Steps = ProgramFrame.Normalize(this.program.Steps, this.stepTypes) };
        // 长度：方案名义长度以中心为基准对到这一次辊身长度（有实测用实测）上，截取 / 延伸只在端部非工作区。
        LengthAlignment alignment = GrindingPlanService.AlignForJob(
            this.profile, this.roll.GrindBodyLengthMm, this.plan?.EndZoneMm, this.machine, this.profileTypes, this.settings.ProfileSampleCount);
        JobCheckResult checks = JobChecklist.Evaluate(
            new JobCheckInput(
                this.roll, this.profile, normalized, start, this.startMeasured, stockMm * 1000.0, Deviation, NullIfBlank(DeviationReason),
                GrindStartZ(), this.grindStartTaught, UsesGrindStart, alignment),
            this.machine);

        GrindingJob? job = null;
        IReadOnlyList<ParameterViolation> violations = Array.Empty<ParameterViolation>();
        try
        {
            job = BuildJob(checks, normalized, start, stockMm * 1000.0);
            if (job is not null)
            {
                violations = this.validator.Validate(job, this.capability).Violations;
                checks = JobChecklist.WithViolations(checks, violations);
            }
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
            job = null;
        }

        if (this.requireMeasure && !this.startMeasured)
        {
            checks = checks with
            {
                Checks = checks.Checks
                    .Select(check => check.Item == JobCheckItems.StartDiameter
                        ? check with { Status = JobCheckStatus.Block, MessageKey = "Check_StartDiameter_MeasureAfterInterrupt", Args = Array.Empty<object>() }
                        : check)
                    .ToArray(),
            };
        }

        var rows = checks.Checks
            .Where(check => check.Item != JobCheckItems.Parameters)
            .Select(check => new CheckRowViewModel(Localizer["CheckItem_" + check.Item], FormatCheck(check), check.Status))
            .Concat(violations.Select(ViolationCheck))
            .Concat(AdjustmentChecks())
            .Concat(this.templateNotices.Select(TemplateNoticeRow))
            .OrderByDescending(row => row.Status);
        foreach (CheckRowViewModel row in rows)
        {
            Checks.Add(row);
        }

        this.result = checks;
        FillProgramSteps(job);
        Finish(job);
    }

    private void Finish(GrindingJob? job)
    {
        int pass = Checks.Count(c => c.Status == JobCheckStatus.Pass);
        int notice = Checks.Count(c => c.IsNotice);
        int block = Checks.Count(c => c.IsBlock);
        CheckSummaryText = Localizer.Format("Job_CheckSummaryFormat", pass, notice, block);
        CanDownload = job is not null && this.result is { CanDownload: true } && block == 0;
        ReviewCurve = job is null
            ? Array.Empty<(double, double)>()
            : job.Profile.Compose(job.Geometry, this.profileTypes, this.settings.ProfileSampleCount).Points
                .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
                .ToArray();
        ReviewCurveChanged?.Invoke(this, EventArgs.Empty);
        ApplyKeys();
    }

    /// <summary>
    /// 本次注意：标准模板已强制更新（原方案 n 转为历史，本次用新方案 m）/ 标准已更新（只提示）/ 要换但换不了（请工艺员处理）。都不拦人。
    /// </summary>
    private CheckRowViewModel TemplateNoticeRow(TemplateUpdateNotice notice) => new(
        Localizer["CheckItem_Template"],
        notice switch
        {
            { Forced: true, NewPlanNo: int created } => Localizer.Format("Check_Template_ForcedFormat", notice.TemplateName, notice.OldPlanNo, created),
            { Forced: true } => Localizer.Format("Check_Template_ForceFailedFormat", notice.TemplateName, Localizer[notice.ProblemKey ?? "PlanProblem_Missing"]),
            _ => Localizer.Format("Check_Template_UpdatedFormat", notice.TemplateName),
        },
        JobCheckStatus.Notice);

    /// <summary>
    /// 本次调整（设计说明 5.2 / 5.4）：超范围不收（拦）；有调整列出来（提示）；上一次调整过、这次还没调的问一句要不要沿用。
    /// </summary>
    private IEnumerable<CheckRowViewModel> AdjustmentChecks()
    {
        foreach (AdjustProblem problem in JobAdjustmentService.Validate(this.adjustable, this.adjustments))
        {
            yield return new CheckRowViewModel(
                Localizer["CheckItem_Adjust"],
                Localizer.Format(
                    "Check_Adjust_RefusedFormat", problem.StepOrder, AdjustText.Parameter(Localizer, problem.ParameterKey),
                    AdjustText.Refusal(Localizer, problem.Refusal)),
                JobCheckStatus.Block);
        }

        if (this.adjustments.Count > 0)
        {
            yield return new CheckRowViewModel(
                Localizer["CheckItem_Adjust"],
                Localizer.Format("Check_Adjust_CountFormat", this.adjustments.Count, AdjustSummary(this.adjustments)),
                JobCheckStatus.Notice);
        }
        else if (this.lastAdjustments.Count > 0)
        {
            yield return new CheckRowViewModel(
                Localizer["CheckItem_Adjust"],
                Localizer.Format("Check_Adjust_LastFormat", this.lastAdjustments.Count, AdjustSummary(this.lastAdjustments)),
                JobCheckStatus.Notice);
        }
    }

    private string AdjustSummary(IEnumerable<JobAdjustment> list) => string.Join(
        Localizer["Adjust_ListSeparator"],
        list.Select(item => AdjustText.Change(
            Localizer, item, this.adjustable.FirstOrDefault(row => row.StepOrder == item.StepOrder)?.StepTypeKey)));

    private CheckRowViewModel ViolationCheck(ParameterViolation violation)
    {
        var text = new ViolationRowViewModel(violation, Localizer);
        return new CheckRowViewModel(
            Localizer["CheckItem_Parameters"], Localizer.Format("Check_Parameters_ViolationFormat", text.ParameterText, text.ReasonText), JobCheckStatus.Block);
    }

    /// <summary>核对一项的说明：参数里是资源键的（"RollKind_…"、"Param_…"）先本地化。</summary>
    private string FormatCheck(JobCheck check)
    {
        object[] args = check.Args
            .Select(arg => arg is string text && (text.StartsWith("RollKind_", StringComparison.Ordinal)
                || text.StartsWith("Check_", StringComparison.Ordinal))
                ? Localizer[text]
                : arg)
            .ToArray();
        return args.Length == 0 ? Localizer[check.MessageKey] : Localizer.Format(check.MessageKey, args);
    }

    private void FillPlanRows()
    {
        if (this.roll is null)
        {
            return;
        }

        PlanRows.Add(new LabelValueViewModel("Job_PlanRow", this.plan is null ? "--" : PlanText(this.plan), Localizer));
        PlanRows.Add(new LabelValueViewModel("Job_PlanProfile", this.profile?.Name ?? "--", Localizer));
        PlanRows.Add(new LabelValueViewModel("Job_PlanProgram", this.program?.Name ?? "--", Localizer));
        PlanRows.Add(new LabelValueViewModel("Job_Deviation", Deviation switch
        {
            JobDeviation.ThisTimeOnly => Localizer.Format("Job_DeviationThisTimeFormat", string.IsNullOrWhiteSpace(DeviationReason) ? Localizer["Job_ReasonMissing"] : ReasonText(DeviationReason)),
            JobDeviation.PlanChanged => Localizer["Job_DeviationPlanChanged"],
            _ => Localizer["Job_DeviationNone"],
        }, Localizer));
        PlanRows.Add(new LabelValueViewModel(
            "Job_AdjustRow",
            this.adjustments.Count == 0 ? Localizer["Job_AdjustNone"] : Localizer.Format("Job_AdjustCountFormat", this.adjustments.Count),
            Localizer));
        PlanRows.Add(new LabelValueViewModel("Job_JobIdLabel", JobId + (this.regrindOf is { } of ? " " + Localizer.Format("Job_RegrindOfFormat", of) : string.Empty), Localizer));
    }

    /// <summary>"方案 3 · 凸度300 · 工作辊标准 · ★ 当前"（或"仅本次""设为当前"）。</summary>
    private string PlanText(GrindingPlanRecord shown) =>
        Localizer.Format("Job_QueuePlanFormat", shown.PlanNo, shown.Name)
        + (this.planDeviation == JobDeviation.ThisTimeOnly ? " · " + Localizer["Mark_ThisTime"]
            : this.planDeviation == JobDeviation.PlanChanged ? " · " + Localizer["Mark_PlanChanged"]
            : shown.PlanId == this.roll?.CurrentPlanId ? " · " + Localizer["Mark_CurrentPlan"]
            : string.Empty);

    private string ReasonText(string reason) => reason.StartsWith("Reason_", StringComparison.Ordinal) ? Localizer[reason] : reason;

    private void FillProgramSteps(GrindingJob? job)
    {
        if (job is null)
        {
            return;
        }

        TimeSpan total = TimeSpan.Zero;
        foreach (GrindingJobStep step in job.Steps)
        {
            string duration = string.Empty;
            try
            {
                TimeSpan estimate = this.stepTypes.Get(step.StepTypeKey).CreatePlan(job.Geometry, step.Parameters).EstimateDuration(job.Geometry);
                total += estimate;
                duration = estimate > TimeSpan.Zero ? Localizer.Format("Auto_StepDurationFormat", (int)estimate.TotalMinutes) : string.Empty;
            }
            catch (DomainException)
            {
                duration = string.Empty;
            }

            ProgramSteps.Add(new JobProgramStepRowViewModel(
                step.Order.ToString(CultureInfo.InvariantCulture), StepName(step), duration));
        }

        TotalDurationText = Localizer.Format("Steps_TotalTimeFormat", (int)total.TotalMinutes);
    }

    /// <summary>
    /// 核对页工序清单里的名字。辅助动作写明做什么（"辅助动作：中心架 开"）：这是工艺员编在程序里的，
    /// 与下面"本次取舍"不是一回事，开磨前一起看一眼。
    /// </summary>
    private string StepName(GrindingJobStep step)
    {
        string name = Localizer["StepType_" + step.StepTypeKey];
        if (step.StepTypeKey != StepTypeKeys.Auxiliary)
        {
            return name;
        }

        ParameterSet values = this.stepTypes.Get(step.StepTypeKey).Schema.ApplyDefaults(step.Parameters);
        string Choice(string key)
        {
            ParameterDescriptor descriptor = this.stepTypes.Get(step.StepTypeKey).Schema.Get(key);
            return values.TryGet(key, out ParameterValue? value) && value is { Kind: ParameterValueKind.Choice }
                ? Localizer[descriptor.ChoiceResourceKey(value.Choice)]
                : string.Empty;
        }

        return Localizer.Format("Job_AuxiliaryStepFormat", name, Choice(StepParameterKeys.AuxAction), Choice(StepParameterKeys.AuxState));
    }

    /// <summary>按核对结果拼出这份作业；辊形套不上（长度差太多）返回 null。</summary>
    private GrindingJob? BuildJob(JobCheckResult checks, GrindingProgram normalized, double startMm, double stockMicrometer)
    {
        if (this.roll is null || this.profile is null || checks.Alignment.Profile is not { } fitted)
        {
            return null;
        }

        IReadOnlyList<GrindingJobStep> steps = ApplyAdjustments(
            checks.Stock.ProblemResourceKey is null ? checks.Stock.Steps : normalized.Steps);

        // 十个开关都下发：没列出来的（用不上）一律关，NC 不会收到开着却没有对应工序或装置的开关。
        ParameterSet options = ProgramOptionPlanner.Normalize(
            new ParameterSet(ProgramOptions.Select(row =>
                new KeyValuePair<string, ParameterValue>(row.Descriptor.Key, ParameterValue.FromBoolean(row.IsOn)))),
            steps,
            this.capability,
            this.stepTypes);

        // 辊身长度：有实测用实测，否则用名义；辊形是按它对齐好的那一份。
        return GrindingJob.Create(JobId, this.roll.RollId, RollGeometry.FromDiameter(checks.Alignment.BodyLengthMm, startMm), fitted, steps, options) with
        {
            PlanId = this.plan?.PlanId,
            PlanNo = this.plan?.PlanNo,
            Alignment = checks.Alignment,
            ProfileId = this.profile.ProfileId,
            ProfileName = this.profile.Name,
            ProfileVersion = this.profile.Version,
            ProgramId = normalized.ProgramId,
            ProgramName = normalized.Name,
            ProgramVersion = normalized.Version,
            StartDiameterMm = startMm,
            StockMicrometer = stockMicrometer,
            Deviation = Deviation,
            DeviationReason = Deviation == JobDeviation.ThisTimeOnly ? NullIfBlank(DeviationReason) : null,
            RegrindOfJobId = this.regrindOf,
            ScrapDiameterMm = this.roll.ScrapDiameterMm,
            GrindStartZMm = UsesGrindStart ? GrindStartZ() : null,
            Adjustments = this.adjustments.ToArray(),
        };
    }

    /// <summary>方案值换成本次值（工序里已经是本次值，记录另记"改了什么"）。</summary>
    private IReadOnlyList<GrindingJobStep> ApplyAdjustments(IReadOnlyList<GrindingJobStep> steps)
    {
        if (this.adjustments.Count == 0)
        {
            return steps;
        }

        GrindingJobStep[] adjusted = steps.ToArray();
        foreach (JobAdjustment item in this.adjustments)
        {
            int index = Array.FindIndex(adjusted, step => step.Order == item.StepOrder);
            if (index >= 0)
            {
                adjusted[index] = adjusted[index] with
                {
                    Parameters = adjusted[index].Parameters.With(item.ParameterKey, ParameterValue.FromNumber(item.JobValue)),
                };
            }
        }

        return adjusted;
    }

    // ───────────── 本次调整（设计稿 v4 5.2.5） ─────────────

    /// <summary>可调的参数（工序、参数、设定值、本次值、允许、上次）。</summary>
    public ObservableCollection<AdjustRowViewModel> AdjustRows { get; } = new();

    /// <summary>不可调的（🔒）：要改就改方案。</summary>
    public ObservableCollection<LockedParameterRowViewModel> LockedRows { get; } = new();

    [ObservableProperty]
    private AdjustRowViewModel? selectedAdjustRow;

    [ObservableProperty]
    private bool isAdjusting;

    [ObservableProperty]
    private string adjustTitle = string.Empty;

    partial void OnSelectedAdjustRowChanged(AdjustRowViewModel? value) => ApplyKeys();

    /// <summary>打开本次调整：各项从已定的本次值（没调的是方案值）开始。</summary>
    private void OpenAdjust()
    {
        if (this.plan is null || this.adjustable.Count == 0)
        {
            return;
        }

        AdjustRows.Clear();
        LockedRows.Clear();
        foreach (AdjustableParameter row in this.adjustable)
        {
            if (!row.Adjustable)
            {
                if (!ProgramFrame.IsFixed(row.StepTypeKey))
                {
                    LockedRows.Add(new LockedParameterRowViewModel(
                        Localizer.Format("Adjust_StepFormat", row.StepOrder, Localizer["StepType_" + row.StepTypeKey]),
                        AdjustText.Parameter(Localizer, row.ParameterKey),
                        AdjustText.Number(row.PlanValue)));
                }

                continue;
            }

            double value = this.adjustments.LastOrDefault(item => item.StepOrder == row.StepOrder && item.ParameterKey == row.ParameterKey)
                ?.JobValue ?? row.PlanValue;
            double? last = this.lastAdjustments.FirstOrDefault(item => item.StepOrder == row.StepOrder && item.ParameterKey == row.ParameterKey)?.JobValue;
            var vm = new AdjustRowViewModel(row, value, last, Localizer);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AdjustRowViewModel.IsRefused) or nameof(AdjustRowViewModel.IsChanged))
                {
                    RefreshAdjustTitle();
                }
            };
            AdjustRows.Add(vm);
        }

        SelectedAdjustRow = AdjustRows.FirstOrDefault(row => row.IsChanged) ?? AdjustRows.FirstOrDefault();
        RefreshAdjustTitle();
        IsAdjusting = true;
        ApplyKeys();
    }

    private void RefreshAdjustTitle()
    {
        AdjustTitle = Localizer.Format(
            "Adjust_TitleFormat", this.plan is null ? "--" : Localizer.Format("Job_QueuePlanFormat", this.plan.PlanNo, this.plan.Name),
            AdjustRows.Count(row => row.IsChanged));
        ApplyKeys();
    }

    [RelayCommand]
    private void AdjustDown() => SelectedAdjustRow?.Nudge(-1);

    [RelayCommand]
    private void AdjustUp() => SelectedAdjustRow?.Nudge(1);

    /// <summary>第 1 格"载入上次"：上一次作业调过的值都带进来（仍按范围核）。</summary>
    private void LoadLastAdjustments()
    {
        foreach (AdjustRowViewModel row in AdjustRows)
        {
            if (row.LastValue is double last)
            {
                row.ValueText = AdjustText.Number(last);
            }
        }
    }

    /// <summary>"✓ 用于本次"：超范围的不收；收了记下谁、何时（同一项没再改的保留原来的时间）。</summary>
    private void CommitAdjust()
    {
        if (AdjustRows.FirstOrDefault(row => row.IsRefused) is { } refused)
        {
            SelectedAdjustRow = refused;
            Interaction.Fail(Localizer.Format("Adjust_RefusedFormat", refused.ParameterText, refused.RefusalText));
            return;
        }

        string user = this.session.CurrentUser?.UserName ?? string.Empty;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var next = new List<JobAdjustment>();
        foreach (AdjustRowViewModel row in AdjustRows.Where(row => row.IsChanged && row.Value is double))
        {
            double value = row.Value!.Value;
            JobAdjustment? kept = this.adjustments.LastOrDefault(
                item => item.StepOrder == row.Parameter.StepOrder && item.ParameterKey == row.Parameter.ParameterKey);
            next.Add(kept is not null && Math.Abs(kept.JobValue - value) <= 1e-9
                ? kept
                : new JobAdjustment(row.Parameter.StepOrder, row.Parameter.ParameterKey, row.Parameter.PlanValue, value, false, user, now));
        }

        // 中断续磨带过来的运行中调整：这里没动的照留。
        next.AddRange(this.adjustments.Where(item => item.Running
            && !next.Any(n => n.StepOrder == item.StepOrder && n.ParameterKey == item.ParameterKey)
            && !AdjustRows.Any(row => row.Parameter.StepOrder == item.StepOrder && row.Parameter.ParameterKey == item.ParameterKey && !row.IsChanged)));
        this.adjustments.Clear();
        this.adjustments.AddRange(next);
        IsAdjusting = false;
        Evaluate();
        Say(this.adjustments.Count == 0 ? "Adjust_AllPlanValues" : "Adjust_Applied");
    }

    private void CloseAdjust()
    {
        IsAdjusting = false;
        ApplyKeys();
    }

    private double? GrindStartZ() => TryNumber(GrindStartZText, out double z) ? z : null;

    /// <summary>当前这份作业（自检、测试用）；核对没过返回 null。</summary>
    public GrindingJob? BuildJob()
    {
        if (this.result is null || this.program is null || !TryNumber(StartDiameterText, out double start) || !TryNumber(StockText, out double stockMm))
        {
            return null;
        }

        GrindingProgram normalized = this.program with { Steps = ProgramFrame.Normalize(this.program.Steps, this.stepTypes) };
        return BuildJob(this.result, normalized, start, stockMm * 1000.0);
    }

    // ───────────── 换方案 ─────────────

    private bool CanMakePlan => !this.settings.PlanChangeNeedsAdministrator || this.session.Can(Permission.EditRollPlans);

    /// <summary>换方案（设计稿 v4 5.2.3）：列这支辊的方案，右边与这一次原来用的叠图。</summary>
    private async Task OpenPickerAsync()
    {
        if (this.roll is null)
        {
            return;
        }

        string rollId = this.roll.RollId;
        await RunGuardedAsync(
            token => Picker.LoadPlansAsync(rollId, this.plan?.PlanId ?? this.roll.CurrentPlanId, token),
            CancellationToken.None).ConfigureAwait(true);
        IsPicking = true;
        ApplyKeys();
    }

    /// <summary>
    /// 选好了：就是当前方案直接用；别的方案问"仅本次用（随后选原因）/ 设为这支辊的当前方案… / 取消"——
    /// 仅本次在第 8 格（安全的一边），设为当前在第 6 格（要管理员），不超时（设计稿 v4 5.2.3、5.2.4）。
    /// </summary>
    private void ConfirmPick()
    {
        if (Picker.SelectedItem is not { } item || this.roll is null)
        {
            return;
        }

        IsPicking = false;
        if (this.roll.CurrentPlanId is null || this.roll.CurrentPlanId == item.Id)
        {
            _ = RunRefreshAsync(token => ApplyPickAsync(item.Id, JobDeviation.None, token), CancellationToken.None);
            return;
        }

        this.choosing = true;
        this.makePlanChosen = false;
        ApplyKeys();
        Interaction.Choose(
            Localizer.Format(CanMakePlan ? "Job_AskDeviationFormat" : "Job_AskThisTimeFormat", item.Name),
            "Vk_ThisTimeOnly",
            () => RunGuardedAsync(
                async token =>
                {
                    await ApplyPickAsync(item.Id, JobDeviation.ThisTimeOnly, token).ConfigureAwait(true);
                    if (string.IsNullOrWhiteSpace(DeviationReason))
                    {
                        OpenReasonMenu();
                    }
                },
                CancellationToken.None),
            outcome =>
            {
                this.choosing = false;
                if (this.makePlanChosen)
                {
                    this.makePlanChosen = false;
                    _ = RunRefreshAsync(token => ApplyPickAsync(item.Id, JobDeviation.PlanChanged, token), CancellationToken.None);
                }

                ApplyKeys();
            });
    }

    /// <summary>第 6 格"设为当前方案…"：收掉待答的问题，按设为当前处理（下发成功后生效）。</summary>
    private void ChooseMakePlan()
    {
        if (!this.choosing || !CanMakePlan)
        {
            return;
        }

        this.makePlanChosen = true;
        Interaction.Confirmations.Cancel();
    }

    private async Task ApplyPickAsync(string planId, JobDeviation deviation, CancellationToken cancellationToken)
    {
        await LoadPlanAsync(planId, cancellationToken).ConfigureAwait(true);
        this.planDeviation = deviation;
        ResetOptions();
        this.suppressEvaluate = true;
        StockText = DefaultStockMm().ToString("F3", CultureInfo.CurrentCulture);
        this.suppressEvaluate = false;
        if (Deviation != JobDeviation.ThisTimeOnly)
        {
            DeviationReason = string.Empty;
        }

        Evaluate();
    }

    /// <summary>回到当前方案：这一次照这支辊的当前方案磨。</summary>
    private void RestorePlan()
    {
        if (this.roll is null)
        {
            return;
        }

        _ = RunGuardedAsync(
            async token =>
            {
                this.planDeviation = JobDeviation.None;
                await LoadPlanAsync(this.roll.CurrentPlanId, token).ConfigureAwait(true);
                ResetOptions();
                this.suppressEvaluate = true;
                StockText = DefaultStockMm().ToString("F3", CultureInfo.CurrentCulture);
                DeviationReason = string.Empty;
                this.suppressEvaluate = false;
                Evaluate();
            },
            CancellationToken.None);
    }

    /// <summary>仅本次的原因：常用项竖键，选了就记（关系设计 O2）。</summary>
    private void OpenReasonMenu() => OpenVerticalMenu("Vk_Reason", new FunctionKeyViewModel?[]
    {
        MenuChoice("Reason_Trial", () => DeviationReason = "Reason_Trial", requiresEditable: false),
        MenuChoice("Reason_Quality", () => DeviationReason = "Reason_Quality", requiresEditable: false),
        MenuChoice("Reason_Equipment", () => DeviationReason = "Reason_Equipment", requiresEditable: false),
        MenuChoice("Reason_ScheduleChange", () => DeviationReason = "Reason_ScheduleChange", requiresEditable: false),
        MenuChoice("Reason_CustomerRequest", () => DeviationReason = "Reason_CustomerRequest", requiresEditable: false),
    });

    /// <summary>到方案区改这一次用的方案（辊形页）；改了用过的方案保存时另存为新方案，回来自动重读。</summary>
    private void OpenProfile()
    {
        this.draft.PlanToEdit = this.plan?.PlanId;
        Navigator.StartTask(PageKey.Profile, PageKey.Job);
    }

    /// <summary>同上，工艺页。</summary>
    private void OpenProgram()
    {
        this.draft.PlanToEdit = this.plan?.PlanId;
        Navigator.StartTask(PageKey.Steps, PageKey.Job);
    }

    /// <summary>
    /// 方案 ▸（设计稿 v4 I7）：换方案… · 原因 ▸ · 回到当前方案 · 辊形 · 工艺 · 方案列表。
    /// 原因是下一层子菜单，不走 <see cref="PageViewModelBase.MenuChoice"/>（那个选完就收回根层）。
    /// </summary>
    private void OpenPlanMenu() => OpenVerticalMenu("Vk_PlanMenu", new FunctionKeyViewModel?[]
    {
        MenuChoice("Vk_SwitchPlan", () => _ = OpenPickerAsync()),
        new FunctionKeyViewModel("Vk_Reason", new RelayCommand(OpenReasonMenu, () => Deviation == JobDeviation.ThisTimeOnly), Localizer, requiresEditable: true)
        {
            PreconditionResourceKey = "Job_ReasonOnlyThisTime",
        },
        PlanMenuChoice("Vk_RestorePlan", RestorePlan, () => Deviation != JobDeviation.None, "Job_AlreadyPlan", requiresEditable: true),
        PlanMenuChoice("Vk_OpenProfile", OpenProfile, () => this.plan is not null, "Job_NothingSelected", requiresEditable: false),
        PlanMenuChoice("Vk_OpenProgram", OpenProgram, () => this.plan is not null, "Job_NothingSelected", requiresEditable: false),
        MenuChoice("Vk_PlanList", OpenPlans, requiresEditable: false),
    });

    /// <summary>子菜单里一项带灰键原因的：按下去做事，然后收回根层。</summary>
    private FunctionKeyViewModel PlanMenuChoice(string labelKey, Action action, Func<bool> canChoose, string precondition, bool requiresEditable) =>
        new(labelKey, new RelayCommand(() => { action(); ResetVerticalMenu(); }, canChoose), Localizer, requiresEditable: requiresEditable)
        {
            PreconditionResourceKey = precondition,
        };

    /// <summary>
    /// 第 6 格"改用新模板…"（设计稿 v4 I7）：这支辊的当前方案复制自的模板出了新版（标准已更新）时才有；
    /// 问一句后从新模板复制出新方案设为当前（原方案留作历史），重新核对。仅本次用别的方案时不出。
    /// </summary>
    private FunctionKeyViewModel? UseNewTemplateKey() =>
        this.planDeviation == JobDeviation.None && this.templateNotices.Any(notice => notice.CanAdopt && notice.PlanId == this.plan?.PlanId)
            ? new FunctionKeyViewModel("Vk_UseNewTemplate", new RelayCommand(AskUseNewTemplate), Localizer, requiresEditable: true)
            {
                RequiredPermission = this.settings.PlanChangeNeedsAdministrator ? Permission.EditRollPlans : null,
            }
            : null;

    private void AskUseNewTemplate()
    {
        TemplateUpdateNotice[] adoptable = this.templateNotices.Where(notice => notice.CanAdopt).ToArray();
        if (this.roll is null || adoptable.Length == 0)
        {
            return;
        }

        string rollId = this.roll.RollId;
        Ask(
            "Job_AskUseNewTemplateFormat",
            () => RunGuardedAsync(
                async token =>
                {
                    PlanCreateOutcome outcome = await this.templates
                        .AdoptAsync(rollId, adoptable, this.session.CurrentUser?.UserName ?? string.Empty, token).ConfigureAwait(true);
                    if (outcome.Plan is not { } created)
                    {
                        Interaction.Fail(Localizer[outcome.ProblemKey ?? "PlanProblem_Missing"]);
                        return;
                    }

                    this.roll = await this.ledger.GetAsync(rollId, token).ConfigureAwait(true) ?? this.roll;
                    this.templateNotices = this.templateNotices.Where(notice => !notice.CanAdopt).ToArray();
                    await LoadPlanAsync(created.PlanId, token).ConfigureAwait(true);
                    ResetOptions();
                    this.suppressEvaluate = true;
                    StockText = DefaultStockMm().ToString("F3", CultureInfo.CurrentCulture);
                    this.suppressEvaluate = false;
                    Evaluate();
                    Say("Job_UsedNewTemplateFormat", created.PlanNo, created.Name);
                },
                CancellationToken.None),
            string.Join("、", adoptable.Select(notice => notice.TemplateName)));
    }

    /// <summary>第 6 格"全部恢复设定值…"：问一句（不超时）。</summary>
    private void AskRestoreAll() => Ask(
        "Adjust_AskRestoreAllFormat",
        () =>
        {
            foreach (AdjustRowViewModel row in AdjustRows)
            {
                row.Restore();
            }
        },
        AdjustRows.Count(row => row.IsChanged));

    /// <summary>方案列表（这支辊）。</summary>
    private void OpenPlans()
    {
        this.draft.PlanRollId = this.roll?.RollId;
        Navigator.StartTask(PageKey.Plans, PageKey.Job);
    }

    // ───────────── 竖键 ─────────────

    private void ApplyKeys()
    {
        if (IsPicking)
        {
            SetVerticalKeys(new FunctionKeyViewModel?[] { FunctionKeyViewModel.ForAction("Vk_ThisRollPlans", Localizer, () => { }) });
            SetCommitPair(new FunctionKeyViewModel("Vk_Cancel", new RelayCommand(() => TryDismissPrompt()), Localizer, FunctionKeyKind.Cancel), this.pickKey);
            return;
        }

        if (IsAdjusting)
        {
            SetVerticalKeys(new FunctionKeyViewModel?[]
            {
                new FunctionKeyViewModel("Vk_LoadLastAdjust", new RelayCommand(LoadLastAdjustments, () => AdjustRows.Any(row => row.HasLast)), Localizer, requiresEditable: true)
                {
                    PreconditionResourceKey = "Adjust_NoLast",
                },
                new FunctionKeyViewModel("Vk_RestoreOne", new RelayCommand(() => SelectedAdjustRow?.Restore(), () => SelectedAdjustRow is { IsChanged: true }), Localizer, requiresEditable: true)
                {
                    PreconditionResourceKey = "Adjust_NotChanged",
                },
                null,
                null,
                null,
                new FunctionKeyViewModel("Vk_RestoreAll", new RelayCommand(() => AskRestoreAll(), () => AdjustRows.Any(row => row.IsChanged)), Localizer, requiresEditable: true)
                {
                    PreconditionResourceKey = "Adjust_NotChanged",
                },
            });
            SetCommitPair(
                new FunctionKeyViewModel("Vk_Cancel", new RelayCommand(CloseAdjust), Localizer, FunctionKeyKind.Cancel),
                new FunctionKeyViewModel("Vk_UseThisTime", new RelayCommand(CommitAdjust, () => !AdjustRows.Any(row => row.IsRefused)), Localizer, FunctionKeyKind.Confirm, requiresEditable: true)
                {
                    PreconditionResourceKey = "Adjust_HasRefused",
                });
            return;
        }

        if (this.choosing)
        {
            // 第 7 / 8 格由确认服务给（取消 / 仅本次）；第 6 格"改为计划"。
            SetVerticalKeys(new FunctionKeyViewModel?[] { null, null, null, null, null, CanMakePlan ? this.makePlanKey : null });
            return;
        }

        if (IsReview)
        {
            // 设计稿 v4 I7 / 5.2.2：1 方案 ▸ · 2 本次调整 ▸ · 5 示教起点 Z · 6 改用新模板…（有更新时才亮）· 7 取消 · 8 确认下发。
            SetVerticalKeys(new FunctionKeyViewModel?[]
            {
                new FunctionKeyViewModel("Vk_PlanMenu", new RelayCommand(OpenPlanMenu), Localizer),
                new FunctionKeyViewModel("Vk_AdjustThisTime", new RelayCommand(OpenAdjust, () => this.plan is not null && this.adjustable.Any(row => row.Adjustable)), Localizer, requiresEditable: true)
                {
                    PreconditionResourceKey = "Job_NothingSelected",
                    LabelResourceKey = this.adjustments.Count > 0 ? "Vk_AdjustThisTimeChanged" : "Vk_AdjustThisTime",
                },
                null,
                null,
                UsesGrindStart
                    ? new FunctionKeyViewModel("Vk_TeachGrindStart", TeachGrindStartCommand, Localizer, requiresEditable: true)
                    : null,
                UseNewTemplateKey(),
            });
            // 第 7 / 8 格一直是"✕ 返回清单 / ✓ 确认下发"：核对有拦的时 ✓ 灰着，按了在对话行说哪一项没过。
            SetCommitPair(this.cancelReviewKey, this.confirmDownloadKey);
            return;
        }

        bool interrupted = SelectedQueueRow?.Entry.Mark == QueueMark.Interrupted;
        SetVerticalKeys(new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_ToJob", OpenReviewForSelectedCommand, Localizer)
            {
                PreconditionResourceKey = "Job_NothingSelected",
                LabelResourceKey = interrupted ? "Vk_ResumeJob" : SelectedQueueRow?.Entry.Mark == QueueMark.Regrind ? "Vk_RegrindJob" : "Vk_ToJob",
            },
            new FunctionKeyViewModel("Vk_RegisterRoll", new RelayCommand(RegisterRoll), Localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_EndInterrupted", new RelayCommand(AskEndInterrupted, () => interrupted), Localizer, requiresEditable: true)
            {
                PreconditionResourceKey = "Job_NotInterrupted",
            },
            FunctionKeyViewModel.ForAction("Vk_GoRolls", Localizer, () => Navigator.GoTo(PageKey.Rolls)),
        });
        SetCommitPair(null, null);
    }

    private async Task DownloadAsync()
    {
        await RunGuardedAsync(async token =>
        {
            GrindingJob? job = BuildJob();
            if (job is null || this.result is null)
            {
                return;
            }

            string user = this.session.CurrentUser?.UserName ?? string.Empty;
            JobDownloadResult download = await this.downloadService.DownloadAsync(job, this.result.Checks, user, token).ConfigureAwait(true);
            if (!download.Succeeded)
            {
                foreach (ParameterViolation violation in download.Violations)
                {
                    Checks.Insert(0, ViolationCheck(violation));
                }

                foreach (string missing in download.MissingTags)
                {
                    Alarms.Raise(AlarmSeverity.Error, "Alarm_TagMissing", missing);
                }

                Interaction.Fail(Localizer[download.MissingTags.Count > 0 ? "Job_TagMapIncomplete" : "Job_ValidationFailed"]);
                return;
            }

            // 计划改了写回台账；推断来的计划经人确认。
            await this.planning.AfterDownloadAsync(job, user, token).ConfigureAwait(true);

            // "标准已更新"提示过、这次没改用：同一版不再提示（设计说明 4.6）。
            await this.templates.DeclineAsync(this.templateNotices, token).ConfigureAwait(true);
            Say("Job_HandedOver");

            // Q3 / M7：下发成功就请 NC 切 AUTO、画面转到自动磨削，操作员在按钮板上按循环启动。
            ManualCommandResult mode = await this.manualGrinding.RequestModeAsync(MachineModeRequest.Auto, token).ConfigureAwait(true);
            if (!mode.Succeeded && mode.Outcome != ManualCommandOutcome.NotMapped)
            {
                Alarms.Raise(AlarmSeverity.Warning, mode.ReasonResourceKey!, detail: null, code: AlarmCodes.Unspecified);
            }

            this.roll = null;
            CanDownload = false;
            JobId = NewJobId();
            Navigator.CloseSubView();
            Navigator.GoTo(PageKey.AutoGrinding);
        }, CancellationToken.None).ConfigureAwait(true);
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string NewJobId() =>
        string.Create(CultureInfo.InvariantCulture, $"J{DateTimeOffset.Now:yyyyMMddHHmmss}");
}
