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
using RollGrinder.Core.Units;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Calibration;
using RollGrinder.Data;
using RollGrinder.Services.Jobs;

namespace RollGrinder.App.ViewModels;

/// <summary>工序列表里的一道工序，参数行由该工序类型的 schema 生成。</summary>
public sealed partial class StepRowViewModel : ObservableObject
{
    public StepRowViewModel(int order, IGrindingStepType stepType, ParameterSet parameters, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        ArgumentNullException.ThrowIfNull(localizer);

        this.order = order;
        StepType = stepType;
        StepTypeKey = stepType.Key;
        DisplayName = localizer["StepType_" + stepType.Key];
        Parameters = new ObservableCollection<ParameterRowViewModel>(
            stepType.Schema.Descriptors.Select(descriptor => new ParameterRowViewModel(
                descriptor,
                parameters.TryGet(descriptor.Key, out ParameterValue? value) && value is not null
                    ? value
                    : descriptor.DefaultValue,
                localizer)));

        foreach (ParameterRowViewModel row in Parameters)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ParameterRowViewModel.Text))
                {
                    RefreshApplicability();
                }
            };
        }

        RefreshApplicability();
    }

    /// <summary>
    /// 按依赖关系点亮/压暗参数格：变速幅度与周期只在开了变速时才有意义。
    ///
    /// 连续进给与周期进给**不在这里互斥**——两路分量可以同时给值（说明书的磨削实例
    /// 粗磨一列两者都非零），哪一路不用就填 0。
    ///
    /// 格子始终留在原位，只是按不动——键位不跳动，操作员的手不用重新找。
    /// </summary>
    private void RefreshApplicability()
    {
        string? variation = ValueOf(StepParameterKeys.SpeedVariationTarget);
        bool isVarying = variation is null || variation != SpeedVariationChoices.Off;
        SetApplicable(StepParameterKeys.SpeedVariationPercent, isVarying);
        SetApplicable(StepParameterKeys.SpeedVariationPeriodSeconds, isVarying);
    }

    private string? ValueOf(string key) =>
        Parameters.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.Ordinal))?.Text;

    private void SetApplicable(string key, bool isApplicable)
    {
        ParameterRowViewModel? row = Parameters
            .FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        if (row is not null)
        {
            row.IsApplicable = isApplicable;
        }
    }

    [ObservableProperty]
    private int order;

    public string StepTypeKey { get; }

    public IGrindingStepType StepType { get; }

    public string DisplayName { get; }

    public ObservableCollection<ParameterRowViewModel> Parameters { get; }

    /// <summary>预计时长，例如"约 211 min"。参数改了要重算。</summary>
    [ObservableProperty]
    private string durationText = string.Empty;
}

/// <summary>
/// "插入工序"下拉里的一项。本台机床装不了的工序照样列出来，但标上"（未配置）"并禁掉——
/// 藏起来只会让人找不到，标出来才知道是机床没装，不是软件少做。
/// </summary>
public sealed class StepTypeOptionViewModel
{
    public StepTypeOptionViewModel(IGrindingStepType stepType, bool isAvailable, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        ArgumentNullException.ThrowIfNull(localizer);

        Key = stepType.Key;
        IsAvailable = isAvailable;
        RequiredOptionKey = stepType.RequiredOptionKey;

        string name = localizer["StepType_" + stepType.Key];
        DisplayName = isAvailable ? name : name + localizer["Steps_StepTypeNotAvailable"];
    }

    public string Key { get; }

    public string DisplayName { get; }

    /// <summary>本台机床能不能做这道工序。</summary>
    public bool IsAvailable { get; }

    /// <summary>缺的是哪一项装置；不需要装置时为 null。</summary>
    public string? RequiredOptionKey { get; }
}

/// <summary>
/// 程序步骤（自动磨削前取舍）里的一行。
/// 本台机床做不了的那几项压暗并禁掉，ToolTip 说明缺什么——
/// 藏起来只会让人以为软件少做，标出来才知道是机床没装。
/// </summary>
public sealed partial class ProgramOptionRowViewModel : ObservableObject
{
    public ProgramOptionRowViewModel(
        ProgramOptionDescriptor descriptor,
        bool isEnabled,
        bool isAvailable,
        IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(localizer);

        Descriptor = descriptor;
        Label = localizer[descriptor.ResourceKey];
        IsAvailable = isAvailable;
        UnavailableHint = isAvailable ? null : localizer["Steps_OptionNotAvailable"];

        // 机床做不了的项一律按"关"处理，免得存进程序里再到下发时被打回来。
        this.isOn = isEnabled && isAvailable;
    }

