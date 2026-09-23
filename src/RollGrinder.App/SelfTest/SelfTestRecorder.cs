using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RollGrinder.App.SelfTest;

/// <summary>一步的结论。</summary>
public enum StepStatus
{
    /// <summary>通过。</summary>
    Pass = 0,

    /// <summary>通过，但有值得看的东西（提示级报警、预期内的降级……）。</summary>
    Warn = 1,

    /// <summary>失败：抛了异常、界面异常、未预期的错误级报警、断言不成立。</summary>
    Fail = 2,

    /// <summary>没跑（前置条件不满足）。</summary>
    Skip = 3,
}

/// <summary>一步的记录。</summary>
public sealed record StepResult(
    int Sequence,
    string Suite,
    string Case,
    string Step,
    StepStatus Status,
    long DurationMs,
    string Detail,
    IReadOnlyList<string> Alarms,
    string? Exception,
    string? Screenshot,
    DateTimeOffset StartedAtUtc);

/// <summary>整轮汇总。</summary>
public sealed record SelfTestSummary(
    string Label,
    DateTimeOffset StartedAtUtc,
    double TotalSeconds,
    int Total,
    int Passed,
    int Warned,
    int Failed,
    int Skipped,
    bool Aborted,
    string? AbortReason,
    IReadOnlyList<string> FailedSteps);

/// <summary>
/// 自检记录器。每一步同时写两份：
/// - selftest.jsonl：一行一个 JSON，给机器（分析时按字段筛）；
/// - selftest.log：对齐的纯文本，给人看。
/// 结束时写 summary.json。每写一步都立即落盘——进程中途崩了，前面的记录也还在。
///
/// 纯逻辑，不引用 WPF，便于单测。
/// </summary>
public sealed class SelfTestRecorder : IDisposable
{
    public const string JsonLinesFileName = "selftest.jsonl";
    public const string TextLogFileName = "selftest.log";
    public const string SummaryFileName = "summary.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string outputDirectory;
    private readonly string label;
    private readonly DateTimeOffset startedAtUtc;
    private readonly List<StepResult> results = new();
    private readonly StreamWriter jsonLines;
    private readonly StreamWriter textLog;
    private int sequence;

    public SelfTestRecorder(
        string outputDirectory, string label, IReadOnlyDictionary<string, string> environment, DateTimeOffset startedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(environment);
        this.outputDirectory = outputDirectory;
        this.label = label ?? string.Empty;
        this.startedAtUtc = startedAtUtc;

        Directory.CreateDirectory(outputDirectory);
        this.jsonLines = new StreamWriter(Path.Combine(outputDirectory, JsonLinesFileName), append: false, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        this.textLog = new StreamWriter(Path.Combine(outputDirectory, TextLogFileName), append: false, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        this.textLog.WriteLine(Invariant($"# Roll Grinder HMI self-test  label={this.label}  started={startedAtUtc:O}"));
        foreach (KeyValuePair<string, string> pair in environment.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            this.textLog.WriteLine(Invariant($"# {pair.Key} = {pair.Value}"));
        }

        this.textLog.WriteLine("#");
        this.jsonLines.WriteLine(JsonSerializer.Serialize(new { kind = "environment", label = this.label, startedAtUtc, environment }, JsonOptions));
    }

    /// <summary>目前记下的所有步骤。</summary>
    public IReadOnlyList<StepResult> Results => this.results;

    /// <summary>下一步的序号。</summary>
    public int NextSequence() => ++this.sequence;

    /// <summary>记一步。</summary>
    public void Record(StepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        this.results.Add(result);
        this.jsonLines.WriteLine(JsonSerializer.Serialize(new { kind = "step", result }, JsonOptions));
        this.textLog.WriteLine(FormatLine(result));
    }

    /// <summary>写一行备注（不算一步），例如"进入某个套件"。</summary>
    public void Note(string text)
    {
        this.textLog.WriteLine(Invariant($"## {text}"));
        this.jsonLines.WriteLine(JsonSerializer.Serialize(new { kind = "note", text }, JsonOptions));
    }

    /// <summary>收尾：写汇总。</summary>
    public SelfTestSummary Complete(DateTimeOffset finishedAtUtc, string? abortReason = null)
    {
        var summary = new SelfTestSummary(
            this.label,
            this.startedAtUtc,
            Math.Round((finishedAtUtc - this.startedAtUtc).TotalSeconds, 1),
            this.results.Count,
            this.results.Count(r => r.Status == StepStatus.Pass),
            this.results.Count(r => r.Status == StepStatus.Warn),
            this.results.Count(r => r.Status == StepStatus.Fail),
            this.results.Count(r => r.Status == StepStatus.Skip),
            abortReason is not null,
            abortReason,
            this.results.Where(r => r.Status == StepStatus.Fail)
                .Select(r => Invariant($"[{r.Sequence:D4}] {r.Suite} / {r.Case} / {r.Step}: {r.Detail}"))
                .ToList());

        File.WriteAllText(
            Path.Combine(this.outputDirectory, SummaryFileName),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }),
            new UTF8Encoding(false));

        this.textLog.WriteLine("#");
        this.textLog.WriteLine(Invariant(
            $"# RESULT  total={summary.Total} pass={summary.Passed} warn={summary.Warned} fail={summary.Failed} skip={summary.Skipped} seconds={summary.TotalSeconds}{(summary.Aborted ? "  ABORTED: " + summary.AbortReason : string.Empty)}"));
        foreach (string failed in summary.FailedSteps)
        {
            this.textLog.WriteLine("# FAIL " + failed);
        }

        this.jsonLines.WriteLine(JsonSerializer.Serialize(new { kind = "summary", summary }, JsonOptions));
        return summary;
    }

    /// <summary>进程退出码：0 全过（含 WARN），1 有失败，3 中途放弃。</summary>
    public static int ExitCodeFor(SelfTestSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Aborted)
        {
            return 3;
        }

        return summary.Failed > 0 ? 1 : 0;
    }

    public void Dispose()
    {
        this.jsonLines.Dispose();
        this.textLog.Dispose();
    }

    /// <summary>纯文本日志的一行。</summary>
    public static string FormatLine(StepResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var line = new StringBuilder();
        line.Append(Invariant($"[{r.Sequence:D4}] {r.Status.ToString().ToUpperInvariant(),-4}  {r.Suite} / {r.Case} / {r.Step}  ({r.DurationMs} ms)"));
        if (!string.IsNullOrEmpty(r.Detail))
        {
            line.Append("  | ").Append(r.Detail);
        }

        if (r.Alarms.Count > 0)
        {
            line.Append("  | alarms: ").Append(string.Join("; ", r.Alarms));
        }

        if (r.Screenshot is not null)
        {
            line.Append("  | shot: ").Append(r.Screenshot);
        }

        if (r.Exception is not null)
        {
            line.AppendLine();
            foreach (string exceptionLine in r.Exception.Split('\n'))
            {
                line.Append("        ").AppendLine(exceptionLine.TrimEnd('\r'));
            }
        }

        return line.ToString().TrimEnd();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
