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
using RollGrinder.Services.Manual;
using RollGrinder.Services.Session;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Records;

namespace RollGrinder.App.ViewModels;

/// <summary>作业向导左边的一步。</summary>
public sealed partial class JobStepItemViewModel : ObservableObject
{
    public JobStepItemViewModel(int number, string title)
    {
        Number = number;
        NumberText = number.ToString(CultureInfo.InvariantCulture);
        Title = title;
    }

    public int Number { get; }

    public string NumberText { get; }

    public string Title { get; }

    /// <summary>这一步选了什么（辊号、辊形名……）；还没选时为空。</summary>
    [ObservableProperty]
    private string summary = string.Empty;

    [ObservableProperty]
    private bool isCurrent;

    [ObservableProperty]
    private bool isDone;
}

/// <summary>作业里那支程序的一道工序（名字与按这支辊估的时长）。</summary>
/// <param name="OrderText">第几道。</param>
/// <param name="Name">工序名。</param>
/// <param name="DurationText">预计时长。</param>
public sealed record JobProgramStepRowViewModel(string OrderText, string Name, string DurationText);

/// <summary>
/// 作业（阶段 1，修改稿 5.4）：按步骤把一支辊的活拼起来——
/// ① 轧辊（台账里选）→ ② 辊形（库里选，设计长度与辊身长度对不上时让人选拉伸或居中）
/// → ③ 工艺程序（库里选）→ ④ 程序步骤（这一次的开关）→ ⑤ 核对并下发。
/// 有任何错误时"下发 NC"按不下去；下发成功自动切到自动加工页（问题 Q3 的决定）。
///
/// 作业只做"组合 + 下发"：尺寸在台账里改，辊形在辊形页改，工序在工艺程序页改。
/// </summary>
public sealed partial class JobViewModel : PageViewModelBase
{
    public const int RollStep = 1;
    public const int ProfileStep = 2;
    public const int ProgramStep = 3;
    public const int OptionsStep = 4;
    public const int ReviewStep = 5;

    private const int ListLimit = 500;

    private readonly IRecordService recordService;
    private readonly IRollLedgerService ledger;
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IJobDownloadService downloadService;
    private readonly GrindingJobValidator validator;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly MachineCapability capability;
    private readonly HmiSettings settings;
    private readonly JobDraft draft;
    private readonly IManualGrindingService manualGrinding;
    private readonly FunctionKeyViewModel cancelReviewKey;
    private readonly FunctionKeyViewModel confirmDownloadKey;
    private readonly RelayCommand nextCommand;
    private readonly RelayCommand previousCommand;
    private readonly AsyncRelayCommand downloadCommand;

    private RollRecord? roll;
    private RollProfileDefinition? profile;
    private GrindingProgram? program;

