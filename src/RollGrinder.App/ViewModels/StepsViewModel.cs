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

    /// <summary>进入本页时的程序快照，供"放弃修改"回退。</summary>
    private StepsSnapshot committed = StepsSnapshot.Empty;

    /// <summary>回退期间不要把恢复动作本身算成修改。</summary>
    private bool suppressDirty;

    public StepsViewModel(
        RollProfileTypeRegistry profileTypes,
        GrindingStepTypeRegistry stepTypes,
        IJobDownloadService downloadService,
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

        SetFunctionKeys(new[]
        {
            FunctionKeyViewModel.Placeholder("Fn_SaveProgram", localizer, () => NotImplementedYet("Fn_SaveProgram"), FunctionKeyKind.Primary, requiresEditable: true),
            FunctionKeyViewModel.Placeholder("Fn_SaveAs", localizer, () => NotImplementedYet("Fn_SaveAs"), requiresEditable: true),

            // 派去辊形编辑页选一个辊形，办完由导航槽送回本页。
            FunctionKeyViewModel.Placeholder(
                "Fn_SelectProfile", localizer, () => Navigator.StartTask(PageKey.Profile, PageKey.Steps)),
            FunctionKeyViewModel.Placeholder("Fn_RollData", localizer, () => NotImplementedYet("Fn_RollData"), requiresEditable: true),

            // 下发是唯一的写机床通道；自动循环挂着程序时锁掉，免得把运行中的程序改了。
            new FunctionKeyViewModel("Fn_DownloadNc", DownloadCommand, localizer, requiresEditable: true),
            FunctionKeyViewModel.Placeholder("Fn_ProgramLibrary", localizer, () => NotImplementedYet("Fn_ProgramLibrary")),
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

        ParameterSet? profileParameters = Collect(ProfileParameters);
        if (profileParameters is null)
        {
            StatusResourceKey = "Job_ParametersInvalid";
            return null;
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

        return GrindingJob.Create(
            JobId,
            RollId,
            RollGeometry.FromDiameter(bodyLengthMm, nominalDiameterMm),
            SelectedProfileTypeKey,
            profileParameters,
            steps,
            CollectProgramOptions());
    }

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
            settings.ProfileToleranceDiameterMicrometer.ToString("F1", CultureInfo.CurrentCulture),
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
