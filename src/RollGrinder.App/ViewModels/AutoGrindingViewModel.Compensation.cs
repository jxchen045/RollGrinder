using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Core;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Units;
using RollGrinder.Data.Model;
using RollGrinder.Services.Measurement;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>补偿子视图里的一次行程间修正。</summary>
/// <param name="VersionText">第几版。</param>
/// <param name="TimeText">上位机看到的时刻。</param>
/// <param name="OffsetText">修正量（直径量 µm）。</param>
public sealed record StrokeCorrectionRow(string VersionText, string TimeText, string OffsetText);

/// <summary>补偿子视图里的一次测量：它离目标还差多少——看补偿是不是在收敛。</summary>
public sealed record MeasurementConvergenceRow(string TimeText, string StageText, string RmsText, string PeakToValleyText);

/// <summary>
/// 自动页的补偿子视图（修改稿 3②、5.5）：NC 行程间补偿的状态与收敛过程、这支辊各次测量离目标多远，
/// 以及上位机算补偿用的增益、平滑、限幅——制造商可改，改动写改动记录。
/// 原来这些只在工艺程序页只读显示（甲方测试 3②：补偿不该跑到工序里去），现在都在这里。
/// </summary>
public sealed partial class AutoGrindingViewModel
{
    /// <summary>补偿子视图的资源键，同时用作面包屑文案。</summary>
    public const string CompensationSubView = "SubView_Compensation";

    private ICompensationTuningService tuning = null!;
    private IStrokeCompensationLog strokeLog = null!;
    private int seenStrokeCount = -1;
    private AsyncRelayCommand saveTuningCommand = null!;
    private AsyncRelayCommand resetTuningCommand = null!;

    /// <summary>NC 的各次行程间修正，新的在前。</summary>
    public ObservableCollection<StrokeCorrectionRow> StrokeCorrections { get; } = new();

