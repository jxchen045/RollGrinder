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
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
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
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
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

        SetFunctionKeys(new[]
        {
            new FunctionKeyViewModel("Fn_PreviousStep", this.previousCommand, localizer),
            new FunctionKeyViewModel("Fn_NextStep", this.nextCommand, localizer, FunctionKeyKind.Primary),
            new FunctionKeyViewModel("Fn_NewJob", NewJobCommand, localizer, requiresEditable: true),
            new FunctionKeyViewModel("Fn_RegisterRoll", RegisterRollCommand, localizer),

            // 下发是唯一的写机床通道；自动循环挂着程序时锁掉，免得把运行中的程序改了。
            new FunctionKeyViewModel("Fn_DownloadNc", this.downloadCommand, localizer, requiresEditable: true),
            FunctionKeyViewModel.ForAction(
                "Fn_EnterAuto", localizer, () => Navigator.GoToArea(PageKey.AutoGrinding), FunctionKeyKind.Start),
        });

        RefreshStepItems();
    }

    public override PageKey Key => PageKey.Job;

    public override string TitleResourceKey => "Page_Job";

    /// <summary>自动循环挂着程序时落只读锁：正在磨的那支辊不能被换掉。</summary>
    public override bool LocksDuringRun => true;

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

    partial void OnSelectedRollChanged(RollLedgerRowViewModel? value) =>
        _ = RunGuardedAsync(token => LoadRollAsync(value?.RollId, token), CancellationToken.None);

    partial void OnSelectedProfileChanged(RollProfileSummary? value) =>
        _ = RunGuardedAsync(token => LoadProfileAsync(value?.ProfileId, token), CancellationToken.None);

    partial void OnSelectedProgramChanged(ProgramSummary? value) =>
        _ = RunGuardedAsync(token => LoadProgramAsync(value?.ProgramId, token), CancellationToken.None);

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
        string? keepProfile = SelectedProfile?.ProfileId;
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
        RefreshStepItems();
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
        ProgramStep => this.program is not null,
        OptionsStep => true,
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

    /// <summary>台账里没有这支辊：派去台账新登记一支，登记完按导航槽回来就选上它。</summary>
    [RelayCommand]
    private void RegisterRoll()
    {
        this.draft.RegisterNewRollRequested = true;
        Navigator.StartTask(PageKey.Records, PageKey.Job);
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
                ProgramFrame.Normalize(this.program.Steps, this.stepTypes),
                options) with
        {
            ProfileId = this.profile.ProfileId,
            ProfileName = this.profile.Name,
            ProgramId = this.program.ProgramId,
            ProgramName = this.program.Name,
        };
    }

    /// <summary>核对页：列出这份作业的全部要点，跑下发前同一套校验；有错时"下发 NC"按不下去。</summary>
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

        CanDownload = result.IsValid;
        StatusResourceKey = result.IsValid ? "Job_ReadyToHandOver" : "Job_ValidationFailed";
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

                // Q3：下发成功就进自动加工页，操作员接着按启动。下一支辊从一张新作业开始。
                JobId = NewJobId();
                CanDownload = false;
                this.downloadCommand.NotifyCanExecuteChanged();
                Navigator.GoToArea(PageKey.AutoGrinding);
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
