using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.Core.Steps;
using RollGrinder.Data.Model;
using RollGrinder.Services.Records;

namespace RollGrinder.App.ViewModels;

/// <summary>道次表看哪些行（设计稿 v4 5.3.4）。</summary>
public enum PassFilter
{
    /// <summary>正在磨（或选中）的这一道工序。</summary>
    CurrentStep = 0,

    /// <summary>全部。</summary>
    All = 1,

    /// <summary>只看越界（倍率 &lt; 50 % / &gt; 110 % / 整道不在 100 %）。</summary>
    OutOfBound = 2,
}

/// <summary>道次表的一行：工序、道次、左右折返实际 Z / 停留、折返时的进给倍率、补偿量、平均电流。</summary>
public sealed record PassRowViewModel(
    string StepText,
    string PassText,
    string LeftZText,
    string RightZText,
    string DwellText,
    string OverrideText,
    string CompensationText,
    string CurrentText,
    bool IsCurrent,
    bool IsOffNominal,
    bool IsOutOfBound)
{
    /// <summary>正在磨的那一道前面标 ▶。</summary>
    public string Marker => IsCurrent ? "▶" : IsOutOfBound ? "✕" : string.Empty;
}

/// <summary>一道工序的摘要行：设定 / 本次 / 平均倍率 / 有效速度 / 折返散布 / 补偿走向 / 越界次数。</summary>
public sealed record StepSummaryRowViewModel(string StepText, string FeedText, string OverrideText, string EffectiveText, string ReversalText, string CompensationText, string OutOfBoundText);

/// <summary>
/// 道次表（设计说明第 6 节、设计稿 v4 5.3.4 / 5.8.2）：自动页看正在磨的这一支，记录页看存下来的那一支。
/// 行按工序分组，倍率不在 100 % 的加粗、低于 50 % 的标 ✕；下面是逐工序摘要（含有效值）与事件。
/// </summary>
public sealed partial class PassTableViewModel : ObservableObject
{
    private readonly IStringLocalizer localizer;
    private IReadOnlyList<RecordPassRow> passes = Array.Empty<RecordPassRow>();
    private IReadOnlyList<OverrideSummaryRow> overrides = Array.Empty<OverrideSummaryRow>();
    private IReadOnlyList<RunEvent> events = Array.Empty<RunEvent>();
    private IReadOnlyList<StepRunSummary> summaries = Array.Empty<StepRunSummary>();
    private GrindingJob? job;
    private int currentStep;
    private int currentPass;

