using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using RollGrinder.App.SelfTest;
using RollGrinder.Contracts.Dtos;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 界面自检框架里不依赖 WPF 的部分：参数解析、两道安全闸、记录器的落盘格式与退出码。
/// 安全闸是重点——自检会按"启动""复位"、给 admin 设口令，绝不能对着真机床或现场数据跑。
/// </summary>
public sealed class SelfTestFrameworkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rg-selftest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public void Disabled_unless_asked()
    {
        SelfTestOptions.Parse(new[] { "--gateway", "sim" }, this.root).Enabled.Should().BeFalse();
    }

    [Fact]
    public void Parses_all_options()
    {
        SelfTestOptions options = SelfTestOptions.Parse(
            new[] { "--selftest", "--selftest-out", "out", "--selftest-scope", "render", "--selftest-label", "en-US" }, this.root);

        options.Enabled.Should().BeTrue();
        options.OutputDirectory.Should().Be(Path.Combine(this.root, "out"));
        options.Scope.Should().Be(SelfTestScope.Render);
        options.Label.Should().Be("en-US");
    }

    [Fact]
    public void Unknown_scope_is_rejected()
    {
        Action act = () => SelfTestOptions.Parse(new[] { "--selftest", "--selftest-scope", "everything" }, this.root);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(GatewayKind.OpcUa)]
    [InlineData(GatewayKind.File)]
    public void Refuses_real_or_replayed_machines(GatewayKind gateway)
    {
        SelfTestOptions.Refuse(gateway, Path.Combine(this.root, "fresh")).Should().NotBeNull("自检会按启动与复位");
    }

    [Theory]
    [InlineData(GatewayKind.Sim)]
    [InlineData(GatewayKind.Offline)]
    [InlineData(GatewayKind.Stub)]
    public void Allows_fake_machines_on_a_fresh_directory(GatewayKind gateway)
    {
        SelfTestOptions.Refuse(gateway, Path.Combine(this.root, "fresh")).Should().BeNull();
    }

    [Fact]
    public void Refuses_a_used_data_directory_without_the_marker()
    {
        string data = Path.Combine(this.root, "production");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "rollgrinder.db"), "x");

        SelfTestOptions.Refuse(GatewayKind.Sim, data).Should().NotBeNull("现场的库里不能被写进自检用户与记录");
    }

    [Fact]
    public void Accepts_a_used_directory_that_carries_the_marker()
    {
        string data = Path.Combine(this.root, "selftest-data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "rollgrinder.db"), "x");
        File.WriteAllText(Path.Combine(data, SelfTestOptions.DataMarkerFileName), "marker");

        SelfTestOptions.Refuse(GatewayKind.Sim, data).Should().BeNull();
    }

    [Fact]
    public void Recorder_writes_lines_log_and_summary()
    {
        string output = Path.Combine(this.root, "out");
        DateTimeOffset t0 = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);
        SelfTestSummary summary;
        using (var recorder = new SelfTestRecorder(output, "sim", new Dictionary<string, string> { ["gateway"] = "Sim" }, t0))
        {
            recorder.Note("suite Demo");
            recorder.Record(Step(recorder.NextSequence(), StepStatus.Pass));
            recorder.Record(Step(recorder.NextSequence(), StepStatus.Warn, alarms: new[] { "Warning:Alarm_X" }));
            recorder.Record(Step(recorder.NextSequence(), StepStatus.Fail, exception: "System.Exception: boom\n   at Somewhere()"));
            recorder.Record(Step(recorder.NextSequence(), StepStatus.Skip));
            summary = recorder.Complete(t0.AddSeconds(12));
        }

        summary.Total.Should().Be(4);
        summary.Failed.Should().Be(1);
        summary.FailedSteps.Should().ContainSingle().Which.Should().Contain("[0003]");
        SelfTestRecorder.ExitCodeFor(summary).Should().Be(1);

        string[] lines = File.ReadAllLines(Path.Combine(output, SelfTestRecorder.JsonLinesFileName));
        lines.Should().HaveCount(1 + 1 + 4 + 1, "environment + note + 4 steps + summary");
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            document.RootElement.GetProperty("kind").GetString().Should().NotBeNullOrEmpty();
        }

        string log = File.ReadAllText(Path.Combine(output, SelfTestRecorder.TextLogFileName));
        log.Should().Contain("[0003] FAIL").And.Contain("boom").And.Contain("# RESULT  total=4 pass=1 warn=1 fail=1 skip=1");
        File.Exists(Path.Combine(output, SelfTestRecorder.SummaryFileName)).Should().BeTrue();
    }

    [Fact]
    public void Exit_codes_distinguish_pass_fail_and_abort()
    {
        SelfTestRecorder.ExitCodeFor(Summary(failed: 0, aborted: false)).Should().Be(0);
        SelfTestRecorder.ExitCodeFor(Summary(failed: 2, aborted: false)).Should().Be(1);
        SelfTestRecorder.ExitCodeFor(Summary(failed: 0, aborted: true)).Should().Be(3);
    }

    private static StepResult Step(int sequence, StepStatus status, string[]? alarms = null, string? exception = null) =>
        new(sequence, "Demo", "Case", "Step" + sequence, status, 5, status.ToString(), alarms ?? Array.Empty<string>(), exception, null,
            new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero));

    private static SelfTestSummary Summary(int failed, bool aborted) =>
        new("x", DateTimeOffset.UnixEpoch, 1, 3, 3 - failed, 0, failed, 0, aborted, aborted ? "timeout" : null,
            Enumerable.Repeat("f", failed).ToList());
}
