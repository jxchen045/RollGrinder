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
using RollGrinder.Services.Manual;
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
    private readonly FunctionKeyViewModel cancelReviewKey;
    private readonly FunctionKeyViewModel confirmDownloadKey;
    private readonly FunctionKeyViewModel pickKey;
    private readonly FunctionKeyViewModel makePlanKey;
    private readonly AsyncRelayCommand downloadCommand;

    private RollRecord? roll;
    private RollProfileDefinition? profile;
    private GrindingProgram? program;
    private JobCheckResult? result;
    private PlanPickKind pickKind;
    private JobDeviation profileDeviation;
    private JobDeviation programDeviation;
    private bool startMeasured;
    private bool requireMeasure;
    private bool suppressEvaluate;
    private bool choosing;
    private bool makePlanChosen;
    private string? regrindOf;

    public JobViewModel(
        IRollPlanningService planning,
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
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.planning = planning ?? throw new ArgumentNullException(nameof(planning));
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

        ProgramOptions = new ObservableCollection<ProgramOptionRowViewModel>(
            ProgramOptionCatalog.All.Select(option => new ProgramOptionRowViewModel(
                option, option.DefaultEnabled, capability.Supports(option), localizer)));
        foreach (ProgramOptionRowViewModel row in ProgramOptions)
        {
            row.PropertyChanged += (_, _) => Evaluate();
        }

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

    public override bool HasModalPrompt => IsPicking;

    public override bool TryDismissPrompt()
    {
        if (!IsPicking)
        {
            return false;
        }

        IsPicking = false;
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
        Dictionary<string, RollProfileSummary> profileNames = (await this.profiles.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(true))
            .ToDictionary(entry => entry.ProfileId, StringComparer.Ordinal);

        this.allRows.Clear();
        foreach (QueueEntry entry in entries)
        {
            RollRecord r = entry.Roll;
            string plan = r.TargetProfileId is { } id && profileNames.TryGetValue(id, out RollProfileSummary? summary)
                ? Localizer.Format("Lib_NameVersionFormat", summary.Name, summary.Version)
                : Localizer["Job_NoPlan"];
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

    public JobDeviation Deviation =>
        this.profileDeviation == JobDeviation.ThisTimeOnly || this.programDeviation == JobDeviation.ThisTimeOnly ? JobDeviation.ThisTimeOnly
        : this.profileDeviation == JobDeviation.PlanChanged || this.programDeviation == JobDeviation.PlanChanged ? JobDeviation.PlanChanged
        : JobDeviation.None;

    public IAsyncRelayCommand DownloadCommand => this.downloadCommand;

    /// <summary>计划与这一次：台账计划、这一次用的、与计划的关系。</summary>
    public ObservableCollection<LabelValueViewModel> PlanRows { get; } = new();

    public ObservableCollection<CheckRowViewModel> Checks { get; } = new();

    public ObservableCollection<JobProgramStepRowViewModel> ProgramSteps { get; } = new();

    /// <summary>这一次的程序步骤开关，默认值来自程序。</summary>
    public ObservableCollection<ProgramOptionRowViewModel> ProgramOptions { get; }

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

        string? profileId = this.roll.TargetProfileId;
        string? programId = this.roll.ProgramId;
        this.requireMeasure = false;
        this.regrindOf = null;
        JobId = NewJobId();
        this.profileDeviation = JobDeviation.None;
        this.programDeviation = JobDeviation.None;
        DeviationReason = string.Empty;

        if (row.Entry.JobId is { } storedId && await this.jobs.GetAsync(storedId, cancellationToken).ConfigureAwait(true) is { } stored)
        {
            JobId = storedId;
            profileId = stored.Job.ProfileId ?? profileId;
            programId = stored.Job.ProgramId ?? programId;
            this.regrindOf = stored.Job.RegrindOfJobId;
            this.requireMeasure = row.Entry.Mark == QueueMark.Interrupted;
            this.profileDeviation = profileId == this.roll.TargetProfileId ? JobDeviation.None : stored.Job.Deviation;
            this.programDeviation = programId == this.roll.ProgramId ? JobDeviation.None : stored.Job.Deviation;
            DeviationReason = stored.Job.DeviationReason ?? string.Empty;
        }

        this.profile = profileId is null ? null : await this.profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(true);
        this.program = programId is null ? null : await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(true);
        ResetOptions();

        this.suppressEvaluate = true;
        this.startMeasured = false;
        StartDiameterText = this.requireMeasure ? string.Empty : this.roll.StartDiameterMm.ToString("F3", CultureInfo.CurrentCulture);
        StockText = DefaultStockMm().ToString("F3", CultureInfo.CurrentCulture);
        this.suppressEvaluate = false;

        ReviewTitle = Localizer.Format("Job_ReviewTitleFormat", this.roll.RollId, Localizer["RollKind_" + this.roll.Kind], this.roll.Purpose ?? "--");
        Navigator.OpenSubView(ReviewSubView);
        Evaluate();
        if (this.requireMeasure)
        {
            Interaction.Hint(Localizer["Job_MeasureAfterInterrupt"]);
        }
    }

    /// <summary>从辊形区 / 工艺区改完回来：重读这一次用的辊形与程序（版本可能 +1 了）。</summary>
    private async Task ReloadChoicesAsync(CancellationToken cancellationToken)
    {
        if (this.roll is null)
        {
            return;
        }

        this.roll = await this.ledger.GetAsync(this.roll.RollId, cancellationToken).ConfigureAwait(true) ?? this.roll;
        if (this.profile is not null)
        {
            this.profile = await this.profiles.GetAsync(this.profile.ProfileId, cancellationToken).ConfigureAwait(true);
        }

        if (this.program is not null)
        {
            this.program = await this.programs.GetAsync(this.program.ProgramId, cancellationToken).ConfigureAwait(true);
        }

        Evaluate();
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

        this.suppressEvaluate = true;
        foreach (ProgramOptionRowViewModel row in ProgramOptions)
        {
            row.IsOn = row.IsAvailable && this.program.IsProgramOptionEnabled(row.Descriptor.Key);
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
        JobCheckResult checks = JobChecklist.Evaluate(
            new JobCheckInput(this.roll, this.profile, normalized, start, this.startMeasured, stockMm * 1000.0, Deviation, NullIfBlank(DeviationReason)),
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

        PlanRows.Add(new LabelValueViewModel("Job_PlanProfile", this.profile is null ? "--" : NameVersion(this.profile.Name, this.profile.Version, this.profileDeviation), Localizer));
        PlanRows.Add(new LabelValueViewModel("Job_PlanProgram", this.program is null ? "--" : NameVersion(this.program.Name, this.program.Version, this.programDeviation), Localizer));
        PlanRows.Add(new LabelValueViewModel("Job_Deviation", Deviation switch
        {
            JobDeviation.ThisTimeOnly => Localizer.Format("Job_DeviationThisTimeFormat", string.IsNullOrWhiteSpace(DeviationReason) ? Localizer["Job_ReasonMissing"] : ReasonText(DeviationReason)),
            JobDeviation.PlanChanged => Localizer["Job_DeviationPlanChanged"],
            _ => Localizer["Job_DeviationNone"],
        }, Localizer));
        PlanRows.Add(new LabelValueViewModel("Job_JobIdLabel", JobId + (this.regrindOf is { } of ? " " + Localizer.Format("Job_RegrindOfFormat", of) : string.Empty), Localizer));
    }

    private string NameVersion(string name, int version, JobDeviation deviation) =>
        Localizer.Format("Lib_NameVersionFormat", name, version)
        + (deviation == JobDeviation.ThisTimeOnly ? " · " + Localizer["Mark_ThisTime"]
            : deviation == JobDeviation.PlanChanged ? " · " + Localizer["Mark_PlanChanged"]
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
                step.Order.ToString(CultureInfo.InvariantCulture), Localizer["StepType_" + step.StepTypeKey], duration));
        }

        TotalDurationText = Localizer.Format("Steps_TotalTimeFormat", (int)total.TotalMinutes);
    }

    /// <summary>按核对结果拼出这份作业；辊形套不上（长度差太多）返回 null。</summary>
    private GrindingJob? BuildJob(JobCheckResult checks, GrindingProgram normalized, double startMm, double stockMicrometer)
    {
        if (this.roll is null || this.profile is null || checks.Fit.Profile is not { } fitted)
        {
            return null;
        }

        ParameterSet options = new(ProgramOptions.Select(row =>
            new KeyValuePair<string, ParameterValue>(row.Descriptor.Key, ParameterValue.FromBoolean(row.IsOn))));
        IReadOnlyList<GrindingJobStep> steps = checks.Stock.ProblemResourceKey is null ? checks.Stock.Steps : normalized.Steps;

        return GrindingJob.Create(JobId, this.roll.RollId, RollGeometry.FromDiameter(this.roll.Geometry.BodyLengthMm, startMm), fitted, steps, options) with
        {
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
        };
    }

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

    // ───────────── 换辊形 / 换程序 ─────────────

    private bool CanMakePlan => !this.settings.PlanChangeNeedsAdministrator || this.session.Can(Permission.EditRollPlans);

    private async Task OpenPickerAsync(PlanPickKind kind)
    {
        if (this.roll is null)
        {
            return;
        }

        this.pickKind = kind;
        string? current = kind == PlanPickKind.Profile ? this.profile?.ProfileId : this.program?.ProgramId;
        await RunGuardedAsync(
            token => Picker.LoadAsync(kind, this.roll.Geometry.BodyLengthMm, this.roll.Kind, current, token),
            CancellationToken.None).ConfigureAwait(true);
        IsPicking = true;
        ApplyKeys();
    }

    /// <summary>
    /// 选好了：与计划一样（或这支辊还没有计划）直接用；与计划不同问"仅本次 / 改为计划 / 取消"——
    /// 仅本次在第 8 格（安全的一边），改为计划在第 6 格，不超时（关系设计 O1）。
    /// </summary>
    private void ConfirmPick()
    {
        if (Picker.SelectedItem is not { } item || this.roll is null)
        {
            return;
        }

        IsPicking = false;
        PlanPickKind kind = this.pickKind;
        string? planId = kind == PlanPickKind.Profile ? this.roll.TargetProfileId : this.roll.ProgramId;
        if (planId is null || planId == item.Id)
        {
            _ = RunRefreshAsync(token => ApplyPickAsync(kind, item.Id, JobDeviation.None, token), CancellationToken.None);
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
                    await ApplyPickAsync(kind, item.Id, JobDeviation.ThisTimeOnly, token).ConfigureAwait(true);
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
                    _ = RunRefreshAsync(token => ApplyPickAsync(kind, item.Id, JobDeviation.PlanChanged, token), CancellationToken.None);
                }

                ApplyKeys();
            });
    }

    /// <summary>第 6 格"改为计划"：收掉待答的问题，按改为计划处理（下发后写回台账）。</summary>
    private void ChooseMakePlan()
    {
        if (!this.choosing || !CanMakePlan)
        {
            return;
        }

        this.makePlanChosen = true;
        Interaction.Confirmations.Cancel();
    }

    private async Task ApplyPickAsync(PlanPickKind kind, string id, JobDeviation deviation, CancellationToken cancellationToken)
    {
        if (kind == PlanPickKind.Profile)
        {
            this.profile = await this.profiles.GetAsync(id, cancellationToken).ConfigureAwait(true);
            this.profileDeviation = deviation;
        }
        else
        {
            this.program = await this.programs.GetAsync(id, cancellationToken).ConfigureAwait(true);
            this.programDeviation = deviation;
            ResetOptions();
            this.suppressEvaluate = true;
            StockText = DefaultStockMm().ToString("F3", CultureInfo.CurrentCulture);
            this.suppressEvaluate = false;
        }

        if (Deviation != JobDeviation.ThisTimeOnly)
        {
            DeviationReason = string.Empty;
        }

        Evaluate();
    }

    /// <summary>回到计划：这一次照台账的辊形与程序。</summary>
    private void RestorePlan()
    {
        if (this.roll is null)
        {
            return;
        }

        _ = RunGuardedAsync(
            async token =>
            {
                this.profileDeviation = JobDeviation.None;
                this.programDeviation = JobDeviation.None;
                this.profile = this.roll.TargetProfileId is { } p ? await this.profiles.GetAsync(p, token).ConfigureAwait(true) : null;
                this.program = this.roll.ProgramId is { } g ? await this.programs.GetAsync(g, token).ConfigureAwait(true) : null;
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

    private void OpenProfile()
    {
        this.draft.ProfileToOpen = this.profile?.ProfileId;
        Navigator.StartTask(PageKey.Profile, PageKey.Job);
    }

    private void OpenProgram()
    {
        this.draft.ProgramToOpen = this.program?.ProgramId;
        Navigator.StartTask(PageKey.Steps, PageKey.Job);
    }

    // ───────────── 竖键 ─────────────

    private void ApplyKeys()
    {
        if (IsPicking)
        {
            SetVerticalKeys(new FunctionKeyViewModel?[]
            {
                FunctionKeyViewModel.ForAction(Picker.ShowAll ? "Vk_ShowFitting" : "Vk_ShowAll", Localizer,
                    () => _ = RunGuardedAsync(Picker.ToggleShowAllAsync, CancellationToken.None)),
            });
            SetCommitPair(new FunctionKeyViewModel("Vk_Cancel", new RelayCommand(() => TryDismissPrompt()), Localizer, FunctionKeyKind.Cancel), this.pickKey);
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
            SetVerticalKeys(new FunctionKeyViewModel?[]
            {
                new FunctionKeyViewModel("Vk_ChangeProfile", new RelayCommand(() => _ = OpenPickerAsync(PlanPickKind.Profile)), Localizer, requiresEditable: true),
                new FunctionKeyViewModel("Vk_ChangeProgram", new RelayCommand(() => _ = OpenPickerAsync(PlanPickKind.Program)), Localizer, requiresEditable: true),
                new FunctionKeyViewModel("Vk_Reason", new RelayCommand(OpenReasonMenu, () => Deviation == JobDeviation.ThisTimeOnly), Localizer, requiresEditable: true)
                {
                    PreconditionResourceKey = "Job_ReasonOnlyThisTime",
                },
                new FunctionKeyViewModel("Vk_RestorePlan", new RelayCommand(RestorePlan, () => Deviation != JobDeviation.None), Localizer, requiresEditable: true)
                {
                    PreconditionResourceKey = "Job_AlreadyPlan",
                },
                new FunctionKeyViewModel("Vk_OpenProfile", new RelayCommand(OpenProfile, () => this.profile is not null), Localizer)
                {
                    PreconditionResourceKey = "Job_NothingSelected",
                },
                new FunctionKeyViewModel("Vk_OpenProgram", new RelayCommand(OpenProgram, () => this.program is not null), Localizer)
                {
                    PreconditionResourceKey = "Job_NothingSelected",
                },
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