    public JobViewModel(
        IRecordService recordService,
        IRollLedgerService ledger,
        IRollProfileRepository profiles,
        IProgramRepository programs,
        IJobDownloadService downloadService,
        GrindingJobValidator validator,
        GrindingStepTypeRegistry stepTypes,
        RollProfileTypeRegistry profileTypes,
        MachineCapability capability,
        HmiSettings settings,
        JobDraft draft,
        IManualGrindingService manualGrinding,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.capability = capability ?? throw new ArgumentNullException(nameof(capability));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.draft = draft ?? throw new ArgumentNullException(nameof(draft));
        this.manualGrinding = manualGrinding ?? throw new ArgumentNullException(nameof(manualGrinding));

        StepItems = new ObservableCollection<JobStepItemViewModel>(new[]
        {
            new JobStepItemViewModel(RollStep, localizer["Job_StepRoll"]),
            new JobStepItemViewModel(ProfileStep, localizer["Job_StepProfile"]),
            new JobStepItemViewModel(ProgramStep, localizer["Job_StepProgram"]),
            new JobStepItemViewModel(OptionsStep, localizer["Job_StepOptions"]),
            new JobStepItemViewModel(ReviewStep, localizer["Job_StepReview"]),
        });

        ProgramOptions = new ObservableCollection<ProgramOptionRowViewModel>(
            ProgramOptionCatalog.All.Select(option => new ProgramOptionRowViewModel(
                option, option.DefaultEnabled, capability.Supports(option), localizer)));
        foreach (ProgramOptionRowViewModel row in ProgramOptions)
        {
            row.PropertyChanged += (_, _) => RefreshStepSummaries();
        }

        this.previousCommand = new RelayCommand(() => ActiveStep--, () => ActiveStep > RollStep);
        this.nextCommand = new RelayCommand(GoNext, CanGoNext);
        this.downloadCommand = new AsyncRelayCommand(DownloadAsync, () => ActiveStep == ReviewStep && CanDownload);
        this.jobId = NewJobId();

        // 横键：机床区 JOG 那一排（最终稿 5.1），"作业"青底。
        SetFunctionKeys(MachineAreaKeys.Create(Navigator, localizer, MachineAreaKeys.Job));

        // 竖键（最终稿 5.6）：上一步 · 下一步 · 新登记轧辊 · 打开辊形 · 打开程序 · 空 · 取消作业…；
        // 第 5 步核对全部通过时第 7 / 8 格是"✕ 取消 / ✓ 确认下发"——确认下发就是那一次确认，不再多问。
        var previous = new FunctionKeyViewModel("Vk_PreviousStep", this.previousCommand, localizer);
        var openProfile = new FunctionKeyViewModel("Vk_OpenProfile", OpenProfileCommand, localizer) { PreconditionResourceKey = "Job_NothingSelected" };
        var openProgram = new FunctionKeyViewModel("Vk_OpenProgram", OpenProgramCommand, localizer) { PreconditionResourceKey = "Job_NothingSelected" };
        var cancelJob = new FunctionKeyViewModel("Vk_CancelJob", new RelayCommand(AskCancelJob), localizer, requiresEditable: true);
        this.stepKeys = new FunctionKeyViewModel?[]
        {
            previous,
            new FunctionKeyViewModel("Vk_NextStep", this.nextCommand, localizer) { PreconditionResourceKey = "Job_NextNeedsSelection" },
            new FunctionKeyViewModel("Vk_RegisterRoll", RegisterRollCommand, localizer, requiresEditable: true),
            openProfile,
            openProgram,
            null,
            cancelJob,
        };

        // 第 5 步核对：没有"下一步"，"取消作业…"挪到第 6 格，7 / 8 留给"✕ 取消 / ✓ 确认下发"（最终稿 5.6）。
        this.reviewKeys = new FunctionKeyViewModel?[] { previous, openProfile, openProgram, null, null, cancelJob };
        SetVerticalKeys(this.stepKeys);

        this.cancelReviewKey = new FunctionKeyViewModel("Vk_Cancel", this.previousCommand, localizer, FunctionKeyKind.Cancel);

        // 下发是唯一的写机床通道；自动循环挂着程序时锁掉，免得把运行中的程序改了。
        this.confirmDownloadKey = new FunctionKeyViewModel("Vk_ConfirmDownload", this.downloadCommand, localizer, FunctionKeyKind.Confirm, requiresEditable: true)
        {
            IsMachineCommand = true,
        };

        RefreshStepItems();
    }

    public override PageKey Key => PageKey.Job;

    public override string TitleResourceKey => "Page_Job";

    /// <summary>自动循环挂着程序时落只读锁：正在磨的那支辊不能被换掉。</summary>
    public override bool LocksDuringRun => true;

    /// <summary>建作业、登记轧辊、下发：操作者就能做（Q9）。</summary>
    public override Permission? EditPermission => Permission.EditJobs;

    /// <summary>离线也能拼作业、核对；只有下发要机床。</summary>
    public override bool WorksOffline => true;

    public ObservableCollection<JobStepItemViewModel> StepItems { get; }

    public ObservableCollection<RollLedgerRowViewModel> Rolls { get; } = new();

    public ObservableCollection<RollProfileSummary> Profiles { get; } = new();

    public ObservableCollection<ProgramSummary> Programs { get; } = new();

    /// <summary>选中那支程序的工序（按选中的辊估时长）。</summary>
    public ObservableCollection<JobProgramStepRowViewModel> ProgramSteps { get; } = new();

    /// <summary>这一次的程序步骤开关，默认值来自选中的程序。</summary>
    public ObservableCollection<ProgramOptionRowViewModel> ProgramOptions { get; }

