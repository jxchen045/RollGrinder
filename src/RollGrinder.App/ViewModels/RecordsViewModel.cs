using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
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
public sealed partial class RecordsViewModel : ViewModelBase
{
    private readonly IRecordService recordService;
    private readonly IStringLocalizer localizer;

    public RecordsViewModel(IRecordService recordService, IStringLocalizer localizer, IAlarmSink alarms)
        : base(alarms)
    {
        this.recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));

        this.toDate = DateTime.Today;
        this.fromDate = DateTime.Today.AddDays(-7);
    }

    public ObservableCollection<RecordRowViewModel> Records { get; } = new();

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

    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : this.localizer[StatusResourceKey];

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

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
                Records.Add(new RecordRowViewModel(view, this.localizer));
            }

            StatusResourceKey = Records.Count == 0 ? "Records_Empty" : "Records_Loaded";
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