    public ProgramOptionDescriptor Descriptor { get; }

    public string Label { get; }

    /// <summary>本台机床做不做得了。</summary>
    public bool IsAvailable { get; }

    /// <summary>做不了时的说明。</summary>
    public string? UnavailableHint { get; }

    /// <summary>开关状态。</summary>
    [ObservableProperty]
    private bool isOn;
}

/// <summary>校验失败的一行，文案由原因与参数键组合而成。</summary>
public sealed class ViolationRowViewModel
{
    public ViolationRowViewModel(ParameterViolation violation, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(violation);
        ArgumentNullException.ThrowIfNull(localizer);

        // 违规项可能是一个参数，也可能是一整道工序（机床没装那个装置时）。
        // 两个命名空间都试一遍，都没有才退回原始键——界面上不留 "!Key!"。
        ParameterText = FirstLocalized(
            localizer,
            violation.ParameterKey,
            "Parameter_" + violation.ParameterKey,
            "StepType_" + violation.ParameterKey);
        ReasonText = violation.Limit is null
            ? localizer["Violation_" + violation.Kind]
            : localizer.Format("Violation_" + violation.Kind + "_WithLimit", violation.Limit.Value);
    }

    public string ParameterText { get; }

    public string ReasonText { get; }

    private static string FirstLocalized(IStringLocalizer localizer, string fallback, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            string text = localizer[candidate];
            if (!text.StartsWith('!'))
            {
                return text;
            }
        }

        return fallback;
    }
}

/// <summary>程序的一份快照，"放弃修改"用它回退。参数按界面文本原样存，回填时不做二次解析。</summary>
/// <param name="TypeKey">工序类型键。</param>
/// <param name="ParameterTexts">参数行文本，顺序与 schema 一致。</param>
internal sealed record StepSnapshot(string TypeKey, IReadOnlyList<string> ParameterTexts);

/// <summary>整支程序的快照。</summary>
internal sealed record StepsSnapshot(
    string JobId,
    string RollId,
    string BodyLengthMmText,
    string NominalDiameterMmText,
    string SelectedProfileTypeKey,
    IReadOnlyList<string> ProfileParameterTexts,
    IReadOnlyList<StepSnapshot> Steps,
    IReadOnlyList<bool> ProgramOptions)
{
    /// <summary>空快照：还没进过本页时用。</summary>
    public static StepsSnapshot Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<StepSnapshot>(),
        Array.Empty<bool>());
}

/// <summary>
/// 工艺编排：辊件几何、目标辊形、工序序列的编辑与下发。
/// 界面按注册表与 schema 生成，新增一类辊形或工序不改这里。
/// </summary>
public sealed partial class StepsViewModel : PageViewModelBase
{
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly IJobDownloadService downloadService;
    private readonly IProgramRepository programs;
    private readonly IRollProfileRepository profileLibrary;
    private readonly ICalibrationService calibration;

    /// <summary>从辊形库选中的那条辊形；没选（现编现用）时为 null。</summary>
    private RollProfileDefinition? selectedProfileDefinition;

    /// <summary>进入本页时的程序快照，供"放弃修改"回退。</summary>
    private StepsSnapshot committed = StepsSnapshot.Empty;

    /// <summary>回退期间不要把恢复动作本身算成修改。</summary>
    private bool suppressDirty;