    /// <summary>核对页：轧辊、辊形与长度核对、程序、时长……</summary>
    public ObservableCollection<LabelValueViewModel> ReviewRows { get; } = new();

    public ObservableCollection<ViolationRowViewModel> Violations { get; } = new();

    /// <summary>最近一次刷新列表的任务（进页时开始）。自检等它读完再往下走。</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private string jobId;

    /// <summary>当前在第几步（1–5）。</summary>
    [ObservableProperty]
    private int activeStep = RollStep;

    [ObservableProperty]
    private RollLedgerRowViewModel? selectedRoll;

    [ObservableProperty]
    private RollProfileSummary? selectedProfile;

    [ObservableProperty]
    private ProgramSummary? selectedProgram;

    /// <summary>选中那支辊的尺寸（辊身、直径、类型）。</summary>
    [ObservableProperty]
    private string rollSummaryText = string.Empty;

    /// <summary>辊形设计长度与辊身长度对不上，要人选拉伸还是居中。</summary>
    [ObservableProperty]
    private bool lengthMismatch;

    /// <summary>对不上时选的处理方式；还没选为 null。</summary>
    [ObservableProperty]
    private ProfileFitMode? fitMode;

    /// <summary>长度核对的结果说明。</summary>
    [ObservableProperty]
    private string lengthCheckText = string.Empty;

    [ObservableProperty]
    private string totalDurationText = "--";

    /// <summary>核对通过，可以下发。</summary>
    [ObservableProperty]
    private bool canDownload;

    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : Localizer[StatusResourceKey];

    public bool IsStretchChosen => FitMode == ProfileFitMode.Stretch;

    public bool IsCenterChosen => FitMode == ProfileFitMode.CenterAlign;

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnActiveStepChanged(int value)
    {
        if (value == ReviewStep)
        {
            Review();
        }

        RefreshStepItems();
    }

    partial void OnCanDownloadChanged(bool value) => RefreshCommitPair();

    private readonly IReadOnlyList<FunctionKeyViewModel?> stepKeys;

    private readonly IReadOnlyList<FunctionKeyViewModel?> reviewKeys;

    private bool showingReviewKeys;

    /// <summary>第 5 步、核对通过：竖键 7 / 8 = 取消 / 确认下发。第 5 步的竖键另排一排。</summary>
    private void RefreshCommitPair()
    {
        bool review = ActiveStep == ReviewStep;
        if (review != this.showingReviewKeys)
        {
            this.showingReviewKeys = review;
            SetVerticalKeys(review ? this.reviewKeys : this.stepKeys);
        }

        bool ready = review && CanDownload;
        SetCommitPair(ready ? this.cancelReviewKey : null, ready ? this.confirmDownloadKey : null);
    }

    /// <summary>打开辊形：到辊形区把选中的辊形打开来改，改完"« 返回"回到作业。</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private void OpenProfile()
    {
        this.draft.ProfileToOpen = SelectedProfile?.ProfileId;
        Navigator.StartTask(PageKey.Profile, PageKey.Job);
    }

    /// <summary>打开程序：到工艺区把选中的程序打开来改。</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedProgram))]
    private void OpenProgram()
    {
        this.draft.ProgramToOpen = SelectedProgram?.ProgramId;
        Navigator.StartTask(PageKey.Steps, PageKey.Job);
    }

    private bool HasSelectedProfile() => SelectedProfile is not null;

    private bool HasSelectedProgram() => SelectedProgram is not null;

    /// <summary>取消作业…：问一句，清掉所有选择从第 ① 步重来。</summary>
    private void AskCancelJob() => Ask("Job_AskCancel", NewJob);

    partial void OnSelectedRollChanged(RollLedgerRowViewModel? value) =>
        _ = RunGuardedAsync(token => LoadRollAsync(value?.RollId, token), CancellationToken.None);

    partial void OnSelectedProfileChanged(RollProfileSummary? value)
    {
        OpenProfileCommand.NotifyCanExecuteChanged();
        _ = RunGuardedAsync(token => LoadProfileAsync(value?.ProfileId, token), CancellationToken.None);
    }

    partial void OnSelectedProgramChanged(ProgramSummary? value)
    {
        OpenProgramCommand.NotifyCanExecuteChanged();
        _ = RunGuardedAsync(token => LoadProgramAsync(value?.ProgramId, token), CancellationToken.None);
    }

