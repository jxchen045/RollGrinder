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
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Units;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Measurement;

namespace RollGrinder.App.ViewModels;

/// <summary>测点列表里的一行；界面按辊身坐标与直径量显示。</summary>
public sealed class MeasurementRowViewModel
{
    public MeasurementRowViewModel(MeasurementPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        Point = point;
    }

    public MeasurementPoint Point { get; }

    public string BodyPositionText => Point.BodyPositionMm.ToString("F1", CultureInfo.CurrentCulture);

    public string DiameterText =>
        UnitConversion.RadiusMmToDiameterMm(Point.MeasuredRadiusMm).ToString("F4", CultureInfo.CurrentCulture);
}

/// <summary>
/// 测量与补偿：手动采点、归档、算补偿。补偿只在下一次下发时生效。
/// </summary>
public sealed partial class MeasurementViewModel : ViewModelBase
{
    private readonly IMeasurementService measurementService;
    private readonly ICompensationService compensationService;
    private readonly IStringLocalizer localizer;
    private readonly HmiSettings settings;

    public MeasurementViewModel(
        IMeasurementService measurementService,
        ICompensationService compensationService,
        HmiSettings settings,
        IStringLocalizer localizer,
        IAlarmSink alarms)
        : base(alarms)
    {
        this.measurementService = measurementService ?? throw new ArgumentNullException(nameof(measurementService));
        this.compensationService = compensationService ?? throw new ArgumentNullException(nameof(compensationService));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public ObservableCollection<MeasurementRowViewModel> Points { get; } = new();

    /// <summary>最近一次算出的偏差曲线，供图表使用。</summary>
    public RollProfile? Deviation { get; private set; }

    /// <summary>最近一次算出的补偿曲线，供图表使用。</summary>
    public RollProfile? Compensation { get; private set; }

    /// <summary>偏差或补偿曲线有更新。</summary>
    public event EventHandler? ProfilesChanged;

    [ObservableProperty]
    private string jobId = string.Empty;

    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    [ObservableProperty]
    private string worstDeviationText = "--";

    [ObservableProperty]
    private string peakToValleyText = "--";

    [ObservableProperty]
    private bool isWithinTolerance;

    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : this.localizer[StatusResourceKey];

    /// <summary>公差提示（直径量 µm）。</summary>
    public string ToleranceText =>
        this.settings.ProfileToleranceDiameterMicrometer.ToString("F1", CultureInfo.CurrentCulture);

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    [RelayCommand]
    private Task CapturePointAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            MeasurementPoint point = await this.measurementService.CapturePointAsync(token).ConfigureAwait(true);
            Points.Add(new MeasurementRowViewModel(point));
            StatusResourceKey = "Measurement_PointCaptured";
        }, cancellationToken);

    [RelayCommand]
    private void ClearPoints()
    {
        Points.Clear();
        StatusResourceKey = string.Empty;
    }

    [RelayCommand]
    private Task SaveAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(JobId) || Points.Count < 2)
            {
                StatusResourceKey = "Measurement_NotEnoughPoints";
                return;
            }

            await this.measurementService.SaveAsync(
                JobId,
                Points.Select(row => row.Point).ToArray(),
                "manual",
                token).ConfigureAwait(true);

            StatusResourceKey = "Measurement_Saved";
        }, cancellationToken);

    [RelayCommand]
    private Task ComputeCompensationAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(JobId))
            {
                StatusResourceKey = "Measurement_JobIdMissing";
                return;
            }

            CompensationResult result = await this.compensationService
                .ComputeAndStoreAsync(JobId, token).ConfigureAwait(true);

            Deviation = result.Deviation;
            Compensation = result.Compensation;

            WorstDeviationText = result.Quality.WorstDeviationDiameterMicrometer
                .ToString("F2", CultureInfo.CurrentCulture);
            PeakToValleyText = result.Quality.PeakToValleyDiameterMicrometer
                .ToString("F2", CultureInfo.CurrentCulture);
            IsWithinTolerance = result.Quality.IsWithinToleranceDiameterMicrometer(
                this.settings.ProfileToleranceDiameterMicrometer);

            StatusResourceKey = "Measurement_CompensationStored";
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }, cancellationToken);
}
