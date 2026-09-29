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
    private readonly JobDraft jobDraft;
    private readonly Dictionary<RecordCurveKind, FunctionKeyViewModel> curveKeys = new();
    private readonly FunctionKeyViewModel recordsKey;
    private readonly FunctionKeyViewModel cancelQueryKey;
    private readonly FunctionKeyViewModel confirmQueryKey;

    public RecordsViewModel(
        IRecordService recordService,
        IReportService reportService,
        JobDraft jobDraft,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        this.reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        this.jobDraft = jobDraft ?? throw new ArgumentNullException(nameof(jobDraft));

        this.toDate = DateTime.Today;
        this.fromDate = DateTime.Today.AddDays(-7);

        // 横键（最终稿 5.11）：磨削记录 · 磨前报表 · 空 · 日报 · 月报。轧辊台账挪去了库区。
        this.recordsKey = FunctionKeyViewModel.ForAction("Fn_GrindingRecords", localizer, () => Navigator.CloseSubView());
        SetFunctionKeys(new FunctionKeyViewModel?[]
        {
            this.recordsKey,
            new FunctionKeyViewModel("Fn_PreGrindReport", PreviewPreGrindReportCommand, localizer),
            null,
            new FunctionKeyViewModel("Fn_DailyReport", ShowDailySummaryCommand, localizer),
            new FunctionKeyViewModel("Fn_MonthlyReport", ShowMonthlySummaryCommand, localizer),
        });
        MarkActiveFunctionKey(this.recordsKey);

        foreach ((RecordCurveKind kind, string label) in new[]
        {
            (RecordCurveKind.BeforeAfterProfile, "Curve_BeforeAfter"),
            (RecordCurveKind.Deviation, "Curve_Error"),
            (RecordCurveKind.Roundness, "Curve_Roundness"),
            (RecordCurveKind.CompensationConvergence, "Curve_Convergence"),
        })
        {
            RecordCurveKind chosen = kind;
            this.curveKeys[kind] = FunctionKeyViewModel.ForAction(label, localizer, () => SelectCurve(chosen));
        }

        this.cancelQueryKey = new FunctionKeyViewModel("Vk_Cancel", new RelayCommand(() => Navigator.CloseSubView()), localizer, FunctionKeyKind.Cancel);
        this.confirmQueryKey = new FunctionKeyViewModel("Vk_Query", new AsyncRelayCommand(RunQueryAsync), localizer, FunctionKeyKind.Confirm);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveSubViewKey))
            {
                ApplyVerticalKeys();
            }
        };
        ApplyVerticalKeys();
    }

    /// <summary>查询子功能（竖键"查询…"）：起止日期，竖键 7 / 8 = 取消 / 查询。</summary>
    public const string QuerySubView = "SubView_Query";

    public bool IsQueryOpen => ActiveSubViewKey == QuerySubView;

    /// <summary>
    /// 竖键（最终稿 5.11）：1–4 选曲线（磨前 / 磨后、误差、圆度、补偿收敛，和自动页同一套"竖键选曲线"），
    /// 5–8 查询…、标记完成…、打印、导出 Excel。报表预览里是"打印"和"« 返回"。
    /// </summary>
    private void ApplyVerticalKeys()
    {
        OnPropertyChanged(nameof(IsQueryOpen));
        if (ActiveSubViewKey == ReportSubView)
        {
            SetCommitPair(null, null);
            SetVerticalKeys(new FunctionKeyViewModel?[]
            {
                FunctionKeyViewModel.ForAction("Vk_PrintNow", Localizer, () => PrintRequested?.Invoke(this, EventArgs.Empty)),
                null, null, null, null, null, null,
                new FunctionKeyViewModel("Vk_Back", new RelayCommand(Navigator.CloseSubView), Localizer, FunctionKeyKind.Navigation),
            });
            return;
        }

        SetVerticalKeys(new FunctionKeyViewModel?[]
        {
            this.curveKeys[RecordCurveKind.BeforeAfterProfile],
            this.curveKeys[RecordCurveKind.Deviation],
            this.curveKeys[RecordCurveKind.Roundness],
            this.curveKeys[RecordCurveKind.CompensationConvergence],
            FunctionKeyViewModel.ForAction("Vk_QueryAsk", Localizer, () => Navigator.OpenSubView(QuerySubView)),
            new FunctionKeyViewModel("Vk_MarkFinished", new RelayCommand(AskFinish), Localizer) { PreconditionResourceKey = "Records_NoSelection" },
            new FunctionKeyViewModel("Vk_Print", PreviewPostGrindReportCommand, Localizer),
            new FunctionKeyViewModel("Vk_ExportExcel", RequestExportCommand, Localizer),
        });
        MarkCurve();
        SetCommitPair(IsQueryOpen ? this.cancelQueryKey : null, IsQueryOpen ? this.confirmQueryKey : null);
    }

    private void MarkCurve()
    {
        foreach ((RecordCurveKind kind, FunctionKeyViewModel key) in this.curveKeys)
        {
            key.IsActive = kind == SelectedCurve;
        }
    }

    /// <summary>报表预览里按"打印"：排版在视图里，视图接这个事件去打。</summary>
    public event EventHandler? PrintRequested;

    private async Task RunQueryAsync()
    {
        Navigator.CloseSubView();
        await QueryAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>标记完成…：问一句再收尾（备注一并存）。</summary>
    private void AskFinish()
    {
        if (SelectedRecord is null)
        {
            Interaction.Refuse(Localizer["Records_NoSelection"]);
            return;
        }

        Ask("Records_AskFinish", () => FinishSelectedAsync(CancellationToken.None), SelectedRecord.RollCode);
    }

    /// <summary>查询的快捷范围：今天、近 7 天、近 30 天。</summary>
    [RelayCommand]
    private void SetRange(string days)
    {
        int count = int.Parse(days, CultureInfo.InvariantCulture);
        ToDate = DateTime.Today;
        FromDate = DateTime.Today.AddDays(-(count - 1));
    }

    public override PageKey Key => PageKey.Records;

    /// <summary>离线可用：记录都在数据库里，查与打印都不需要机床。</summary>
    public override bool WorksOffline => true;

    public override string TitleResourceKey => "Page_Records";


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

    partial void OnStatusResourceKeyChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText));
        if (value.Length > 0)
        {
            Interaction.Say(Localizer[value]);
        }
    }

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

    private void SelectCurve(RecordCurveKind kind)
    {
        SelectedCurve = kind;
        MarkCurve();
        RefreshCurve();
    }

    /// <summary>曲线窗标题：现在看的是哪一条。</summary>
    public string CurveTitle => Localizer[SelectedCurve switch
    {
        RecordCurveKind.Deviation => "Curve_Error",
        RecordCurveKind.Roundness => "Curve_Roundness",
        RecordCurveKind.CompensationConvergence => "Curve_Convergence",
        _ => "Curve_BeforeAfter",
    }];

    partial void OnSelectedCurveChanged(RecordCurveKind value) => OnPropertyChanged(nameof(CurveTitle));

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

            // 从自动页"磨削记录（本支辊）"或库 › 作业"打开"过来的：选中那一份。
            string? wanted = this.jobDraft.RecordsJobId;
            this.jobDraft.RecordsJobId = null;
            SelectedRecord = Records.FirstOrDefault(row => wanted is not null && row.View.JobId == wanted) ?? Records.FirstOrDefault();
        }, cancellationToken);

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