    public StepsViewModel(
        RollProfileTypeRegistry profileTypes,
        GrindingStepTypeRegistry stepTypes,
        IJobDownloadService downloadService,
        IProgramRepository programs,
        IRollProfileRepository profileLibrary,
        ICalibrationService calibration,
        MachineDescription machine,
        MachineCapability capability,
        HmiSettings settings,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        ArgumentNullException.ThrowIfNull(capability);
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.profileLibrary = profileLibrary ?? throw new ArgumentNullException(nameof(profileLibrary));
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        ArgumentNullException.ThrowIfNull(machine);

        ProfileTypeKeys = new ObservableCollection<string>(profileTypes.All.Select(type => type.Key));
        StepTypeOptions = new ObservableCollection<StepTypeOptionViewModel>(
            stepTypes.All.Select(type => new StepTypeOptionViewModel(type, capability.Supports(type), localizer)));

        this.bodyLengthMmText = machine.Workpiece.MinBodyLengthMm.ToString("F1", CultureInfo.InvariantCulture);
        this.nominalDiameterMmText = machine.Workpiece.MinDiameterMm.ToString("F1", CultureInfo.InvariantCulture);
        this.selectedProfileTypeKey = ProfileTypeKeys.FirstOrDefault() ?? string.Empty;
        this.selectedStepType = StepTypeOptions.FirstOrDefault(option => option.IsAvailable)
            ?? StepTypeOptions.FirstOrDefault();
        this.jobId = NewJobId();
        this.rollId = string.Empty;

        RebuildProfileParameters();

        ProgramOptions = new ObservableCollection<ProgramOptionRowViewModel>(
            ProgramOptionCatalog.All.Select(option => new ProgramOptionRowViewModel(
                option, option.DefaultEnabled, capability.Supports(option), localizer)));

        foreach (ProgramOptionRowViewModel row in ProgramOptions)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProgramOptionRowViewModel.IsOn))
                {
                    MarkEdited();
                }
            };
        }

        BuildCompensationSettings(settings, machine);

        // 公差是现场标定值，设置页上随时能改——改完这张卡片要跟着变。
        calibration.Changed += (_, _) => BuildCompensationSettings(settings, machine);

        SetFunctionKeys(new[]
        {
            new FunctionKeyViewModel("Fn_SaveProgram", new AsyncRelayCommand(
                () => SaveAsync(CancellationToken.None)), localizer, FunctionKeyKind.Primary, requiresEditable: true),
            new FunctionKeyViewModel("Fn_SaveAs", SaveProgramAsCommand, localizer, requiresEditable: true),

            // 派去辊形编辑页选一个辊形，办完由导航槽送回本页。
            new FunctionKeyViewModel("Fn_SelectProfile", OpenProfileLibraryCommand, localizer),
            FunctionKeyViewModel.Placeholder("Fn_RollData", localizer, () => NotImplementedYet("Fn_RollData"), requiresEditable: true),

            // 下发是唯一的写机床通道；自动循环挂着程序时锁掉，免得把运行中的程序改了。
            new FunctionKeyViewModel("Fn_DownloadNc", DownloadCommand, localizer, requiresEditable: true),
            new FunctionKeyViewModel("Fn_ProgramLibrary", OpenProgramLibraryCommand, localizer),
            FunctionKeyViewModel.Placeholder(
                "Fn_EnterAuto", localizer, () => Navigator.GoToArea(PageKey.AutoGrinding), FunctionKeyKind.Start),
        });
    }

    public override PageKey Key => PageKey.Steps;

    public override string TitleResourceKey => "Page_Steps";

    public override string MenuHintResourceKey => "Menu_StepsHint";

    /// <summary>编辑页：自动循环挂着程序时落只读锁。</summary>
    public override bool LocksDuringRun => true;

    public ObservableCollection<string> ProfileTypeKeys { get; }

    public ObservableCollection<StepTypeOptionViewModel> StepTypeOptions { get; }

    public ObservableCollection<ParameterRowViewModel> ProfileParameters { get; } = new();

    public ObservableCollection<StepRowViewModel> Steps { get; } = new();

    public ObservableCollection<ViolationRowViewModel> Violations { get; } = new();

    /// <summary>程序步骤（自动磨削前取舍）的八个开关。</summary>
    public ObservableCollection<ProgramOptionRowViewModel> ProgramOptions { get; }

    /// <summary>补偿设置（制造商权限）。取值来自 hmi.json 与 machine.json 的阈值。</summary>
    public ObservableCollection<LabelValueViewModel> CompensationSettings { get; } = new();

    [ObservableProperty]
    private string totalDurationText = "--";

    [ObservableProperty]
    private string jobId;

    [ObservableProperty]
    private string rollId;

    [ObservableProperty]
    private string bodyLengthMmText;

    [ObservableProperty]
    private string nominalDiameterMmText;

    [ObservableProperty]
    private string selectedProfileTypeKey;

    [ObservableProperty]
    private StepTypeOptionViewModel? selectedStepType;

    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    /// <summary>状态文字，按资源键取。</summary>
    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : Localizer[StatusResourceKey];

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnSelectedProfileTypeKeyChanged(string value)
    {
        RebuildProfileParameters();
        MarkEdited();
    }

    partial void OnJobIdChanged(string value) => MarkEdited();

    partial void OnRollIdChanged(string value) => MarkEdited();

    partial void OnBodyLengthMmTextChanged(string value) => MarkEdited();

    partial void OnNominalDiameterMmTextChanged(string value) => MarkEdited();

    [RelayCommand]
    private void AddStep()
    {
        if (SelectedStepType is null)
        {
            return;
        }

        if (!SelectedStepType.IsAvailable)
        {
            // 机床没装这道工序要用的装置：当场说清楚，而不是让人编完、下发时才被打回来。
            Alarms.Raise(
                AlarmSeverity.Warning,
                "Alarm_StepTypeNotAvailable",
                Localizer["StepType_" + SelectedStepType.Key]);
            return;
        }

        IGrindingStepType stepType = this.stepTypes.Get(SelectedStepType.Key);
        Steps.Add(Track(new StepRowViewModel(Steps.Count + 1, stepType, stepType.Schema.CreateDefaults(), Localizer)));
        RefreshDurations();
        MarkEdited();
    }

    [RelayCommand]
    private void RemoveStep(StepRowViewModel? step)
    {
        if (step is null)
        {
            return;
        }

        Steps.Remove(step);
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].Order = i + 1;
        }

        MarkEdited();

        RefreshDurations();
    }

    [RelayCommand]
    private void NewJob()
    {
        JobId = NewJobId();
        Steps.Clear();
        Violations.Clear();
        StatusResourceKey = string.Empty;
        MarkEdited();
    }

    [RelayCommand]
    private Task ValidateAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(_ =>
        {
            GrindingJob? job = TryBuildJob();
            if (job is null)
            {
                return Task.CompletedTask;
            }

            RefreshDurations();
            StatusResourceKey = "Job_ReadyToHandOver";
            return Task.CompletedTask;
        }, cancellationToken);

    [RelayCommand]
    private Task DownloadAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            GrindingJob? job = TryBuildJob();
            if (job is null)
            {
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

                // 下发成功 = 机床上的程序与界面一致，本页不再是脏的。
                Capture();
                MarkClean();
                return;
            }

            StatusResourceKey = result.MissingTags.Count > 0 ? "Job_TagMapIncomplete" : "Job_ValidationFailed";
            foreach (string missing in result.MissingTags)
            {
                Alarms.Raise(AlarmSeverity.Error, "Alarm_TagMissing", missing);
            }
        }, cancellationToken);

    private GrindingJob? TryBuildJob()
    {
        Violations.Clear();
        StatusResourceKey = string.Empty;

        if (!TryParseDouble(BodyLengthMmText, out double bodyLengthMm)
            || !TryParseDouble(NominalDiameterMmText, out double nominalDiameterMm))
        {
            StatusResourceKey = "Job_GeometryInvalid";
            return null;
        }

        if (string.IsNullOrWhiteSpace(JobId) || string.IsNullOrWhiteSpace(RollId))
        {
            StatusResourceKey = "Job_IdentifiersMissing";
            return null;
        }

        if (Steps.Count == 0)
        {
            StatusResourceKey = "Job_NoSteps";
            return null;
        }

        // 辊形有两条来路：从辊形库选一条（多段叠加），或者在本页现编一条单曲线。
        // 选了库里的就用库里的，并把来源记进作业——记录要记当时用的是哪一条。
        CompositeRollProfile? libraryProfile = this.selectedProfileDefinition?.Profile;
        ParameterSet? profileParameters = null;
        if (libraryProfile is null)
        {
            profileParameters = Collect(ProfileParameters);
            if (profileParameters is null)
            {
                StatusResourceKey = "Job_ParametersInvalid";
                return null;
            }
        }

        var steps = new List<GrindingJobStep>(Steps.Count);
        foreach (StepRowViewModel step in Steps)
        {
            ParameterSet? stepParameters = Collect(step.Parameters);
            if (stepParameters is null)
            {
                StatusResourceKey = "Job_ParametersInvalid";
                return null;
            }

            steps.Add(new GrindingJobStep(step.Order, step.StepTypeKey, stepParameters));
        }

        RollGeometry geometry = RollGeometry.FromDiameter(bodyLengthMm, nominalDiameterMm);

        GrindingJob job = libraryProfile is not null
            ? GrindingJob.Create(JobId, RollId, geometry, libraryProfile, steps, CollectProgramOptions())
            : GrindingJob.Create(
                JobId, RollId, geometry, SelectedProfileTypeKey, profileParameters!, steps, CollectProgramOptions());

        return job with
        {
            ProfileId = this.selectedProfileDefinition?.ProfileId,
            ProfileName = this.selectedProfileDefinition?.Name,
            ProgramId = ProgramId,
            ProgramName = string.IsNullOrWhiteSpace(ProgramName) ? null : ProgramName.Trim(),
        };
    }

    // ── 程序库与辊形库 ────────────────────────────────────────────────────────
    //
    // 程序与辊形都是**可复用的模板**，作业只是"这支辊用哪条辊形、哪支程序"。
    // 作业引用它们的时候复制一份快照，库里之后改了不会动已经磨过的那支辊的记录。

    /// <summary>当前程序的名字，库里按这个名字找。</summary>
    [ObservableProperty]
    private string programName = string.Empty;

    /// <summary>当前程序在库里的标识；还没存过就是 null。</summary>
    [ObservableProperty]
    private string? programId;

    /// <summary>库里现有的程序。</summary>
    public ObservableCollection<ProgramSummary> ProgramLibraryEntries { get; } = new();

    [ObservableProperty]
    private bool isProgramLibraryOpen;

    [ObservableProperty]
    private ProgramSummary? selectedProgramEntry;

    /// <summary>库里现有的辊形。</summary>
    public ObservableCollection<RollProfileSummary> ProfileLibraryEntries { get; } = new();

    [ObservableProperty]
    private bool isProfileLibraryOpen;

    [ObservableProperty]
    private RollProfileSummary? selectedProfileEntry;

    /// <summary>
    /// 这支作业用的辊形是从库里选的还是现编的。选了库里的，下面那个单曲线参数格就压暗——
    /// 两边同时能改会让人搞不清最后下发的是哪一条。
    /// </summary>
    [ObservableProperty]
    private bool usesLibraryProfile;

    /// <summary>选中的辊形名，界面上显示；现编现用时是空的。</summary>
    [ObservableProperty]
    private string selectedProfileName = string.Empty;

    /// <summary>
    /// 本页那条现编的单曲线还能不能改：只读时不能，选了库里的辊形时也不能——
    /// 两处同时能改会让人搞不清最后下发的是哪一条。
    /// </summary>
    public bool CanEditInlineProfile => !IsReadOnly && !UsesLibraryProfile;

    partial void OnUsesLibraryProfileChanged(bool value) => OnPropertyChanged(nameof(CanEditInlineProfile));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // IsReadOnly 在基类里，改不了它的 partial 钩子，只能在这里接一手。
        if (e.PropertyName == nameof(IsReadOnly))
        {
            OnPropertyChanged(nameof(CanEditInlineProfile));
        }
    }

    partial void OnProgramNameChanged(string value)
    {
        MarkEdited();
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>有名字才谈得上保存——没名字存进库里就找不回来了。</summary>
    public override bool CanSave => !string.IsNullOrWhiteSpace(ProgramName);

    /// <summary>
    /// 把当前这支程序存回程序库。走页面基类的保存契约，
    /// 所以"改了没存就想离开"那道拦截也会用到它。
    /// </summary>
    public override Task<bool> SaveAsync(CancellationToken cancellationToken) =>
        StoreProgramAsync(ProgramId ?? NewProgramId(), cancellationToken);

    /// <summary>另存一支新程序，库里原来那支不动。</summary>
    [RelayCommand]
    private Task SaveProgramAsAsync(CancellationToken cancellationToken) =>
        StoreProgramAsync(NewProgramId(), cancellationToken);

    private async Task<bool> StoreProgramAsync(string programId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ProgramName))
        {
            Alarms.Raise(AlarmSeverity.Warning, "Program_NeedsName", code: AlarmCodes.DomainFailure);
            return false;
        }

        var steps = new List<GrindingJobStep>(Steps.Count);
        foreach (StepRowViewModel step in Steps)
        {
            ParameterSet? stepParameters = Collect(step.Parameters);
            if (stepParameters is null)
            {
                StatusResourceKey = "Job_ParametersInvalid";
                return false;
            }

            steps.Add(new GrindingJobStep(step.Order, step.StepTypeKey, stepParameters));
        }

        if (steps.Count == 0)
        {
            StatusResourceKey = "Job_NoSteps";
            return false;
        }

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GrindingProgram? existing = ProgramId is null
                ? null
                : await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(true);

            await this.programs.SaveAsync(
                GrindingProgram.Create(programId, ProgramName.Trim(), steps, existing?.CreatedAtUtc ?? now, CollectProgramOptions())
                    with { ModifiedAtUtc = now },
                cancellationToken).ConfigureAwait(true);

            ProgramId = programId;
            Capture();
            IsDirty = false;
            Alarms.Raise(AlarmSeverity.Information, "Program_Saved", ProgramName, AlarmCodes.HandoverCompleted);
            return true;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
            return false;
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
            return false;
        }
    }

    [RelayCommand]
    private async Task OpenProgramLibraryAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ProgramSummary> entries =
                await this.programs.ListAsync(LibraryListLimit, cancellationToken).ConfigureAwait(true);

            ProgramLibraryEntries.Clear();
            foreach (ProgramSummary entry in entries)
            {
                ProgramLibraryEntries.Add(entry);
            }

            SelectedProgramEntry = ProgramLibraryEntries.FirstOrDefault();
            IsProgramLibraryOpen = true;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    [RelayCommand]
    private void CloseProgramLibrary() => IsProgramLibraryOpen = false;

    /// <summary>把选中的那支程序调进编辑器，整串工序与开关一起换掉。</summary>
    [RelayCommand]
    private async Task LoadProgramAsync(CancellationToken cancellationToken)
    {
        if (SelectedProgramEntry is null)
        {
            return;
        }

        try
        {
            GrindingProgram? program = await this.programs
                .GetAsync(SelectedProgramEntry.ProgramId, cancellationToken).ConfigureAwait(true);
            if (program is null)
            {
                return;
            }

            ApplyProgram(program);
            IsProgramLibraryOpen = false;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    private void ApplyProgram(GrindingProgram program)
    {
        this.suppressDirty = true;
        try
        {
            ProgramId = program.ProgramId;
            ProgramName = program.Name;

            Steps.Clear();
            foreach (GrindingJobStep step in program.Steps)
            {
                IGrindingStepType stepType = this.stepTypes.Get(step.StepTypeKey);
                Steps.Add(Track(new StepRowViewModel(step.Order, stepType, step.Parameters, Localizer)));
            }

            foreach (ProgramOptionRowViewModel row in ProgramOptions)
            {
                row.IsOn = program.IsProgramOptionEnabled(row.Descriptor.Key);
            }
        }
        finally
        {
            this.suppressDirty = false;
        }

        Capture();
        IsDirty = false;
        RefreshDurations();
    }

    [RelayCommand]
    private async Task DeleteProgramAsync(CancellationToken cancellationToken)
    {
        if (SelectedProgramEntry is null)
        {
            return;
        }

        try
        {
            await this.programs.DeleteAsync(SelectedProgramEntry.ProgramId, cancellationToken).ConfigureAwait(true);
            if (string.Equals(ProgramId, SelectedProgramEntry.ProgramId, StringComparison.Ordinal))
            {
                // 编辑器里还开着它：工序留着，但它已经不在库里了，再存就是新的一支。
                ProgramId = null;
            }

            await OpenProgramLibraryAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    [RelayCommand]
    private async Task OpenProfileLibraryAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<RollProfileSummary> entries =
                await this.profileLibrary.ListAsync(LibraryListLimit, cancellationToken).ConfigureAwait(true);

            ProfileLibraryEntries.Clear();
            foreach (RollProfileSummary entry in entries)
            {
                ProfileLibraryEntries.Add(entry);
            }

            SelectedProfileEntry = ProfileLibraryEntries.FirstOrDefault();
            IsProfileLibraryOpen = true;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    [RelayCommand]
    private void CloseProfileLibrary() => IsProfileLibraryOpen = false;

    /// <summary>选用库里的那条辊形。多段曲线就是这样进到作业里的。</summary>
    [RelayCommand]
    private async Task UseProfileFromLibraryAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfileEntry is null)
        {
            return;
        }

        try
        {
            this.selectedProfileDefinition = await this.profileLibrary
                .GetAsync(SelectedProfileEntry.ProfileId, cancellationToken).ConfigureAwait(true);
            if (this.selectedProfileDefinition is null)
            {
                return;
            }

            UsesLibraryProfile = true;
            SelectedProfileName = this.selectedProfileDefinition.Name;
            IsProfileLibraryOpen = false;
            MarkEdited();
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    /// <summary>改回现编现用：下面那个单曲线参数格重新可用。</summary>
    [RelayCommand]
    private void ClearProfileSelection()
    {
        this.selectedProfileDefinition = null;
        UsesLibraryProfile = false;
        SelectedProfileName = string.Empty;
        MarkEdited();
    }

    /// <summary>库面板一次列多少条。</summary>
    private const int LibraryListLimit = 200;

    private static string NewProgramId() =>
        string.Create(CultureInfo.InvariantCulture, $"G{DateTimeOffset.Now:yyyyMMddHHmmss}");

    private ParameterSet CollectProgramOptions() => new(ProgramOptions.Select(row =>
        new KeyValuePair<string, ParameterValue>(
            row.Descriptor.Key, ParameterValue.FromBoolean(row.IsOn))));

    /// <summary>切到本页时记住当前程序，"放弃修改"才有东西可回。</summary>
    public override void OnActivated() => Capture();

    /// <summary>放弃修改：回到进入本页（或上次下发成功）时的程序。</summary>
    public override void DiscardChanges()
    {
        Restore(this.committed);
        base.DiscardChanges();
    }

    /// <summary>把当前程序存成"干净"版本。</summary>
    private void Capture() => this.committed = new StepsSnapshot(
        JobId,
        RollId,
        BodyLengthMmText,
        NominalDiameterMmText,
        SelectedProfileTypeKey,
        ProfileParameters.Select(row => row.Text).ToArray(),
        Steps.Select(step => new StepSnapshot(
            step.StepTypeKey,
            step.Parameters.Select(row => row.Text).ToArray())).ToArray(),
        ProgramOptions.Select(row => row.IsOn).ToArray());

    private void Restore(StepsSnapshot snapshot)
    {
        this.suppressDirty = true;
        try
        {
            JobId = snapshot.JobId;
            RollId = snapshot.RollId;
            BodyLengthMmText = snapshot.BodyLengthMmText;
            NominalDiameterMmText = snapshot.NominalDiameterMmText;

            // 换类型会重建参数行，所以要先换类型、再回填文本。
            SelectedProfileTypeKey = snapshot.SelectedProfileTypeKey;
            RebuildProfileParameters();
            ApplyTexts(ProfileParameters, snapshot.ProfileParameterTexts);

            Steps.Clear();
            for (int i = 0; i < snapshot.Steps.Count; i++)
            {
                StepSnapshot stepSnapshot = snapshot.Steps[i];
                IGrindingStepType stepType = this.stepTypes.Get(stepSnapshot.TypeKey);
                var row = new StepRowViewModel(i + 1, stepType, stepType.Schema.CreateDefaults(), Localizer);
                ApplyTexts(row.Parameters, stepSnapshot.ParameterTexts);
                Steps.Add(Track(row));
            }

            int optionCount = Math.Min(ProgramOptions.Count, snapshot.ProgramOptions.Count);
            for (int i = 0; i < optionCount; i++)
            {
                ProgramOptions[i].IsOn = snapshot.ProgramOptions[i];
            }

            Violations.Clear();
            StatusResourceKey = string.Empty;
            RefreshDurations();
        }
        finally
        {
            this.suppressDirty = false;
        }
    }

    private static void ApplyTexts(IList<ParameterRowViewModel> rows, IReadOnlyList<string> texts)
    {
        int count = Math.Min(rows.Count, texts.Count);
        for (int i = 0; i < count; i++)
        {
            rows[i].Text = texts[i];
        }
    }

    /// <summary>参数行的文本一改就算改了程序。</summary>
    private ParameterRowViewModel Track(ParameterRowViewModel row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ParameterRowViewModel.Text))
            {
                MarkEdited();
            }
        };

        return row;
    }

    private StepRowViewModel Track(StepRowViewModel row)
    {
        foreach (ParameterRowViewModel parameter in row.Parameters)
        {
            Track(parameter);
        }

        return row;
    }

    /// <summary>程序被改动：打脏标记，并把"已下发"状态清掉——界面与机床已经不一致了。</summary>
    private void MarkEdited()
    {
        if (this.suppressDirty)
        {
            return;
        }

        MarkDirty();
    }

    private static ParameterSet? Collect(IEnumerable<ParameterRowViewModel> rows)
    {
        var values = new List<KeyValuePair<string, ParameterValue>>();
        foreach (ParameterRowViewModel row in rows)
        {
            ParameterValue? value = row.ToParameterValue();
            if (value is null)
            {
                return null;
            }

            values.Add(new KeyValuePair<string, ParameterValue>(row.Key, value));
        }

        return new ParameterSet(values);
    }

    private void RebuildProfileParameters()
    {
        ProfileParameters.Clear();
        if (string.IsNullOrEmpty(SelectedProfileTypeKey))
        {
            return;
        }

        IRollProfileType profileType = this.profileTypes.Get(SelectedProfileTypeKey);
        ParameterSet defaults = profileType.Schema.CreateDefaults();
        foreach (ParameterDescriptor descriptor in profileType.Schema.Descriptors)
        {
            ProfileParameters.Add(Track(new ParameterRowViewModel(descriptor, defaults.Get(descriptor.Key), Localizer)));
        }
    }

    /// <summary>按当前参数重算每道工序与总的预计时长。</summary>
    private void RefreshDurations()
    {
        if (!TryParseDouble(BodyLengthMmText, out double bodyLengthMm)
            || !TryParseDouble(NominalDiameterMmText, out double nominalDiameterMm))
        {
            TotalDurationText = "--";
            return;
        }

        RollGeometry geometry;
        try
        {
            geometry = RollGeometry.FromDiameter(bodyLengthMm, nominalDiameterMm);
        }
        catch (DomainException)
        {
            TotalDurationText = "--";
            return;
        }

        TimeSpan total = TimeSpan.Zero;
        foreach (StepRowViewModel step in Steps)
        {
            ParameterSet? parameters = Collect(step.Parameters);
            if (parameters is null)
            {
                step.DurationText = string.Empty;
                continue;
            }

            try
            {
                TimeSpan duration = step.StepType.CreatePlan(geometry, parameters).EstimateDuration(geometry);
                total += duration;
                step.DurationText = duration > TimeSpan.Zero
                    ? Localizer.Format("Auto_StepDurationFormat", (int)duration.TotalMinutes)
                    : string.Empty;
            }
            catch (DomainException)
            {
                step.DurationText = string.Empty;
            }
        }

        TotalDurationText = Localizer.Format("Steps_TotalTimeFormat", (int)total.TotalMinutes);
    }

    private void BuildCompensationSettings(HmiSettings settings, MachineDescription machine)
    {
        CompensationSettings.Clear();
        CompensationSettings.Add(new LabelValueViewModel(
            "Comp_Gain", settings.CompensationGain.ToString("F2", CultureInfo.CurrentCulture), Localizer));
        CompensationSettings.Add(new LabelValueViewModel(
            "Comp_SmoothingPoints",
            settings.CompensationSmoothingPoints.ToString(CultureInfo.CurrentCulture),
            Localizer));
        CompensationSettings.Add(new LabelValueViewModel(
            "Comp_MaxCorrection",
            machine.Thresholds.TryGetValue("maxCompensationRadiusMm", out double maxCorrectionMm)
                ? UnitConversion.RadiusMmToDiameterMicrometer(maxCorrectionMm).ToString("F1", CultureInfo.CurrentCulture)
                : Localizer["Common_NotConfigured"],
            Localizer));
        CompensationSettings.Add(new LabelValueViewModel(
            "Comp_Tolerance",
            this.calibration.Current.ProfileToleranceMicrometer.ToString("F1", CultureInfo.CurrentCulture),
            Localizer));
        CompensationSettings.Add(new LabelValueViewModel(
            "Comp_MaxInfeed",
            machine.Thresholds.TryGetValue("maxInfeedPerPassRadiusMm", out double maxInfeedMm)
                ? UnitConversion.RadiusMmToDiameterMicrometer(maxInfeedMm).ToString("F1", CultureInfo.CurrentCulture)
                : Localizer["Common_NotConfigured"],
            Localizer));
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string NewJobId() =>
        string.Create(CultureInfo.InvariantCulture, $"J{DateTimeOffset.Now:yyyyMMddHHmmss}");
}
