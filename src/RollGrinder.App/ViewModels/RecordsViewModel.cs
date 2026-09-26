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
using RollGrinder.Core.Time;
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
    private readonly IReportService reportService;

    public RecordsViewModel(
        IRecordService recordService,
        IReportService reportService,
        IRollLedgerService ledgerService,
        JobDraft jobDraft,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator)
        : base(alarms, localizer, navigator)
    {
        this.recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        this.reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        this.ledgerService = ledgerService ?? throw new ArgumentNullException(nameof(ledgerService));
        this.jobDraft = jobDraft ?? throw new ArgumentNullException(nameof(jobDraft));

        this.toDate = DateTime.Today;
        this.fromDate = DateTime.Today.AddDays(-7);

        SetFunctionKeys(new[]
        {
            new FunctionKeyViewModel("Fn_OpenRecord", QueryCommand, localizer, FunctionKeyKind.Primary),
            new FunctionKeyViewModel("Fn_PreGrindReport", PreviewPreGrindReportCommand, localizer),
            new FunctionKeyViewModel("Fn_DailyReport", ShowDailySummaryCommand, localizer),
            new FunctionKeyViewModel("Fn_MonthlyReport", ShowMonthlySummaryCommand, localizer),

            // 导出要挑一个文件路径，对话框在视图里；这个键只是把范围定好再交给它。
            new FunctionKeyViewModel("Fn_ExportExcel", RequestExportCommand, localizer),
            new FunctionKeyViewModel("Fn_Print", PreviewPostGrindReportCommand, localizer),
            new FunctionKeyViewModel("Fn_RollLedger", OpenLedgerCommand, localizer),
        });
    }

    public override PageKey Key => PageKey.Records;

    /// <summary>离线可用：记录都在数据库里，查与打印都不需要机床。</summary>
    public override bool WorksOffline => true;

    public override string TitleResourceKey => "Page_Records";

    public override string MenuHintResourceKey => "Menu_RecordsHint";

    public override void OnActivated()
    {
        if (this.jobDraft.RegisterNewRollRequested)
        {
            // 作业页派来登记一支新辊：直接开台账、给一张新表；存好后按导航槽回作业页就选上它。
            this.jobDraft.RegisterNewRollRequested = false;
            this.registeringForJob = true;
            _ = RunGuardedAsync(
                async token =>
                {
                    await ReloadLedgerAsync(null, token).ConfigureAwait(true);
                    NewLedgerRoll();
                    Navigator.OpenSubView(LedgerSubView);
                },
                CancellationToken.None);
            return;
        }

        _ = QueryAsync(CancellationToken.None);
    }

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

        // 先摆一屏 "--"：指标要查库，别让格子在读完之前是空的。
        ShowOutcome(GrindingOutcome.Empty);
        RefreshCurve();
        _ = RunGuardedAsync(async token =>
        {
            GrindingOutcome outcome = await this.recordService
                .LoadOutcomeAsync(value.RecordId, token).ConfigureAwait(true);

            // 读的过程中人可能已经点了别的记录，那就别把旧结果摆上去。
            if (ReferenceEquals(SelectedRecord, value))
            {
                ShowOutcome(outcome);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 设计稿 B-Records 的"磨削结果"那 12 项，顺序照设计稿。
    ///
    /// 每一项算不出来就是 "--"：**"没量过"与"量出来是 0"是两回事**，
    /// 填一个 0 会让人以为这支辊量过了。
    /// </summary>
    private void ShowOutcome(GrindingOutcome outcome)
    {
        Metrics.Clear();

        Add("Metric_PreDiameterHead", outcome.PreGrindDiameterHeadMm, "F3");
        Add("Metric_PreDiameterTail", outcome.PreGrindDiameterTailMm, "F3");
        Add("Metric_PostDiameterHead", outcome.PostGrindDiameterHeadMm, "F3");
        Add("Metric_PostDiameterTail", outcome.PostGrindDiameterTailMm, "F3");
        Add("Metric_Taper", outcome.TaperMm, "F3", showSign: true);
        Add("Metric_ProfileRms", outcome.ProfileRmsMicrometer, "F1");
        Add("Metric_Roundness", outcome.RoundnessMicrometer, "F1");
        Add("Metric_Concentricity", outcome.ConcentricityMicrometer, "F1");
        Add("Metric_ActualCrown", outcome.ActualCrownMm, "F4", showSign: true);
        Add("Metric_WheelDiameter", outcome.WheelDiameterMm, "F2");

        Metrics.Add(new LabelValueViewModel(
            "Metric_Duration",
            outcome.Duration is TimeSpan duration
                ? duration.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
                : Dash,
            Localizer));

        Metrics.Add(new LabelValueViewModel(
            "Metric_CompensationIterations",
            outcome.CompensationIterations.ToString(CultureInfo.CurrentCulture),
            Localizer));

        void Add(string labelResourceKey, double? value, string format, bool showSign = false)
        {
            string text = value is null
                ? Dash
                : value.Value.ToString(format, CultureInfo.CurrentCulture);
            if (showSign && value is double number && number >= 0.0)
            {
                text = "+" + text;
            }

            Metrics.Add(new LabelValueViewModel(labelResourceKey, text, Localizer));
        }
    }

    /// <summary>算不出来时格子里写什么。</summary>
    private const string Dash = "--";

    /// <summary>当前这张曲线。界面拿它去画；没有数据时 HasData 为 false。</summary>
    public RecordCurve Curve { get; private set; } = RecordCurve.Empty(RecordCurveKind.BeforeAfterProfile);

    /// <summary>曲线换了，界面该重画。</summary>
    public event EventHandler? CurveChanged;

    [ObservableProperty]
    private RecordCurveKind selectedCurve = RecordCurveKind.BeforeAfterProfile;

    /// <summary>没有数据时写在图上的那句话。</summary>
    [ObservableProperty]
    private string curveEmptyText = string.Empty;

    /// <summary>有没有画得出来的线。</summary>
    [ObservableProperty]
    private bool curveHasData;

    [RelayCommand]
    private void SelectCurve(RecordCurveKind kind)
    {
        SelectedCurve = kind;
        RefreshCurve();
    }

    private void RefreshCurve()
    {
        RecordRowViewModel? selected = SelectedRecord;
        if (selected is null)
        {
            Curve = RecordCurve.Empty(SelectedCurve);
            CurveHasData = false;
            CurveEmptyText = Localizer["Records_NoSelection"];
            CurveChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _ = RunGuardedAsync(async token =>
        {
            RecordCurve curve = await this.recordService
                .LoadCurveAsync(selected.RecordId, SelectedCurve, token).ConfigureAwait(true);

            // 读的过程中人可能已经点了别的记录或别的曲线。
            if (!ReferenceEquals(SelectedRecord, selected) || curve.Kind != SelectedCurve)
            {
                return;
            }

            Curve = curve;
            CurveHasData = curve.HasData;

            // 这支辊没有这项数据就照实说，不画一条编出来的线。
            CurveEmptyText = curve.HasData ? string.Empty : Localizer["Records_CurveNoData"];
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }, CancellationToken.None);
    }

    [RelayCommand]
    public Task QueryAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            // 查询日期是操作员所在地的日历日，不是 UTC 日。
            (DateTimeOffset fromUtc, DateTimeOffset toUtc) = LocalDays.Range(FromDate, ToDate, TimeZoneInfo.Local);

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

            // 磨完的辊现在由 NC 的结束位自动收尾；已经收过尾的不再动——
            // 再收一次会改掉当时的结束时间与状态，还会多打一张磨削报告。
            if (SelectedRecord.View.FinishedAtUtc is not null)
            {
                StatusResourceKey = "Records_AlreadyFinished";
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

    /// <summary>报表预览子视图的资源键，同时用作面包屑文案。</summary>
    public const string ReportSubView = "SubView_Report";

    /// <summary>轧辊台账子视图的资源键。</summary>
    public const string LedgerSubView = "SubView_RollLedger";

    /// <summary>轧辊台账的行。</summary>
    public ObservableCollection<RollLedgerRowViewModel> Ledger { get; } = new();

    /// <summary>日报 / 月报的那一行汇总。</summary>
    [ObservableProperty]
    private string summaryLineText = string.Empty;

    /// <summary>界面要导出时触发；路径由视图选。</summary>
    public event EventHandler? ExportRequested;

    /// <summary>今天磨了什么。</summary>
    [RelayCommand]
    private Task ShowDailySummaryAsync(CancellationToken cancellationToken) =>
        SummariseAsync(DateTime.Today, DateTime.Today, "Records_DailySummaryFormat", cancellationToken);

    /// <summary>这个月磨了什么。</summary>
    [RelayCommand]
    private Task ShowMonthlySummaryAsync(CancellationToken cancellationToken)
    {
        DateTime first = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        return SummariseAsync(first, first.AddMonths(1).AddDays(-1), "Records_MonthlySummaryFormat", cancellationToken);
    }

    /// <summary>
    /// 汇总一段时间。日报与月报是同一件事，差别只在取哪一段——
    /// 顺带把列表的日期范围也设成那一段，让人看得见汇总说的是哪些记录。
    /// </summary>
    private Task SummariseAsync(
        DateTime from, DateTime to, string formatResourceKey, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            FromDate = from;
            ToDate = to;
            await QueryAsync(token).ConfigureAwait(true);

            (DateTimeOffset fromUtc, DateTimeOffset toUtc) = LocalDays.Range(from, to, TimeZoneInfo.Local);
            GrindingSummary summary = await this.recordService.SummariseAsync(fromUtc, toUtc, token).ConfigureAwait(true);

            // 一支都没磨时合格率是"--"不是 0%：
            // "这段时间没干活"与"干了活全不合格"是两回事。
            SummaryLineText = Localizer.Format(
                formatResourceKey,
                summary.TotalCount,
                summary.CompletedCount,
                summary.CompletionRate is double rate
                    ? rate.ToString("P1", CultureInfo.CurrentCulture)
                    : Dash,
                summary.TotalDuration.TotalHours.ToString("F1", CultureInfo.CurrentCulture));
        }, cancellationToken);

    /// <summary>打开轧辊台账。</summary>
    [RelayCommand]
    private Task OpenLedgerAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            Ledger.Clear();
            await ReloadLedgerAsync(null, token).ConfigureAwait(true);
            StatusResourceKey = Ledger.Count == 0 ? "Records_LedgerEmpty" : string.Empty;
            Navigator.OpenSubView(LedgerSubView);
        }, cancellationToken);

    /// <summary>导出：路径由视图上的文件对话框选。</summary>
    [RelayCommand]
    private void RequestExport() => ExportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 预览里那张报表。界面层拿它排版、打印；没有选中记录时为 null。
    ///
    /// 视图模型只持有**内容**，不持有 FlowDocument——排版是界面层的事，
    /// 这样同一份内容也能被测试直接核对。
    /// </summary>
    public GrindingReport? Report { get; private set; }

    /// <summary>报表变了，界面该重排。</summary>
    public event EventHandler? ReportChanged;

    /// <summary>磨前报表：这支辊准备按什么磨。</summary>
    [RelayCommand]
    private Task PreviewPreGrindReportAsync(CancellationToken cancellationToken) =>
        PreviewReportAsync(ReportKind.PreGrind, cancellationToken);

    /// <summary>磨后报表：这支辊实际磨成了什么样。</summary>
    [RelayCommand]
    private Task PreviewPostGrindReportAsync(CancellationToken cancellationToken) =>
        PreviewReportAsync(ReportKind.PostGrind, cancellationToken);

    private Task PreviewReportAsync(ReportKind kind, CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            if (SelectedRecord is null)
            {
                StatusResourceKey = "Records_NoSelection";
                return;
            }

            Report = await this.reportService
                .BuildAsync(SelectedRecord.RecordId, kind, token).ConfigureAwait(true);

            if (Report is null)
            {
                // 记录在、作业不在：那条记录追溯不到按什么磨的，打出来也是半张纸。
                StatusResourceKey = "Report_JobMissing";
                return;
            }

            StatusResourceKey = string.Empty;
            ReportChanged?.Invoke(this, EventArgs.Empty);
            Navigator.OpenSubView(ReportSubView);
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
