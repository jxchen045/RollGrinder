using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Services;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.NcInterface;
using RollGrinder.Sim;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 《上位机与 NC 接口约定》第 2–4 节的仿真版联调用例（第 8 节 1–7）：
/// 下发握手、改参数暂存区、道次环形缓冲。NC 一侧由仿真机照约定实现。
/// </summary>
public sealed class NcInterfaceContractTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    // ───────────────────────── 第 2 节：作业下发握手 ─────────────────────────

    [Fact]
    public async Task Download_clears_the_valid_flag_and_reads_it_back_before_writing_any_parameter()
    {
        await using ServiceProvider services = await BuildServicesAsync();
        var recorder = new RecordingGateway(services.GetRequiredService<IMachineGateway>());
        JobDownloadService download = ActivatorUtilities.CreateInstance<JobDownloadService>(services, recorder);

        JobDownloadResult result = await download.DownloadAsync(CreateJob(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        int cleared = recorder.Events.IndexOf($"write {MachineTagKeys.JobParametersValid}=False");
        int readBack = recorder.Events.IndexOf($"read {MachineTagKeys.JobParametersValid}");
        int firstParameter = recorder.Events.FindIndex(e => e.StartsWith("batch", StringComparison.Ordinal));
        cleared.Should().BeGreaterThanOrEqualTo(0, "① 先置 false");
        readBack.Should().BeGreaterThan(cleared, "② 读回确认");
        firstParameter.Should().BeGreaterThan(readBack, "③ 确认之后才写参数");
        recorder.Events.Take(cleared).Should().OnlyContain(e => e.StartsWith("read", StringComparison.Ordinal),
            "置 false 之前一条都不写");
        string[] batch = recorder.Events.SkipWhile(e => !e.StartsWith("batch", StringComparison.Ordinal)).ToArray();
        batch[^1].Should().Be($"batch {MachineTagKeys.JobParametersValid}=True", "⑤ 有效标志最后写");
        batch[^2].Should().StartWith($"batch {MachineTagKeys.JobDownloadSeq}=", "④ 序号在参数之后、有效之前");
    }

    [Fact]
    public async Task Each_download_gets_the_next_sequence_and_the_nc_latches_it_at_cycle_start()
    {
        await using ServiceProvider services = await BuildServicesAsync();
        IJobDownloadService download = services.GetRequiredService<IJobDownloadService>();
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();

        JobDownloadResult first = await download.DownloadAsync(CreateJob(), CancellationToken.None);
        JobDownloadResult second = await download.DownloadAsync(CreateJob() with { JobId = "J-2" }, CancellationToken.None);

        first.DownloadSequence.Should().Be(1);
        second.DownloadSequence.Should().Be(2);
        (await ReadIntAsync(gateway, MachineTagKeys.JobAcceptedSeq)).Should().Be(2, "NC 正在磨的就是上位机最后下发的那一份");

        string? snapshot = await services.GetRequiredService<IGrindingRecordRepository>()
            .GetSnapshotAsync(second.RecordId!, CancellationToken.None);
        JsonDocument.Parse(snapshot!).RootElement.GetProperty("downloadSequence").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_download_that_dies_halfway_leaves_the_parameters_invalid_and_the_nc_does_not_start()
    {
        // 联调用例 1：下发写到一半杀掉上位机。上一份留下的是 true——以前这里会被当成有效。
        await using ServiceProvider services = await BuildServicesAsync();
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(CreateJob(), CancellationToken.None);
        (await gateway.ReadTagAsync(MachineTagKeys.JobParametersValid, CancellationToken.None)).Raw.Should().Be(true);

        var dying = new RecordingGateway(gateway) { FailBatchAfter = 10 };
        JobDownloadService download = ActivatorUtilities.CreateInstance<JobDownloadService>(services, dying);
        Func<Task> act = () => download.DownloadAsync(CreateJob() with { JobId = "J-2" }, CancellationToken.None);

        await act.Should().ThrowAsync<GatewayException>();
        (await gateway.ReadTagAsync(MachineTagKeys.JobParametersValid, CancellationToken.None)).Raw.Should().Be(false);
        (await ReadIntAsync(gateway, MachineTagKeys.ChannelState)).Should().NotBe((int)NcChannelState.Running);
    }

    // ───────────────────────── 第 3 节：运行中改参数 ─────────────────────────

    [Fact]
    public async Task While_the_previous_edit_is_not_applied_a_new_one_is_refused()
    {
        // 联调用例 4：上一次修改未生效时再改，上位机拒收。
        await using ServiceProvider services = await BuildServicesAsync();
        GrindingJob job = CreateJob();
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(job, CancellationToken.None);
        IStepParameterUpdateService update = services.GetRequiredService<IStepParameterUpdateService>();

        GrindingJob first = Edit(job, 1, StepParameterKeys.FeedMmPerMin, 1800.0);
        StepUpdateResult accepted = await update.UpdateAsync(job, first, 1, "wang", CancellationToken.None);
        GrindingJob second = Edit(first, 1, StepParameterKeys.FeedMmPerMin, 1700.0);
        StepUpdateResult refused = await update.UpdateAsync(first, second, 1, "wang", CancellationToken.None);

        accepted.Succeeded.Should().BeTrue();
        accepted.EditSequence.Should().Be(1);
        refused.Succeeded.Should().BeFalse();
        refused.Refusal.Should().Be(StepUpdateRefusal.PreviousEditPending);
    }

    [Fact]
    public async Task A_new_download_voids_an_edit_the_nc_never_took()
    {
        // 上一支辊的修改还在暂存区里没被采用；不作废的话，NC 会在新作业的第一道把它拷进来。
        await using ServiceProvider services = await BuildServicesAsync();
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();
        IJobDownloadService download = services.GetRequiredService<IJobDownloadService>();
        GrindingJob job = CreateJob();
        await download.DownloadAsync(job, CancellationToken.None);
        await services.GetRequiredService<IStepParameterUpdateService>()
            .UpdateAsync(job, Edit(job, 1, StepParameterKeys.FeedMmPerMin, 1800.0), 1, "wang", CancellationToken.None);
        (await ReadIntAsync(gateway, MachineTagKeys.JobEditSeq))
            .Should().NotBe(await ReadIntAsync(gateway, MachineTagKeys.JobEditAppliedSeq), "前提：有一个待采用的修改");

        await download.DownloadAsync(CreateJob() with { JobId = "J-2" }, CancellationToken.None);

        (await ReadIntAsync(gateway, MachineTagKeys.JobEditSeq))
            .Should().Be(await ReadIntAsync(gateway, MachineTagKeys.JobEditAppliedSeq));
    }

    [Fact]
    public async Task The_staging_area_takes_one_step_at_a_time()
    {
        await using ServiceProvider services = await BuildServicesAsync();
        GrindingJob job = CreateJob();
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(job, CancellationToken.None);

        GrindingJob edited = Edit(Edit(job, 2, StepParameterKeys.FeedMmPerMin, 900.0), 3, StepParameterKeys.FeedMmPerMin, 900.0);
        StepUpdateResult result = await services.GetRequiredService<IStepParameterUpdateService>()
            .UpdateAsync(job, edited, 1, "wang", CancellationToken.None);

        result.Refusal.Should().Be(StepUpdateRefusal.OneStepAtATime);
    }

    [Fact]
    public async Task The_nc_takes_a_staged_edit_as_a_whole_at_the_start_of_the_next_pass()
    {
        // 联调用例 3：三个参数一起改，NC 在某一道次开始时整组采用，不出现部分采用。
        SimulatedMachine sim = await BuildSimAsync();
        HandOver(sim, passCount: 6);
        string feed = TagKeySyntax.Indexed(MachineTagKeys.JobStepFeedMmPerMin, 0);
        string passes = TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, 0);

        Write(sim, MachineTagKeys.JobEditOf(MachineTagKeys.JobStepFeedMmPerMin), 1500.0);
        Write(sim, MachineTagKeys.JobEditOf(MachineTagKeys.JobStepPassCount), 9.0);
        Write(sim, MachineTagKeys.JobEditStepOrder, 1.0);
        Write(sim, MachineTagKeys.JobEditSeq, 1.0);

        Convert.ToInt32(sim.Read(MachineTagKeys.JobEditAppliedSeq)).Should().Be(0, "还没到道次开始");
        Convert.ToDouble(sim.Read(feed)).Should().Be(2000.0);

        int guard = 0;
        while (Convert.ToInt32(sim.Read(MachineTagKeys.JobEditAppliedSeq)) == 0 && guard++ < 1000)
        {
            sim.Advance(TimeSpan.FromSeconds(0.25));
            bool feedChanged = Convert.ToDouble(sim.Read(feed)) != 2000.0;
            bool passesChanged = Convert.ToDouble(sim.Read(passes)) != 6.0;
            feedChanged.Should().Be(passesChanged, "要么整组都采用，要么都没采用");
        }

        Convert.ToInt32(sim.Read(MachineTagKeys.JobEditAppliedSeq)).Should().Be(1);
        Convert.ToDouble(sim.Read(feed)).Should().Be(1500.0);
        Convert.ToDouble(sim.Read(passes)).Should().Be(9.0);
    }

    // ───────────────────────── 第 4 节：道次环形缓冲 ─────────────────────────

    [Fact]
    public async Task The_nc_never_waits_for_the_hmi_and_counts_what_it_had_to_overwrite()
    {
        // 联调用例 6、7：上位机一直不读（断了 / 卡死），机床照常磨，溢出计数增加。
        SimulatedMachine sim = await BuildSimAsync();
        HandOver(sim, passCount: 200);

        for (int i = 0; i < 400 && Convert.ToInt32(sim.Read(MachineTagKeys.PassWriteSeq)) < 30; i++)
        {
            sim.Advance(TimeSpan.FromSeconds(1));
        }

        sim.ChannelState.Should().Be(NcChannelState.Running, "NC 不等上位机");
        Convert.ToInt32(sim.Read(MachineTagKeys.PassWriteSeq)).Should().BeGreaterThanOrEqualTo(30);
        Convert.ToInt32(sim.Read(MachineTagKeys.PassOverflowCount)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Rows_are_read_in_order_and_the_read_sequence_is_written_back()
    {
        (SimulationGateway gateway, ManualTimeProvider clock, MachineDescription machine) = await BuildSimGatewayAsync();
        await HandOverAsync(gateway, passCount: 200);
        var collector = new PassDataCollector(gateway, null, clock, PassDataCollector.DefaultWatchdog);

        for (int i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await collector.PollAsync(
                await gateway.ReadStateAsync(MachineTagKeys.MonitoringKeys(machine), CancellationToken.None),
                CancellationToken.None);
        }

        IReadOnlyList<PassRowRecord> rows = collector.Rows();
        rows.Should().NotBeEmpty();
        rows.Select(row => row.Sequence).Should().Equal(Enumerable.Range(1, rows.Count), "按序号连续读走");
        collector.MissingSequences().Should().BeEmpty();
        rows.Should().OnlyContain(row => row.StepOrder >= 1 && row.Pass >= 1);
        rows[^1].Left.ZMm.Should().NotBeNull();
        rows[^1].Right.ZMm.Should().NotBeNull();
        (await ReadIntAsync(gateway, MachineTagKeys.PassReadSeq)).Should().Be(rows[^1].Sequence);
        collector.Status.OverflowCount.Should().Be(0);
    }

    [Fact]
    public async Task An_hmi_away_longer_than_the_buffer_records_the_gap_and_the_machine_keeps_grinding()
    {
        // 联调用例 5、6：拔网线超过缓冲深度：机床不停、不报警；恢复后补读能读的，缺的标出来。
        (SimulationGateway gateway, ManualTimeProvider clock, MachineDescription machine) = await BuildSimGatewayAsync();
        await HandOverAsync(gateway, passCount: 200);
        var collector = new PassDataCollector(gateway, null, clock, PassDataCollector.DefaultWatchdog);

        for (int i = 0; i < 90; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await gateway.ReadTagAsync(MachineTagKeys.ChannelState, CancellationToken.None);
        }

        await collector.PollAsync(
            await gateway.ReadStateAsync(MachineTagKeys.MonitoringKeys(machine), CancellationToken.None),
            CancellationToken.None);

        (await ReadIntAsync(gateway, MachineTagKeys.ChannelState)).Should().Be((int)NcChannelState.Running);
        int written = collector.Status.WriteSeq;
        written.Should().BeGreaterThan(SimulatedMachine.PassSlots);
        collector.MissingSequences().Should().Equal(Enumerable.Range(1, written - SimulatedMachine.PassSlots));
        collector.Rows().Select(row => row.Sequence)
            .Should().Equal(Enumerable.Range(written - SimulatedMachine.PassSlots + 1, SimulatedMachine.PassSlots));
        collector.Status.OverflowCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_row_overwritten_while_it_is_being_read_is_not_taken()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
        var gateway = new TornRowGateway(clock);
        var collector = new PassDataCollector(gateway, null, clock, PassDataCollector.DefaultWatchdog);

        await collector.PollAsync(gateway.Snapshot(writeSeq: 1), CancellationToken.None);

        collector.Rows().Should().BeEmpty("前后两次读到的行序号不一致，这一行可能是半新半旧");
        collector.MissingSequences().Should().Equal(1);
    }

    [Fact]
    public async Task The_watchdog_only_speaks_while_machining_and_never_touches_the_machine()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
        var gateway = new TornRowGateway(clock);
        var collector = new PassDataCollector(gateway, null, clock, TimeSpan.FromSeconds(10));

        await collector.PollAsync(gateway.Snapshot(writeSeq: 0, running: true), CancellationToken.None);
        collector.IsStale(clock.GetUtcNow().AddSeconds(5)).Should().BeFalse();
        collector.IsStale(clock.GetUtcNow().AddSeconds(11)).Should().BeTrue();

        await collector.PollAsync(gateway.Snapshot(writeSeq: 0, running: false), CancellationToken.None);
        collector.IsStale(clock.GetUtcNow().AddSeconds(60)).Should().BeFalse("没在加工就没有道次数据可等");
        gateway.Writes.Should().BeEmpty("看门狗只提示");
    }

    // ───────────────────────── 搭建 ─────────────────────────

    private async Task<ServiceProvider> BuildServicesAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);

        var database = new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName));
        await database.MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);

        ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);
        return provider;
    }

    private async Task<SimulatedMachine> BuildSimAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);
        return new SimulatedMachine(await new JsonMachineConfigProvider(options).GetMachineAsync(CancellationToken.None));
    }

    private async Task<(SimulationGateway Gateway, ManualTimeProvider Clock, MachineDescription Machine)> BuildSimGatewayAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);
        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
        var gateway = new SimulationGateway(tagMap, machine, clock);
        await gateway.ConnectAsync(CancellationToken.None);
        return (gateway, clock, machine);
    }

    /// <summary>一道工序、辊身 100 mm、拖板 2000 mm/min：3 s 一个行程。</summary>
    private static void HandOver(SimulatedMachine sim, int passCount)
    {
        Write(sim, MachineTagKeys.JobRollRadiusMm, 300.0);
        Write(sim, MachineTagKeys.JobBodyLengthMm, 100.0);
        Write(sim, MachineTagKeys.JobFeedMmPerMin, 2000.0);
        Write(sim, MachineTagKeys.JobStepCount, 1.0);
        Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, 0), (double)passCount);
        Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepFeedMmPerMin, 0), 2000.0);
        Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepInfeedPerPassRadiusMm, 0), 0.01);
        Write(sim, MachineTagKeys.JobParametersValid, true);
    }

    private static async Task HandOverAsync(IMachineGateway gateway, int passCount)
    {
        async Task Put(string key, TagDataType type, object value) =>
            await gateway.WriteTagAsync(key, new TagValue(key, type, value, DateTimeOffset.UtcNow), CancellationToken.None);

        await Put(MachineTagKeys.JobRollRadiusMm, TagDataType.Double, 300.0);
        await Put(MachineTagKeys.JobBodyLengthMm, TagDataType.Double, 100.0);
        await Put(MachineTagKeys.JobFeedMmPerMin, TagDataType.Double, 2000.0);
        await Put(MachineTagKeys.JobStepCount, TagDataType.Int32, 1);
        await Put(TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, 0), TagDataType.Int32, passCount);
        await Put(TagKeySyntax.Indexed(MachineTagKeys.JobStepFeedMmPerMin, 0), TagDataType.Double, 2000.0);
        await Put(TagKeySyntax.Indexed(MachineTagKeys.JobStepInfeedPerPassRadiusMm, 0), TagDataType.Double, 0.01);
        await Put(MachineTagKeys.JobParametersValid, TagDataType.Boolean, true);
    }

    private static void Write(SimulatedMachine sim, string key, object value) =>
        sim.Write(key, new TagValue(key, value is bool ? TagDataType.Boolean : TagDataType.Double, value, DateTimeOffset.UtcNow));

    private static async Task<int> ReadIntAsync(IMachineGateway gateway, string key) =>
        Convert.ToInt32((await gateway.ReadTagAsync(key, CancellationToken.None)).Raw, System.Globalization.CultureInfo.InvariantCulture);

    private static GrindingJob CreateJob() => GrindingJob.Create(
        "J-1",
        "R-1",
        RollGeometry.FromDiameter(2000.0, 650.0),
        ProfileTypeKeys.Cylindrical,
        new CylindricalProfileType().Schema.CreateDefaults(),
        new[]
        {
            new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
            new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
            new GrindingJobStep(3, StepTypeKeys.SparkOut, new SparkOutStepType().Schema.CreateDefaults()),
        });

    private static GrindingJob Edit(GrindingJob job, int stepOrder, string key, double value) => job with
    {
        Steps = job.Steps
            .Select(step => step.Order == stepOrder
                ? step with { Parameters = step.Parameters.With(key, ParameterValue.FromNumber(value)) }
                : step)
            .ToArray(),
    };

    /// <summary>记下读写的顺序；可以让批量写在第 n 条后失败（模拟上位机写到一半崩溃）。</summary>
    private sealed class RecordingGateway : IMachineGateway
    {
        private readonly IMachineGateway inner;

        public RecordingGateway(IMachineGateway inner) => this.inner = inner;

        public List<string> Events { get; } = new();

        public int? FailBatchAfter { get; init; }

        public GatewayConnectionState ConnectionState => this.inner.ConnectionState;

        public Task ConnectAsync(CancellationToken cancellationToken) => this.inner.ConnectAsync(cancellationToken);

        public Task DisconnectAsync(CancellationToken cancellationToken) => this.inner.DisconnectAsync(cancellationToken);

        public Task<MachineStateSnapshot> ReadStateAsync(IReadOnlyList<string> logicalNames, CancellationToken cancellationToken) =>
            this.inner.ReadStateAsync(logicalNames, cancellationToken);

        public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken)
        {
            Events.Add($"read {logicalName}");
            return this.inner.ReadTagAsync(logicalName, cancellationToken);
        }

        public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken)
        {
            Events.Add($"write {logicalName}={value.Raw}");
            return this.inner.WriteTagAsync(logicalName, value, cancellationToken);
        }

        public async Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken)
        {
            for (int i = 0; i < writes.Count; i++)
            {
                if (FailBatchAfter is int limit && i >= limit)
                {
                    throw new GatewayException("connection lost in the middle of the download");
                }

                Events.Add($"batch {writes[i].LogicalName}={writes[i].Value.Raw}");
                await this.inner.WriteTagAsync(writes[i].LogicalName, writes[i].Value, cancellationToken);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>道次缓冲的假 NC：第一次读行序号是 1，再读时已被下一圈覆盖成 17。</summary>
    private sealed class TornRowGateway : IMachineGateway
    {
        private readonly TimeProvider clock;

        public TornRowGateway(TimeProvider clock) => this.clock = clock;

        public List<string> Writes { get; } = new();

        public GatewayConnectionState ConnectionState => GatewayConnectionState.Connected;

        public MachineStateSnapshot Snapshot(int writeSeq, bool running = true) => new(
            this.clock.GetUtcNow(),
            GatewayConnectionState.Connected,
            new[]
            {
                Value(MachineTagKeys.PassWriteSeq, writeSeq),
                Value(MachineTagKeys.PassSlotCount, 16),
                Value(MachineTagKeys.PassOverflowCount, 0),
                Value(MachineTagKeys.ChannelState, running ? (int)NcChannelState.Running : (int)NcChannelState.Reset),
            });

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<MachineStateSnapshot> ReadStateAsync(IReadOnlyList<string> logicalNames, CancellationToken cancellationToken) =>
            Task.FromResult(new MachineStateSnapshot(
                this.clock.GetUtcNow(),
                GatewayConnectionState.Connected,
                logicalNames.Select(key => Value(key, key == MachineTagKeys.PassRowField(1, 0) ? 1.0 : 5.0)).ToArray()));

        public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken) =>
            Task.FromResult(Value(logicalName, 17.0));

        public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken)
        {
            Writes.Add(logicalName);
            return Task.CompletedTask;
        }

        public Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken)
        {
            Writes.AddRange(writes.Select(write => write.LogicalName));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private TagValue Value(string key, object raw) =>
            new(key, raw is double ? TagDataType.Double : TagDataType.Int32, raw, this.clock.GetUtcNow());
    }
}