    /// <summary>收敛曲线的点：横轴行程版本，纵轴修正量（直径量 µm）。视图画图用。</summary>
    public IReadOnlyList<(double Version, double OffsetMicrometer)> StrokeCurve { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>NC 已经修正过（有东西可画）。</summary>
    [ObservableProperty]
    private bool hasStrokeCorrections;

    /// <summary>收敛曲线变了（视图重画）。</summary>
    public event EventHandler? StrokeCurveChanged;

    /// <summary>这支辊的各次测量离目标多远，新的在前。</summary>
    public ObservableCollection<MeasurementConvergenceRow> MeasurementConvergence { get; } = new();

    [ObservableProperty]
    private string tuningGainText = string.Empty;

    [ObservableProperty]
    private string tuningSmoothingText = string.Empty;

    [ObservableProperty]
    private string tuningMaxCorrectionText = string.Empty;

    /// <summary>保存结果或哪一项不对。</summary>
    [ObservableProperty]
    private string tuningStatusText = string.Empty;

    /// <summary>现在用的是配置文件的值还是改过的值。</summary>
    public string TuningSourceText => Localizer[this.tuning.IsOverridden ? "Comp_SourceOverridden" : "Comp_SourceConfigured"];

    /// <summary>限幅能填多大：machine.json 的那个数是上限。</summary>
    public string TuningLimitText => this.tuning.MachineLimitMicrometer is double limit
        ? Localizer.Format("Comp_LimitFormat", limit)
        : Localizer["Common_NotConfigured"];

    /// <summary>当前登录能不能改补偿设定（只有制造商能改，Q9）。</summary>
    public bool CanEditCompensation => Can(Permission.EditCompensation);

    /// <summary>补偿子视图开着没有。</summary>
    public bool IsCompensationOpen => ActiveSubViewKey == CompensationSubView;

    private void InitializeCompensation(ICompensationTuningService tuningService, IStrokeCompensationLog log)
    {
        this.tuning = tuningService ?? throw new ArgumentNullException(nameof(tuningService));
        this.strokeLog = log ?? throw new ArgumentNullException(nameof(log));
        this.tuning.Changed += (_, _) => OnUiThread(LoadTuningTexts);

        // 保存、恢复都先问一句（最终稿 4.5）：改的是 NC 行程间补偿用的增益与限幅。
        this.saveTuningCommand = new AsyncRelayCommand(
            () =>
            {
                Ask("Comp_AskSave", () => SaveTuningAsync(CancellationToken.None));
                return Task.CompletedTask;
            },
            () => IsCompensationOpen);
        this.resetTuningCommand = new AsyncRelayCommand(
            () =>
            {
                Ask("Comp_AskReset", () => ResetTuningAsync(CancellationToken.None));
                return Task.CompletedTask;
            },
            () => IsCompensationOpen);
        PropertyChanged += OnCompensationPropertyChanged;
        LoadTuningTexts();
    }

    private void OnCompensationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveSubViewKey))
        {
            OnPropertyChanged(nameof(IsCompensationOpen));
            this.saveTuningCommand.NotifyCanExecuteChanged();
            this.resetTuningCommand.NotifyCanExecuteChanged();
        }
    }

    protected override void OnAccessChanged() => OnPropertyChanged(nameof(CanEditCompensation));

    private void OpenCompensation()
    {
        Navigator.OpenSubView(CompensationSubView);
        LoadTuningTexts();
        TuningStatusText = string.Empty;
        this.seenStrokeCount = -1;
        RefreshStrokeCorrections();
        _ = RunGuardedAsync(RefreshMeasurementConvergenceAsync, CancellationToken.None);
    }

    /// <summary>界面节拍里调：子视图开着才刷。</summary>
    private void TickCompensation()
    {
        if (IsCompensationOpen)
        {
            RefreshStrokeCorrections();
        }
    }

    private void RefreshStrokeCorrections()
    {
        IReadOnlyList<StrokeCorrection> corrections = this.strokeLog.Snapshot();
        if (corrections.Count == this.seenStrokeCount)
        {
            return;
        }

        this.seenStrokeCount = corrections.Count;
        StrokeCorrections.Clear();
        foreach (StrokeCorrection correction in corrections.Reverse())
        {
            StrokeCorrections.Add(new StrokeCorrectionRow(
                "v " + correction.Version.ToString(CultureInfo.InvariantCulture),
                correction.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                correction.OffsetMm is double offset
                    ? UnitConversion.RadiusMmToDiameterMicrometer(offset).ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)
                    : "--"));
        }

        StrokeCurve = corrections
            .Where(correction => correction.OffsetMm is not null)
            .Select(correction => ((double)correction.Version, UnitConversion.RadiusMmToDiameterMicrometer(correction.OffsetMm!.Value)))
            .ToArray();
        HasStrokeCorrections = StrokeCorrections.Count > 0;
        StrokeCurveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>这支辊的各次测量各离目标多远：补偿起作用的话，均方根应该一次比一次小。</summary>
    private async Task RefreshMeasurementConvergenceAsync(CancellationToken cancellationToken)
    {
        MeasurementConvergence.Clear();
        if (this.activeJob is null)
        {
            return;
        }

        RollProfile target = TargetProfile();
        IReadOnlyList<MeasurementRecord> records = await this.measurements
            .ListByJobAsync(this.activeJob.JobId, 50, cancellationToken).ConfigureAwait(true);
        foreach (MeasurementRecord record in records.OrderByDescending(record => record.RecordedAtUtc))
        {
            RollProfile deviation = CompensationCalculator.ComputeDeviation(record.Profile, target, this.activeJob.Geometry);
            if (deviation.Points.Count == 0)
            {
                continue;
            }

            double rms = Math.Sqrt(deviation.Points.Average(point =>
            {
                double micrometer = UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm);
                return micrometer * micrometer;
            }));
            MeasurementConvergence.Add(new MeasurementConvergenceRow(
                record.RecordedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                Localizer["MeasurementStage_" + record.Stage],
                rms.ToString("F1", CultureInfo.CurrentCulture),
                ProfileQuality.FromDeviation(deviation).PeakToValleyDiameterMicrometer.ToString("F1", CultureInfo.CurrentCulture)));
        }
    }

    private void LoadTuningTexts()
    {
        CompensationTuning current = this.tuning.Current;
        TuningGainText = current.Gain.ToString("0.###", CultureInfo.CurrentCulture);
        TuningSmoothingText = current.SmoothingPoints.ToString(CultureInfo.CurrentCulture);
        TuningMaxCorrectionText = current.MaxCorrectionMicrometer is double max
            ? max.ToString("0.#", CultureInfo.CurrentCulture)
            : string.Empty;
        OnPropertyChanged(nameof(TuningSourceText));
        OnPropertyChanged(nameof(TuningLimitText));
    }

    private async Task SaveTuningAsync(CancellationToken cancellationToken)
    {
        if (!CanEditCompensation)
        {
            TuningStatusText = Localizer["Comp_NeedsManufacturer"];
            return;
        }

        if (!TryParse(TuningGainText, out double gain)
            || !int.TryParse(TuningSmoothingText, NumberStyles.Integer, CultureInfo.CurrentCulture, out int smoothing))
        {
            TuningStatusText = Localizer["Comp_NotANumber"];
            return;
        }

        double? maxCorrection = null;
        if (!string.IsNullOrWhiteSpace(TuningMaxCorrectionText))
        {
            if (!TryParse(TuningMaxCorrectionText, out double parsed))
            {
                TuningStatusText = Localizer["Comp_NotANumber"];
                return;
            }

            maxCorrection = parsed;
        }

        var edited = new CompensationTuning(gain, smoothing, maxCorrection ?? this.tuning.Configured.MaxCorrectionMicrometer);
        if (edited.FindInvalid(this.tuning.MachineLimitMicrometer) is string invalid)
        {
            TuningStatusText = Localizer[invalid switch
            {
                CompensationTuningKeys.Gain => "Comp_InvalidGain",
                CompensationTuningKeys.SmoothingPoints => "Comp_InvalidSmoothing",
                _ => "Comp_InvalidMaxCorrection",
            }];
            return;
        }

        await RunGuardedAsync(
            token => this.tuning.SaveAsync(edited, this.userSession.CurrentUser?.UserName ?? string.Empty, token),
            cancellationToken).ConfigureAwait(true);
        TuningStatusText = Localizer["Comp_Saved"];
        Interaction.Say(TuningStatusText);
    }

    private async Task ResetTuningAsync(CancellationToken cancellationToken)
    {
        if (!CanEditCompensation)
        {
            TuningStatusText = Localizer["Comp_NeedsManufacturer"];
            return;
        }

        await RunGuardedAsync(
            token => this.tuning.ResetAsync(this.userSession.CurrentUser?.UserName ?? string.Empty, token),
            cancellationToken).ConfigureAwait(true);
        TuningStatusText = Localizer["Comp_Reset"];
        Interaction.Say(TuningStatusText);
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
