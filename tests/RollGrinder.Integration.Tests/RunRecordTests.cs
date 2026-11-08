using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.NcInterface;
using RollGrinder.Services.Records;
using RollGrinder.Sim;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 第 4 期验收（设计说明《轧辊与磨削方案》第 6 节、第 10 节）：道次表随记录保存；
/// 仿真里拧倍率后记录有按道次的摘要、越界逐条记，没有逐条的倍率变化；运行中改参数记下从第几道起生效。
/// </summary>
public sealed class RunRecordTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private sealed record Rig(
        SimulationGateway Gateway, ManualTimeProvider Clock, MachineDescription Machine, PassDataCollector Collector,
        RunRecorder Recorder, SqliteRunLogRepository RunLog, SqliteJobRepository Jobs, RunLogService Service);

    private async Task<Rig> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);
        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 8, 0, 0, TimeSpan.Zero));
        var gateway = new SimulationGateway(tagMap, machine, clock);
        await gateway.ConnectAsync(CancellationToken.None);

        var database = new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName));
        await database.MigrateAsync(CancellationToken.None);
        var runLog = new SqliteRunLogRepository(database);
        var jobs = new SqliteJobRepository(database);
        var records = new SqliteGrindingRecordRepository(database);
        var collector = new PassDataCollector(gateway, null, clock, TimeSpan.FromSeconds(10));
        var recorder = new RunRecorder(null, collector, runLog, jobs, records, clock);

        GrindingJob job = GrindingJob.Create(
            "J-1", "R-1", RollGeometry.FromDiameter(100.0, 600.0), ProfileTypeKeys.Cylindrical,
            new CylindricalProfileType().Schema.CreateDefaults(),
            new[] { new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()) }) with
        {
            Adjustments = new[]
            {
                new JobAdjustment(1, StepParameterKeys.FeedMmPerMin, 2000.0, 1900.0, true, "班长", clock.GetUtcNow()) { EditSeq = 1 },
            },
        };
        await new SqliteRollRepository(database).UpsertAsync(
            new RollRecord("R-1", "R-1", RollGeometry.FromDiameter(100.0, 600.0), "高铬铁", clock.GetUtcNow()), CancellationToken.None);
        await jobs.SaveAsync(job, Data.Model.JobState.Handed, CancellationToken.None);
        await records.AddAsync(new GrindingRecord("REC-1", "J-1", clock.GetUtcNow(), null, Data.Model.JobState.Handed, null), CancellationToken.None);

        // 一道工序、辊身 100 mm、拖板 2000 mm/min：3 s 一个行程（与接口约定用例同一套）。
        async Task Put(string key, TagDataType type, object value) =>
            await gateway.WriteTagAsync(key, new TagValue(key, type, value, clock.GetUtcNow()), CancellationToken.None);
        await Put(MachineTagKeys.JobRollRadiusMm, TagDataType.Double, 300.0);
        await Put(MachineTagKeys.JobBodyLengthMm, TagDataType.Double, 100.0);
        await Put(MachineTagKeys.JobFeedMmPerMin, TagDataType.Double, 2000.0);
        await Put(MachineTagKeys.JobStepCount, TagDataType.Int32, 1);
        await Put(TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, 0), TagDataType.Int32, 200);
        await Put(TagKeySyntax.Indexed(MachineTagKeys.JobStepFeedMmPerMin, 0), TagDataType.Double, 2000.0);
        await Put(TagKeySyntax.Indexed(MachineTagKeys.JobStepInfeedPerPassRadiusMm, 0), TagDataType.Double, 0.01);
        await Put(MachineTagKeys.JobDownloadSeq, TagDataType.Int32, 1);
        await Put(MachineTagKeys.JobParametersValid, TagDataType.Boolean, true);
        await recorder.BeginAsync("REC-1", "J-1", 1, CancellationToken.None);
        return new Rig(gateway, clock, machine, collector, recorder, runLog, jobs, new RunLogService(runLog, records, jobs));
    }

    private static async Task RunAsync(Rig rig, int seconds, double stepSeconds = 0.5)
    {
        for (double t = 0; t < seconds; t += stepSeconds)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(stepSeconds));
            MachineStateSnapshot snapshot = await rig.Gateway.ReadStateAsync(MachineTagKeys.MonitoringKeys(rig.Machine), CancellationToken.None);
            await rig.Collector.PollAsync(snapshot, CancellationToken.None);
            await rig.Recorder.PollAsync(snapshot, CancellationToken.None);
        }
    }

    private static Task SetFeedOverrideAsync(Rig rig, double percent) =>
        rig.Gateway.WriteTagAsync(
            MachineTagKeys.OverrideFeedPercent,
            new TagValue(MachineTagKeys.OverrideFeedPercent, TagDataType.Double, percent, rig.Clock.GetUtcNow()),
            CancellationToken.None);

    [Fact]
    public async Task Turning_the_override_leaves_a_per_pass_summary_and_out_of_bound_events_but_no_per_change_detail()
    {
        Rig rig = await BuildAsync();

        await RunAsync(rig, 12);
        await SetFeedOverrideAsync(rig, 80.0);
        await RunAsync(rig, 3);
        await SetFeedOverrideAsync(rig, 40.0);   // 低于 50 %：越界
        await RunAsync(rig, 3);
        await SetFeedOverrideAsync(rig, 45.0);   // 还在越界里：不另记
        await RunAsync(rig, 3);
        await SetFeedOverrideAsync(rig, 100.0);
        await RunAsync(rig, 12);
        await rig.Recorder.FlushAsync(CancellationToken.None);

        RunLogView view = await rig.Service.LoadAsync("REC-1", CancellationToken.None);
        view.Passes.Should().NotBeEmpty("道次表随记录保存");
        view.Passes.Select(row => row.Sequence).Should().BeInAscendingOrder();
        view.Nc!.DownloadSequence.Should().Be(1);
        view.Nc.AcceptedAtUtc.Should().NotBeNull("NC 循环启动时确认了这一份下发");

        OverrideSummaryRow[] feed = view.Overrides.Where(row => row.Kind == OverrideKinds.Feed).ToArray();
        feed.Should().Contain(row => row.MeanPercent < 99.0 && row.MinPercent <= 40.0 + 1e-9, "拧过倍率的那几道有摘要");
        feed.Should().Contain(row => Math.Abs(row.MeanPercent - 100.0) < 1e-6 && row.OffNominalShare == 0.0, "没拧的道次在 100 %");
        feed.Select(row => (row.StepOrder, row.Pass)).Should().OnlyHaveUniqueItems("每道次一行摘要，不记逐条变化");

        view.Events.Count(item => item.Kind == RunEventKinds.OverrideLow).Should().Be(1, "一次越界只记一条");
        view.Events.Should().NotContain(item => item.Kind == RunEventKinds.OverrideHigh);
        view.OutOfBoundCount.Should().BeGreaterThanOrEqualTo(1);

        StepRunSummary summary = view.Steps.Should().ContainSingle().Subject;
        summary.MeanFeedOverridePercent.Should().BeLessThan(100.0);
        summary.MinFeedOverridePercent.Should().BeApproximately(40.0, 1e-9);
        summary.EffectiveFeedMmPerMin.Should().BeApproximately(summary.JobFeedMmPerMin!.Value * summary.MeanFeedOverridePercent!.Value / 100.0, 1e-6);
        summary.PlanFeedMmPerMin.Should().Be(2000.0, "设定值取调整记录里的方案值");
        view.Passes.Should().Contain(row => row.LeftOverridePercent < 100.0 || row.RightOverridePercent < 100.0, "折返时的倍率也锁存了");
    }

    [Fact]
    public async Task A_running_edit_records_the_pass_it_took_effect_from()
    {
        Rig rig = await BuildAsync();
        await RunAsync(rig, 7);

        // 运行中改参数（序号 1）：暂存区写好，NC 下一道开始时整组采用。
        async Task Put(string key, TagDataType type, object value) =>
            await rig.Gateway.WriteTagAsync(key, new TagValue(key, type, value, rig.Clock.GetUtcNow()), CancellationToken.None);
        await Put(MachineTagKeys.JobEditStepOrder, TagDataType.Int32, 1);
        await Put(MachineTagKeys.JobEditSeq, TagDataType.Int32, 1);
        int passBefore = Convert.ToInt32((await rig.Gateway.ReadTagAsync(MachineTagKeys.JobCurrentPass, CancellationToken.None)).Raw, System.Globalization.CultureInfo.InvariantCulture);
        await RunAsync(rig, 8);

        JobAdjustment adjustment = (await rig.Jobs.GetAsync("J-1", CancellationToken.None))!.Value.Job.Adjustments.Single();
        adjustment.EffectiveStepOrder.Should().Be(1);
        adjustment.EffectivePass.Should().BeGreaterThan(passBefore, "下一道起才生效");
        adjustment.IsPending.Should().BeFalse();
        (await rig.RunLog.ListEventsAsync("REC-1", CancellationToken.None)).Should().Contain(item => item.Kind == RunEventKinds.EditApplied);
        (await rig.RunLog.ListPassesAsync("REC-1", CancellationToken.None))
            .Where(row => row.Pass >= adjustment.EffectivePass).Should().OnlyContain(row => row.EditAppliedSeq == 1, "道次行第 15 格记着已采用的序号");
    }

    [Fact]
    public void Missing_ranges_are_written_compactly()
    {
        RunRecorder.Ranges(new[] { 9, 3, 4, 5 }).Should().Be("3–5, 9");
        RunRecorder.Ranges(Array.Empty<int>()).Should().BeEmpty();
    }
}
