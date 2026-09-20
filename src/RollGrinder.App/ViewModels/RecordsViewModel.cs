using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;

namespace RollGrinder.App.ViewModels;

/// <summary>记录列表里的一行。</summary>
public sealed class RecordRowViewModel
{
    public RecordRowViewModel(GrindingRecordView view, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(localizer);

        View = view;
        StateText = localizer["JobState_" + view.State];
        ProfileTypeText = string.IsNullOrEmpty(view.ProfileTypeKey)
            ? string.Empty
            : localizer["ProfileType_" + view.ProfileTypeKey];
    }

    public GrindingRecordView View { get; }

    public string RecordId => View.RecordId;

    public string RollCode => View.RollCode;

    public string ProfileTypeText { get; }

    public string StateText { get; }

    public string StartedText =>
        View.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string DurationText => View.Duration is null
        ? "--"
        : View.Duration.Value.TotalMinutes.ToString("F1", CultureInfo.CurrentCulture);

    public string WorstDeviationText => View.WorstDeviationDiameterMicrometer is null
        ? "--"
        : View.WorstDeviationDiameterMicrometer.Value.ToString("F2", CultureInfo.CurrentCulture);
}

/// <summary>磨削记录的查询、收尾与导出。</summary>
public sealed partial class RecordsViewModel : PageViewModelBase
{
    private readonly IRecordService recordService;

    public RecordsViewModel(
        IRecordService recordService,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));

        this.toDate = DateTime.Today;
        this.fromDate = DateTime.Today.AddDays(-7);

        SetFunctionKeys(new[]
        {
            new FunctionKeyViewModel("Fn_OpenRecord", QueryCommand, localizer, FunctionKeyKind.Primary),
            FunctionKeyViewModel.Placeholder("Fn_Latest", localizer, () => NotImplementedYet("Fn_Latest")),
            FunctionKeyViewModel.Placeholder("Fn_DailyReport", localizer, () => NotImplementedYet("Fn_DailyReport")),
            FunctionKeyViewModel.Placeholder("Fn_MonthlyReport", localizer, () => NotImplementedYet("Fn_MonthlyReport")),
            FunctionKeyViewModel.Placeholder("Fn_ExportExcel", localizer, () => NotImplementedYet("Fn_ExportExcel")),
            FunctionKeyViewModel.Placeholder("Fn_Print", localizer, () => NotImplementedYet("Fn_Print")),
            FunctionKeyViewModel.Placeholder("Fn_RollLedger", localizer, () => NotImplementedYet("Fn_RollLedger")),
        });
    }

    public override PageKey Key => PageKey.Records;

    public override string TitleResourceKey => "Page_Records";

    public override string MenuHintResourceKey => "Menu_RecordsHint";

    public override void OnActivated() => _ = QueryAsync(CancellationToken.None);

    public ObservableCollection<RecordRowViewModel> Records { get; } = new();

    /// <summary>选中记录的结果指标。目前记录了这些量，其余（磨前直径、圆度、同轴度等）
    /// 需要机床侧的测量通道先接进来。</summary>
    public ObservableCollection<LabelValueViewModel> Metrics { get; } = new();

    [ObservableProperty]
    private string summaryText = string.Empty;

    [ObservableProperty]
    private DateTime fromDate;

    [ObservableProperty]
    private DateTime toDate;

    [ObservableProperty]
    private RecordRowViewModel? selectedRecord;

    [ObservableProperty]
    private string note = string.Empty;

    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : Localizer[StatusResourceKey];

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnSelectedRecordChanged(RecordRowViewModel? value)
    {
        Metrics.Clear();
        if (value is null)
        {
            return;
        }

        Metrics.Add(new LabelValueViewModel("Metric_RollCode", value.RollCode, Localizer));
        Metrics.Add(new LabelValueViewModel("Metric_Profile", value.ProfileTypeText, Localizer));
        Metrics.Add(new LabelValueViewModel("Metric_State", value.StateText, Localizer));
        Metrics.Add(new LabelValueViewModel("Metric_Started", value.StartedText, Localizer));
        Metrics.Add(new LabelValueViewModel("Metric_Duration", value.DurationText, Localizer));
        Metrics.Add(new LabelValueViewModel("Metric_WorstDeviation", value.WorstDeviationText, Localizer));
    }

    [RelayCommand]
    public Task QueryAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            var fromUtc = new DateTimeOffset(FromDate.Date, TimeSpan.Zero);
            var toUtc = new DateTimeOffset(ToDate.Date.AddDays(1), TimeSpan.Zero);

            Records.Clear();
            foreach (GrindingRecordView view in await this.recordService
                .QueryAsync(fromUtc, toUtc, 500, token).ConfigureAwait(true))
            {
                Records.Add(new RecordRowViewModel(view, Localizer));
            }

            StatusResourceKey = Records.Count == 0 ? "Records_Empty" : "Records_Loaded";
            SummaryText = Localizer.Format("Records_SummaryFormat", Records.Count);
            SelectedRecord = Records.FirstOrDefault();
        }, cancellationToken);

    [RelayCommand]
    private Task FinishSelectedAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            if (SelectedRecord is null)
            {
                StatusResourceKey = "Records_NoSelection";
                return;
            }

            await this.recordService.FinishAsync(
                SelectedRecord.RecordId,
                JobState.Completed,
                string.IsNullOrWhiteSpace(Note) ? null : Note,
                token).ConfigureAwait(true);

            StatusResourceKey = "Records_Finished";
            await QueryAsync(token).ConfigureAwait(true);
        }, cancellationToken);

    /// <summary>导出当前列表；路径由界面选定。</summary>
    public Task ExportAsync(string filePath, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            await this.recordService.ExportCsvAsync(
                Records.Select(row => row.View).ToArray(),
                filePath,
                token).ConfigureAwait(true);

            StatusResourceKey = "Records_Exported";
        }, cancellationToken);
}