    partial void OnFitModeChanged(ProfileFitMode? value)
    {
        OnPropertyChanged(nameof(IsStretchChosen));
        OnPropertyChanged(nameof(IsCenterChosen));
        RefreshLengthCheck();
        RefreshStepItems();
    }

    /// <summary>进页：重读台账、辊形库、程序库；带过来的程序、刚登记的辊直接选上。</summary>
    public override void OnActivated() => Loading = RunGuardedAsync(RefreshListsAsync, CancellationToken.None);

    private async Task RefreshListsAsync(CancellationToken cancellationToken)
    {
        string? keepRoll = this.draft.RegisteredRollId ?? SelectedRoll?.RollId;
        string? keepProfile = this.draft.PendingProfileId ?? SelectedProfile?.ProfileId;
        this.draft.PendingProfileId = null;
        string? keepProgram = this.draft.PendingProgramId ?? SelectedProgram?.ProgramId;
        this.draft.RegisteredRollId = null;
        this.draft.PendingProgramId = null;

        Rolls.Clear();
        foreach (RollLedgerRow row in await this.recordService.LoadLedgerAsync(ListLimit, cancellationToken).ConfigureAwait(true))
        {
            Rolls.Add(new RollLedgerRowViewModel(row, KindLabel(row.Kind)));
        }

        Profiles.Clear();
        foreach (RollProfileSummary entry in await this.profiles.ListAsync(ListLimit, cancellationToken).ConfigureAwait(true))
        {
            Profiles.Add(entry);
        }

        Programs.Clear();
        foreach (ProgramSummary entry in await this.programs.ListAsync(ListLimit, cancellationToken).ConfigureAwait(true))
        {
            Programs.Add(entry);
        }

        // 列表换了一批新对象：按标识重新选上，并等它们各自读完，核对页才拿得到完整的数据。
        SelectedRoll = Rolls.FirstOrDefault(row => row.RollId == keepRoll);
        SelectedProfile = Profiles.FirstOrDefault(entry => entry.ProfileId == keepProfile);
        SelectedProgram = Programs.FirstOrDefault(entry => entry.ProgramId == keepProgram);
        await LoadRollAsync(SelectedRoll?.RollId, cancellationToken).ConfigureAwait(true);
        await LoadProfileAsync(SelectedProfile?.ProfileId, cancellationToken).ConfigureAwait(true);
        await LoadProgramAsync(SelectedProgram?.ProgramId, cancellationToken).ConfigureAwait(true);
    }

    private async Task LoadRollAsync(string? rollId, CancellationToken cancellationToken)
    {
        this.roll = rollId is null ? null : await this.ledger.GetAsync(rollId, cancellationToken).ConfigureAwait(true);
        RollSummaryText = this.roll is null
            ? string.Empty
            : Localizer.Format(
                "Job_RollSummaryFormat",
                this.roll.Geometry.BodyLengthMm,
                this.roll.Geometry.NominalDiameterMm,
                StartDiameterMm(this.roll));
        RefreshLengthCheck();
        ResetTargetDiameter();
        RefreshProgramSteps();
        RefreshStepItems();
    }

    private async Task LoadProfileAsync(string? profileId, CancellationToken cancellationToken)
    {
        bool changed = !string.Equals(this.profile?.ProfileId, profileId, StringComparison.Ordinal);
        this.profile = profileId is null ? null : await this.profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(true);
        if (changed)
        {
            // 换了一条辊形才要重新选拉伸 / 居中；从台账回来重读同一条时保留刚才的选择。
            FitMode = null;
        }

        RefreshLengthCheck();
        SortProgramsForProfile();
        RefreshProgramFit();
        RefreshStepItems();
    }

    /// <summary>③ 程序：关联到本辊形的程序排在前面（流程调整方案第 6 节）；选中项不变。</summary>
    private void SortProgramsForProfile()
    {
        string? profileId = this.profile?.ProfileId;
        ProgramSummary? keep = SelectedProgram;
        ProgramSummary[] ordered = Programs
            .OrderBy(entry => entry.ProfileId is not null && entry.ProfileId == profileId ? 0 : 1)
            .ThenByDescending(entry => entry.ModifiedAtUtc)
            .ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            int from = Programs.IndexOf(ordered[i]);
            if (from != i)
            {
                Programs.Move(from, i);
            }
        }