    public PassTableViewModel(IStringLocalizer localizer)
    {
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public ObservableCollection<PassRowViewModel> Rows { get; } = new();

    public ObservableCollection<StepSummaryRowViewModel> Summaries { get; } = new();

    /// <summary>事件（时间 · 种类 · 第几道 · 值）。</summary>
    public ObservableCollection<LabelValueViewModel> Events { get; } = new();

    [ObservableProperty]
    private PassFilter filter = PassFilter.CurrentStep;

    /// <summary>缓冲状态："NC 已写 120 · 已读 118 · 16 槽 · 溢出 0"；记录页是"共 n 道"。</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>"道次表不完整，缺第 3–5 道"；不缺为空。</summary>
    [ObservableProperty]
    private string missingText = string.Empty;

    [ObservableProperty]
    private string title = string.Empty;

    public bool HasRows => Rows.Count > 0;

    partial void OnFilterChanged(PassFilter value) => Rebuild();

    /// <summary>换一份数据（记录页一次性给全；自动页每秒刷新）。</summary>
    public void Load(
        GrindingJob? job,
        IReadOnlyList<RecordPassRow> passes,
        IReadOnlyList<OverrideSummaryRow> overrides,
        IReadOnlyList<RunEvent> events,
        IReadOnlyList<StepRunSummary> summaries,
        string missingRanges,
        string statusText,
        int currentStep = 0,
        int currentPass = 0)
    {
        this.job = job;
        this.passes = passes ?? Array.Empty<RecordPassRow>();
        this.overrides = overrides ?? Array.Empty<OverrideSummaryRow>();
        this.events = events ?? Array.Empty<RunEvent>();
        this.summaries = summaries ?? Array.Empty<StepRunSummary>();
        this.currentStep = currentStep;
        this.currentPass = currentPass;
        MissingText = string.IsNullOrEmpty(missingRanges) ? string.Empty : this.localizer.Format("Pass_MissingFormat", missingRanges);
        StatusText = statusText;
        Title = this.localizer.Format("Pass_TitleFormat", this.passes.Count, this.events.Count(item => RunEventKinds.IsOutOfBound(item.Kind)));
        Rebuild();
    }

    private string StepName(int order) =>
        this.job?.Steps.FirstOrDefault(step => step.Order == order) is { } step
            ? this.localizer.Format("Adjust_StepFormat", order, this.localizer["StepType_" + step.StepTypeKey])
            : order.ToString(CultureInfo.CurrentCulture);

    private static string Number(double? value, string format) =>
        value is double v && double.IsFinite(v) ? v.ToString(format, CultureInfo.CurrentCulture) : "--";

    private void Rebuild()
    {
        Rows.Clear();
        HashSet<(int, int)> outOfBound = this.events
            .Where(item => RunEventKinds.IsOutOfBound(item.Kind) && item.StepOrder is not null && item.Pass is not null)
            .Select(item => (item.StepOrder!.Value, item.Pass!.Value))
            .ToHashSet();
        int focus = this.currentStep > 0 ? this.currentStep : this.passes.LastOrDefault()?.StepOrder ?? 0;
        foreach (RecordPassRow row in this.passes)
        {
            OverrideSummaryRow? feed = this.overrides.FirstOrDefault(
                item => item.StepOrder == row.StepOrder && item.Pass == row.Pass && item.Kind == OverrideKinds.Feed);
            double? atReversal = row.LeftOverridePercent ?? row.RightOverridePercent;
            double? mean = feed?.MeanPercent ?? atReversal;
            double? min = feed?.MinPercent ?? atReversal;
            bool off = (feed is { OffNominalShare: > 0.0 }) || (atReversal is double r && Math.Abs(r - 100.0) > 0.5);
            bool bad = outOfBound.Contains((row.StepOrder, row.Pass)) || min is < RunRecorder.LowLimitPercent;
            bool show = Filter switch
            {
                PassFilter.CurrentStep => row.StepOrder == focus,
                PassFilter.OutOfBound => bad,
                _ => true,
            };
            if (!show)
            {
                continue;
            }

            Rows.Add(new PassRowViewModel(
                StepName(row.StepOrder),
                row.Pass.ToString(CultureInfo.CurrentCulture),
                Number(row.LeftZMm, "F2"),
                Number(row.RightZMm, "F2"),
                row.LeftDwellSeconds is null && row.RightDwellSeconds is null
                    ? "--"
                    : Number(row.LeftDwellSeconds, "0.0") + " / " + Number(row.RightDwellSeconds, "0.0"),
                feed is { } summary && Math.Abs(summary.MinPercent - summary.MaxPercent) > 0.5
                    ? this.localizer.Format("Pass_OverrideRangeFormat", Number(summary.MeanPercent, "0"), Number(summary.MinPercent, "0"), Number(summary.MaxPercent, "0"))
                    : Number(mean, "0") + " %",
                Number(row.CompensationMicrometer, "F1"),
                Number(row.MeanCurrentA, "F1"),
                row.StepOrder == this.currentStep && row.Pass == this.currentPass,
                off,
                bad));
        }

        // 正在磨、还没写进道次表的这一道：先有倍率，再有折返值（行在这一道走完时 NC 才写）。
        if (this.currentStep > 0 && Filter != PassFilter.OutOfBound
            && !this.passes.Any(row => row.StepOrder == this.currentStep && row.Pass == this.currentPass)
            && this.overrides.FirstOrDefault(item => item.StepOrder == this.currentStep && item.Pass == this.currentPass && item.Kind == OverrideKinds.Feed) is { } live)
        {
            Rows.Add(new PassRowViewModel(
                StepName(this.currentStep), this.currentPass.ToString(CultureInfo.CurrentCulture), "…", "…", "…",
                Number(live.MeanPercent, "0") + " %", "…", "…", true, live.OffNominalShare > 0.0, live.MinPercent < RunRecorder.LowLimitPercent));
        }

        Summaries.Clear();
        foreach (StepRunSummary summary in this.summaries)
        {
            Summaries.Add(new StepSummaryRowViewModel(
                StepName(summary.StepOrder),
                summary.PlanFeedMmPerMin is double plan && summary.JobFeedMmPerMin is double now && Math.Abs(plan - now) > 1e-9
                    ? this.localizer.Format("Pass_FeedChangedFormat", Number(plan, "0"), Number(now, "0"))
                    : Number(summary.JobFeedMmPerMin, "0"),
                summary.MeanFeedOverridePercent is null
                    ? "--"
                    : this.localizer.Format("Pass_MeanOverrideFormat", Number(summary.MeanFeedOverridePercent, "0"), Number(summary.MinFeedOverridePercent, "0")),

                // 有效值（设计说明第 6 节）："拖板设定 2300 × 平均倍率 93 % ≈ 2140 mm/min"。
                summary.EffectiveFeedMmPerMin is double effective
                    ? this.localizer.Format("Pass_EffectiveFormat", Number(summary.JobFeedMmPerMin, "0"), Number(summary.MeanFeedOverridePercent, "0"), Number(effective, "0"))
                    : "--",
                Number(summary.ReversalSpreadMm, "F2"),
                summary.FirstCompensationMicrometer is null
                    ? "--"
                    : Number(summary.FirstCompensationMicrometer, "F1") + " → " + Number(summary.LastCompensationMicrometer, "F1"),
                summary.OutOfBoundCount == 0 ? "0" : "✕ " + summary.OutOfBoundCount.ToString(CultureInfo.CurrentCulture)));
        }

        Events.Clear();
        foreach (RunEvent item in this.events
                     .Where(item => Filter != PassFilter.OutOfBound || RunEventKinds.IsOutOfBound(item.Kind))
                     .Reverse()
                     .Take(200))
        {
            string where = item.StepOrder is int step
                ? this.localizer.Format("Pass_EventWhereFormat", StepName(step), item.Pass ?? 0)
                : string.Empty;
            string value = item.Kind switch
            {
                RunEventKinds.OverrideLow or RunEventKinds.OverrideHigh or RunEventKinds.OverrideWholePass =>
                    this.localizer["Override_" + (item.Detail ?? OverrideKinds.Feed)] + " " + Number(item.Value, "0") + " %",
                RunEventKinds.PassMissing => this.localizer.Format("Pass_MissingFormat", item.Detail ?? "--"),
                RunEventKinds.NcAccepted or RunEventKinds.EditApplied => "#" + Number(item.Value, "0"),
                _ => string.Empty,
            };
            Events.Add(LabelValueViewModel.Raw(
                item.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                this.localizer["RunEvent_" + item.Kind] + "  " + where + "  " + value));
        }

        OnPropertyChanged(nameof(HasRows));
    }
}
