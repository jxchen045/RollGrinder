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
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
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
        StepTypeKey = stepType.Key;
        DisplayName = localizer["StepType_" + stepType.Key];
        Parameters = new ObservableCollection<ParameterRowViewModel>(
            stepType.Schema.Descriptors.Select(descriptor => new ParameterRowViewModel(
                descriptor,
                parameters.TryGet(descriptor.Key, out ParameterValue? value) && value is not null
                    ? value
                    : descriptor.DefaultValue,
                localizer)));
    }

    [ObservableProperty]
    private int order;

    public string StepTypeKey { get; }

    public string DisplayName { get; }

    public ObservableCollection<ParameterRowViewModel> Parameters { get; }
}

/// <summary>校验失败的一行，文案由原因与参数键组合而成。</summary>
public sealed class ViolationRowViewModel
{
    public ViolationRowViewModel(ParameterViolation violation, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(violation);
        ArgumentNullException.ThrowIfNull(localizer);

        string parameterLabel = localizer["Parameter_" + violation.ParameterKey];
        ParameterText = parameterLabel.StartsWith('!') ? violation.ParameterKey : parameterLabel;
        ReasonText = violation.Limit is null
            ? localizer["Violation_" + violation.Kind]
            : localizer.Format("Violation_" + violation.Kind + "_WithLimit", violation.Limit.Value);
    }

    public string ParameterText { get; }

    public string ReasonText { get; }
}

/// <summary>
/// 工艺编排：辊件几何、目标辊形、工序序列的编辑与下发。
/// 界面按注册表与 schema 生成，新增一类辊形或工序不改这里。
/// </summary>
public sealed partial class JobEditorViewModel : ViewModelBase
{
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly IJobDownloadService downloadService;
    private readonly IStringLocalizer localizer;

    public JobEditorViewModel(
        RollProfileTypeRegistry profileTypes,
        GrindingStepTypeRegistry stepTypes,
        IJobDownloadService downloadService,
        MachineDescription machine,
        IStringLocalizer localizer,
        IAlarmSink alarms)
        : base(alarms)
    {
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        ArgumentNullException.ThrowIfNull(machine);

        ProfileTypeKeys = new ObservableCollection<string>(profileTypes.All.Select(type => type.Key));
        StepTypeKeys = new ObservableCollection<string>(stepTypes.All.Select(type => type.Key));

        this.bodyLengthMmText = machine.Workpiece.MinBodyLengthMm.ToString("F1", CultureInfo.InvariantCulture);
        this.nominalDiameterMmText = machine.Workpiece.MinDiameterMm.ToString("F1", CultureInfo.InvariantCulture);
        this.selectedProfileTypeKey = ProfileTypeKeys.FirstOrDefault() ?? string.Empty;
        this.selectedStepTypeKey = StepTypeKeys.FirstOrDefault() ?? string.Empty;
        this.jobId = NewJobId();
        this.rollId = string.Empty;

        RebuildProfileParameters();
    }

    public ObservableCollection<string> ProfileTypeKeys { get; }

    public ObservableCollection<string> StepTypeKeys { get; }

    public ObservableCollection<ParameterRowViewModel> ProfileParameters { get; } = new();

    public ObservableCollection<StepRowViewModel> Steps { get; } = new();

    public ObservableCollection<ViolationRowViewModel> Violations { get; } = new();

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
    private string selectedStepTypeKey;

    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    /// <summary>状态文字，按资源键取。</summary>
    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : this.localizer[StatusResourceKey];

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnSelectedProfileTypeKeyChanged(string value) => RebuildProfileParameters();

    [RelayCommand]
    private void AddStep()
    {
        if (string.IsNullOrEmpty(SelectedStepTypeKey))
        {
            return;
        }

        IGrindingStepType stepType = this.stepTypes.Get(SelectedStepTypeKey);
        Steps.Add(new StepRowViewModel(Steps.Count + 1, stepType, stepType.Schema.CreateDefaults(), this.localizer));
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
    }

    [RelayCommand]
    private void NewJob()
    {
        JobId = NewJobId();
        Steps.Clear();
        Violations.Clear();
        StatusResourceKey = string.Empty;
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
                Violations.Add(new ViolationRowViewModel(violation, this.localizer));
            }

            if (result.Succeeded)
            {
                StatusResourceKey = "Job_HandedOver";
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
            steps);
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
            ProfileParameters.Add(new ParameterRowViewModel(descriptor, defaults.Get(descriptor.Key), this.localizer));
        }
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string NewJobId() =>
        string.Create(CultureInfo.InvariantCulture, $"J{DateTimeOffset.Now:yyyyMMddHHmmss}");
}