        SelectedProgram = keep;
    }

    /// <summary>程序与辊形对不对得上：关联的就是这条；关联别的但设计长度相同可以借用（提示）；长度不同不能用。</summary>
    [ObservableProperty]
    private string programFitText = string.Empty;

    private bool programFitBlocks;

    private void RefreshProgramFit()
    {
        programFitBlocks = false;
        this.nextCommand.NotifyCanExecuteChanged();
        if (this.program is null || this.profile is null)
        {
            ProgramFitText = string.Empty;
            return;
        }

        if (this.program.ProfileId is null)
        {
            ProgramFitText = Localizer["Job_ProgramNotLinked"];
            return;
        }

        if (this.program.ProfileId == this.profile.ProfileId)
        {
            ProgramFitText = Localizer["Job_ProgramLinkedHere"];
            return;
        }

        RollProfileSummary? linked = Profiles.FirstOrDefault(entry => entry.ProfileId == this.program.ProfileId);
        string linkedName = linked?.Name ?? this.program.ProfileId;
        programFitBlocks = linked is null || Math.Abs(linked.BodyLengthMm - this.profile.BodyLengthMm) > 0.5;
        ProgramFitText = Localizer.Format(programFitBlocks ? "Job_ProgramOtherLengthFormat" : "Job_ProgramBorrowedFormat", linkedName);
    }

    /// <summary>
    /// ④ 目标直径（mm）：默认 = 磨前直径 − 程序标准余量。本次余量 = 磨前 − 目标；
    /// 与程序不同时差额由粗磨吸收（<see cref="StockAdjustment"/>），精工序不动。
    /// </summary>
    [ObservableProperty]
    private string targetDiameterText = string.Empty;

    /// <summary>④ 本次余量的说明：本次 / 程序 / 粗磨调整后。</summary>
    [ObservableProperty]
    private string stockSummaryText = string.Empty;

    partial void OnTargetDiameterTextChanged(string value) => RefreshStock();

    private StockAdjustmentResult? stock;

    private void ResetTargetDiameter()
    {
        if (this.roll is null || this.program is null)
        {
            TargetDiameterText = string.Empty;
            return;
        }

        double programStock = this.program.StandardStockMicrometer
            ?? StockAdjustment.Apply(this.program.Steps, 1.0).ProgramStockMicrometer;
        TargetDiameterText = (StartDiameterMm(this.roll) - (programStock / 1000.0)).ToString("F3", CultureInfo.CurrentCulture);
        RefreshStock();
    }

    private void RefreshStock()
    {
        this.stock = null;
        StockSummaryText = string.Empty;
        this.nextCommand.NotifyCanExecuteChanged();
        if (this.roll is null || this.program is null
            || !double.TryParse(TargetDiameterText, NumberStyles.Float, CultureInfo.CurrentCulture, out double target))
        {
            return;
        }

        double actual = (StartDiameterMm(this.roll) - target) * 1000.0;
        this.stock = StockAdjustment.Apply(ProgramFrame.Normalize(this.program.Steps, this.stepTypes), actual);
        if (this.roll.ScrapDiameterMm is double scrap && target < scrap)
        {
            // 台账登记了报废直径：磨到比它小这支辊就废了，不下发。
            this.stock = this.stock with { ProblemResourceKey = "Stock_BelowScrap" };
        }

        StockSummaryText = this.stock.ProblemResourceKey is { } problem
            ? Localizer[problem]
            : Localizer.Format("Job_StockSummaryFormat", actual, this.stock.ProgramStockMicrometer, this.stock.RoughStockMicrometer);
        this.nextCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadProgramAsync(string? programId, CancellationToken cancellationToken)
    {
        this.program = programId is null ? null : await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(true);
        if (this.program is not null)
        {
            // 这一次的开关先照程序里的默认值来，再由人改。
            foreach (ProgramOptionRowViewModel row in ProgramOptions)
            {
                row.IsOn = row.IsAvailable && this.program.IsProgramOptionEnabled(row.Descriptor.Key);
            }
        }

        RefreshProgramFit();
        ResetTargetDiameter();
        RefreshProgramSteps();
        RefreshStepItems();
    }

    /// <summary>磨削起始直径：台账登记了当前直径就用它，没登记用公称直径。</summary>
    private static double StartDiameterMm(RollRecord roll) => roll.CurrentDiameterMm ?? roll.Geometry.NominalDiameterMm;

    /// <summary>辊形设计长度与辊身长度核对：一样长直接用；不一样要人选拉伸还是居中（不静默处理）。</summary>
    private void RefreshLengthCheck()
    {
        if (this.roll is null || this.profile is null)
        {
            LengthMismatch = false;
            LengthCheckText = string.Empty;
            return;
        }

        double design = this.profile.BodyLengthMm;
        double body = this.roll.Geometry.BodyLengthMm;
        LengthMismatch = !ProfileFitting.LengthsMatch(design, body);
        LengthCheckText = !LengthMismatch
            ? Localizer.Format("Job_LengthMatchFormat", design)
            : FitMode switch
            {
                ProfileFitMode.Stretch => Localizer.Format("Job_LengthStretchFormat", design, body),
                ProfileFitMode.CenterAlign => Localizer.Format("Job_LengthCenterFormat", design, body),
                _ => Localizer.Format("Job_LengthMismatchFormat", design, body),
            };
    }

    [RelayCommand]
    private void ChooseFit(ProfileFitMode mode) => FitMode = mode;

    private void RefreshProgramSteps()
    {
        ProgramSteps.Clear();
        TotalDurationText = "--";
        if (this.program is null)
        {
            return;
        }

        RollGeometry? geometry = this.roll is null ? null : JobGeometry(this.roll);
        TimeSpan total = TimeSpan.Zero;
        foreach (GrindingJobStep step in ProgramFrame.Normalize(this.program.Steps, this.stepTypes))
        {
            string duration = string.Empty;
            if (geometry is not null)
            {
                try
                {
                    TimeSpan estimate = this.stepTypes.Get(step.StepTypeKey).CreatePlan(geometry, step.Parameters).EstimateDuration(geometry);
                    total += estimate;
                    duration = estimate > TimeSpan.Zero ? Localizer.Format("Auto_StepDurationFormat", (int)estimate.TotalMinutes) : string.Empty;
                }
                catch (DomainException)
                {
                    duration = string.Empty;
                }
            }

            ProgramSteps.Add(new JobProgramStepRowViewModel(
                step.Order.ToString(CultureInfo.InvariantCulture), Localizer["StepType_" + step.StepTypeKey], duration));
        }

        if (geometry is not null)
        {
            TotalDurationText = Localizer.Format("Steps_TotalTimeFormat", (int)total.TotalMinutes);
        }
    }

    private static RollGeometry JobGeometry(RollRecord roll) =>
        RollGeometry.FromDiameter(roll.Geometry.BodyLengthMm, StartDiameterMm(roll));

    private bool CanGoNext() => ActiveStep switch
    {
        RollStep => this.roll is not null,
        ProfileStep => this.profile is not null && (!LengthMismatch || FitMode is not null),
        ProgramStep => this.program is not null && !programFitBlocks,
        OptionsStep => this.stock is { ProblemResourceKey: null },
        _ => false,
    };

    private void GoNext()
    {
        if (CanGoNext())
        {
            ActiveStep++;
        }
    }

    /// <summary>新作业：清掉所有选择，从第 ① 步重来。</summary>
    [RelayCommand]
    private void NewJob()
    {
        JobId = NewJobId();
        SelectedRoll = null;
        SelectedProfile = null;
        SelectedProgram = null;
        this.roll = null;
        this.profile = null;
        this.program = null;
        FitMode = null;
        Violations.Clear();
        ReviewRows.Clear();
        CanDownload = false;
        StatusResourceKey = string.Empty;
        ActiveStep = RollStep;
        RefreshLengthCheck();
        RefreshProgramSteps();
        RefreshStepItems();
    }

    /// <summary>台账里没有这支辊：派去库 › 轧辊台账新登记一支，登记完"« 返回"回来就选上它。</summary>
    [RelayCommand]
    private void RegisterRoll()
    {
        this.draft.RegisterNewRollRequested = true;
        Navigator.StartTask(PageKey.Library, PageKey.Job);
    }

    /// <summary>拼出这份作业；缺哪样返回 null。</summary>
    public GrindingJob? BuildJob()
    {
        if (this.roll is null || this.profile is null || this.program is null
            || (LengthMismatch && FitMode is null))
        {
            return null;
        }

        CompositeRollProfile fitted = LengthMismatch
            ? ProfileFitting.Fit(
                this.profile.Profile,
                this.profile.BodyLengthMm,
                this.roll.Geometry.BodyLengthMm,
                FitMode!.Value,
                this.profileTypes,
                this.settings.ProfileSampleCount)
            : this.profile.Profile;

        ParameterSet options = new(ProgramOptions.Select(row =>
            new KeyValuePair<string, ParameterValue>(row.Descriptor.Key, ParameterValue.FromBoolean(row.IsOn))));

        return GrindingJob.Create(
                JobId,
                this.roll.RollId,
                JobGeometry(this.roll),
                fitted,
                this.stock is { ProblemResourceKey: null } adjusted
                    ? adjusted.Steps
                    : ProgramFrame.Normalize(this.program.Steps, this.stepTypes),
                options) with
        {
            ProfileId = this.profile.ProfileId,
            ProfileName = this.profile.Name,
            ProgramId = this.program.ProgramId,
            ProgramName = this.program.Name,
        };
    }

    /// <summary>
    /// 第 5 步的"磨成什么样"（最终稿 5.6）：辊形按选好的拉伸 / 居中套在辊身上，横轴辊身坐标，纵轴直径量 µm。
    /// 下发之前就能看见会磨成什么样，把选错辊形、选错长度拦在下发之前。
    /// </summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> ReviewCurve { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>核对曲线变了（视图重画）。</summary>
    public event EventHandler? ReviewCurveChanged;

    private void RefreshReviewCurve(GrindingJob? job)
    {
        ReviewCurve = job is null
            ? Array.Empty<(double, double)>()
            : job.Profile.Compose(job.Geometry, this.profileTypes, this.settings.ProfileSampleCount).Points
                .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
                .ToArray();
        ReviewCurveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>核对页：列出这份作业的全部要点，跑下发前同一套校验；有错时"✓ 确认下发"不出现。</summary>
    private void Review()
    {
        ReviewRows.Clear();
        Violations.Clear();
        CanDownload = false;

        GrindingJob? job;
        try
        {
            job = BuildJob();
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
            job = null;
        }

        RefreshReviewCurve(job);
        if (job is null)
        {
            StatusResourceKey = "Job_Incomplete";
            this.downloadCommand.NotifyCanExecuteChanged();
            return;
        }

        ReviewRows.Add(new LabelValueViewModel("Job_JobIdLabel", job.JobId, Localizer));
        ReviewRows.Add(new LabelValueViewModel("Job_RollIdLabel", job.RollId, Localizer));
        ReviewRows.Add(new LabelValueViewModel("Job_ReviewRollSize", RollSummaryText, Localizer));
        ReviewRows.Add(new LabelValueViewModel("Job_ReviewProfile", this.profile!.Name, Localizer));
        ReviewRows.Add(new LabelValueViewModel("Job_ReviewLength", LengthCheckText, Localizer));
        ReviewRows.Add(new LabelValueViewModel("Job_ReviewProgram", this.program!.Name, Localizer));
        ReviewRows.Add(new LabelValueViewModel("Job_ReviewStock", StockSummaryText, Localizer));
        if (this.profile.NominalDiameterMm is double designed && designed > 0.0
            && Math.Abs(StartDiameterMm(this.roll!) - designed) / designed > 0.1)
        {
            // 辊形按另一种辊径设计（例如支承辊的辊形套到工作辊上）：提示，不拦——拉伸 / 居中已经管了长度。
            ReviewRows.Add(new LabelValueViewModel(
                "Job_ReviewDiameterCheck", Localizer.Format("Job_DiameterMismatchFormat", designed, StartDiameterMm(this.roll!)), Localizer));
        }

        ReviewRows.Add(new LabelValueViewModel("Job_ReviewDuration", TotalDurationText, Localizer));
        ReviewRows.Add(new LabelValueViewModel(
            "Job_ReviewOptions",
            string.Join(Localizer["Job_ListSeparator"], ProgramOptions.Where(row => row.IsOn).Select(row => row.Label)),
            Localizer));

        ParameterValidationResult result = this.validator.Validate(job, this.capability);
        foreach (ParameterViolation violation in result.Violations)
        {
            Violations.Add(new ViolationRowViewModel(violation, Localizer));
        }

        bool stockOk = this.stock is { ProblemResourceKey: null } && !programFitBlocks;
        CanDownload = result.IsValid && stockOk;
        StatusResourceKey = !stockOk ? this.stock?.ProblemResourceKey ?? "Job_Incomplete"
            : result.IsValid ? "Job_ReadyToHandOver" : "Job_ValidationFailed";
        this.downloadCommand.NotifyCanExecuteChanged();
    }

    private async Task DownloadAsync()
    {
        await RunGuardedAsync(async token =>
        {
            GrindingJob? job = BuildJob();
            if (job is null)
            {
                StatusResourceKey = "Job_Incomplete";
                return;
            }

            JobDownloadResult result = await this.downloadService.DownloadAsync(job, token).ConfigureAwait(true);
            Violations.Clear();
            foreach (ParameterViolation violation in result.Violations)
            {
                Violations.Add(new ViolationRowViewModel(violation, Localizer));
            }

            if (result.Succeeded)
            {
                StatusResourceKey = "Job_HandedOver";

                // Q3 / M7：下发成功就请 NC 切 AUTO、画面转到自动磨削，操作员在按钮板上按循环启动。
                // 方式请求只是请求：PLC 决定切不切；tagmap 没登记就不请求，操作员在机床面板上切。
                ManualCommandResult mode = await this.manualGrinding
                    .RequestModeAsync(MachineModeRequest.Auto, token).ConfigureAwait(true);
                if (!mode.Succeeded && mode.Outcome != ManualCommandOutcome.NotMapped)
                {
                    Alarms.Raise(AlarmSeverity.Warning, mode.ReasonResourceKey!, detail: null, code: AlarmCodes.Unspecified);
                }

                // 下一支辊从一张新作业开始。
                JobId = NewJobId();
                CanDownload = false;
                this.downloadCommand.NotifyCanExecuteChanged();
                Navigator.GoTo(PageKey.AutoGrinding);
                return;
            }

            StatusResourceKey = result.MissingTags.Count > 0 ? "Job_TagMapIncomplete" : "Job_ValidationFailed";
            foreach (string missing in result.MissingTags)
            {
                Alarms.Raise(AlarmSeverity.Error, "Alarm_TagMissing", missing);
            }
        }, CancellationToken.None).ConfigureAwait(true);
    }

    private void RefreshStepItems()
    {
        foreach (JobStepItemViewModel item in StepItems)
        {
            item.IsCurrent = item.Number == ActiveStep;
        }

        RefreshStepSummaries();
        this.nextCommand.NotifyCanExecuteChanged();
        this.previousCommand.NotifyCanExecuteChanged();
        this.downloadCommand.NotifyCanExecuteChanged();
        RefreshCommitPair();
    }

    private void RefreshStepSummaries()
    {
        StepItems[RollStep - 1].Summary = this.roll?.RollId ?? string.Empty;
        StepItems[RollStep - 1].IsDone = this.roll is not null;
        StepItems[ProfileStep - 1].Summary = this.profile?.Name ?? string.Empty;
        StepItems[ProfileStep - 1].IsDone = this.profile is not null && (!LengthMismatch || FitMode is not null);
        StepItems[ProgramStep - 1].Summary = this.program?.Name ?? string.Empty;
        StepItems[ProgramStep - 1].IsDone = this.program is not null;
        StepItems[OptionsStep - 1].Summary = Localizer.Format("Job_OptionsOnFormat", ProgramOptions.Count(row => row.IsOn));
        StepItems[OptionsStep - 1].IsDone = ActiveStep > OptionsStep;
        StepItems[ReviewStep - 1].IsDone = CanDownload;
    }

    private string KindLabel(RollKind kind) => kind switch
    {
        RollKind.WorkRoll => Localizer["RollKind_WorkRoll"],
        RollKind.BackupRoll => Localizer["RollKind_BackupRoll"],
        _ => "--",
    };

    private static string NewJobId() =>
        string.Create(CultureInfo.InvariantCulture, $"J{DateTimeOffset.Now:yyyyMMddHHmmss}");
}
