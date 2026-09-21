using System;
using System.IO;
using System.Linq;
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
using RollGrinder.Data.Model;
using RollGrinder.Services;
using RollGrinder.Services.Jobs;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 磨削进行当中改工艺参数。
///
/// 这不是一条实时通道：改参数 = 把那一道工序的 R 参数重写一遍，NC 在下一道次读取。
/// 上位机写完就脱手，被强制结束时 NC 拿最后收到的值把这支辊磨完。
/// </summary>
public sealed class StepParameterUpdateTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private async Task<ServiceProvider> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(
            options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

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

    /// <summary>三道工序：粗磨、精磨、光磨。</summary>
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

    private static GrindingJob Edit(GrindingJob job, int stepOrder, string key, ParameterValue value)
    {
        GrindingJobStep[] steps = job.Steps
            .Select(step => step.Order == stepOrder
                ? step with { Parameters = step.Parameters.With(key, value) }
                : step)
            .ToArray();

        return job with { Steps = steps };
    }

    private static Task<StepUpdateResult> UpdateAsync(
        ServiceProvider services, GrindingJob original, GrindingJob edited, int currentStepOrder) =>
        services.GetRequiredService<IStepParameterUpdateService>()
            .UpdateAsync(original, edited, currentStepOrder, "wang", CancellationToken.None);

    [Fact]
    public async Task A_live_editable_parameter_on_the_running_step_goes_through()
    {
        // 电流大了就把拖板速度往下压一点——这是磨削当中最常做的一件事。
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(job, CancellationToken.None);

        GrindingJob edited = Edit(job, 1, StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(1800.0));
        StepUpdateResult result = await UpdateAsync(services, job, edited, currentStepOrder: 1);

        result.Succeeded.Should().BeTrue();
        result.Refusal.Should().Be(StepUpdateRefusal.None);
        result.ChangedStepOrders.Should().Equal(1);
        result.WriteCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_handover_flag_is_not_pulsed_again()
    {
        // 作业的身份没变；再脉冲一次握手标志会让 NC 以为来了一份新作业。
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(job, CancellationToken.None);

        var recorder = new RecordingGateway(services.GetRequiredService<IMachineGateway>());
        var service = new StepParameterUpdateService(
            recorder,
            services.GetRequiredService<GrindingJobValidator>(),
            services.GetRequiredService<RollGrinder.Nc.NcJobTranslator>(),
            services.GetRequiredService<GrindingStepTypeRegistry>(),
            services.GetRequiredService<MachineCapability>(),
            services.GetRequiredService<IJobRepository>(),
            services.GetRequiredService<RollGrinder.Services.Alarms.IAlarmSink>(),
            TimeProvider.System);

        GrindingJob edited = Edit(job, 1, StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(1800.0));
        await service.UpdateAsync(job, edited, 1, "wang", CancellationToken.None);

        recorder.WrittenNames.Should().NotContain(MachineTagKeys.JobParametersValid);
        recorder.WrittenNames.Should().Contain(TagKeySyntax.Indexed(MachineTagKeys.JobStepFeedMmPerMin, 0));
        recorder.WrittenNames.Should().NotContain(
            TagKeySyntax.Indexed(MachineTagKeys.JobStepFeedMmPerMin, 1), "没改的工序不该被重写");
    }

    [Fact]
    public async Task A_parameter_that_is_not_live_editable_is_refused_on_the_running_step()
    {
        // 砂轮线速度：132 kW 的主轴惯量大，磨削当中改就是带着切削长时间爬坡。
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();

        GrindingJob edited = Edit(
            job, 1, StepParameterKeys.WheelSurfaceSpeedMPerSec, ParameterValue.FromNumber(25.0));
        StepUpdateResult result = await UpdateAsync(services, job, edited, currentStepOrder: 1);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Be(StepUpdateRefusal.NotLiveEditable);
        result.BlockedParameterKeys.Should().Equal(StepParameterKeys.WheelSurfaceSpeedMPerSec);
    }

    [Fact]
    public async Task The_same_parameter_is_fine_on_a_step_that_has_not_started()
    {
        // 还没轮到的工序怎么改都行——那跟重新编程没有区别。
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(job, CancellationToken.None);

        GrindingJob edited = Edit(
            job, 2, StepParameterKeys.WheelSurfaceSpeedMPerSec, ParameterValue.FromNumber(25.0));
        StepUpdateResult result = await UpdateAsync(services, job, edited, currentStepOrder: 1);

        result.Succeeded.Should().BeTrue();
        result.ChangedStepOrders.Should().Equal(2);
    }

    [Fact]
    public async Task Changing_a_step_that_is_already_done_is_refused()
    {
        // 改了也没用，而且会让记录对不上实际磨的东西。
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();

        GrindingJob edited = Edit(job, 1, StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(1800.0));
        StepUpdateResult result = await UpdateAsync(services, job, edited, currentStepOrder: 2);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Be(StepUpdateRefusal.StepAlreadyDone);
    }

    [Fact]
    public async Task A_value_that_breaks_the_machine_limits_is_refused()
    {
        // 拖板速度打成 50000：机床最大 3000，改完整支作业必须还站得住。
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();

        GrindingJob edited = Edit(job, 1, StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(50000.0));
        StepUpdateResult result = await UpdateAsync(services, job, edited, currentStepOrder: 1);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Be(StepUpdateRefusal.Invalid);
        result.Violations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Changing_the_step_sequence_is_not_a_parameter_change()
    {
        // 加一道工序、换个辊形，那得停下来重新下发整支作业。
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();

        GrindingJob edited = job with { Steps = job.Steps.Take(2).ToArray() };
        StepUpdateResult result = await UpdateAsync(services, job, edited, currentStepOrder: 1);

        result.Refusal.Should().Be(StepUpdateRefusal.NotJustParameters);
    }

    [Fact]
    public async Task Nothing_changed_is_reported_rather_than_written()
    {
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();

        StepUpdateResult result = await UpdateAsync(services, job, job, currentStepOrder: 1);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Be(StepUpdateRefusal.NothingChanged);
        result.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task The_stored_job_follows_the_change_so_the_record_says_what_was_really_ground()
    {
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(job, CancellationToken.None);

        GrindingJob edited = Edit(job, 1, StepParameterKeys.PassCount, ParameterValue.FromNumber(6.0));
        await UpdateAsync(services, job, edited, currentStepOrder: 1);

        (GrindingJob Job, JobState State)? stored = await services.GetRequiredService<IJobRepository>()
            .GetAsync("J-1", CancellationToken.None);

        stored!.Value.Job.Steps[0].Parameters.GetNumber(StepParameterKeys.PassCount).Should().Be(6.0);
    }

    [Fact]
    public async Task Before_the_cycle_starts_every_parameter_is_open()
    {
        // currentStepOrder = 0：还没开始跑，没有"正在跑的那一道"，也就没有在线调整的限制。
        await using ServiceProvider services = await BuildAsync();
        GrindingJob job = CreateJob();
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(job, CancellationToken.None);

        GrindingJob edited = Edit(
            job, 1, StepParameterKeys.WheelSurfaceSpeedMPerSec, ParameterValue.FromNumber(25.0));
        StepUpdateResult result = await UpdateAsync(services, job, edited, currentStepOrder: 0);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void The_live_editable_set_is_the_one_we_reasoned_about()
    {
        // 这张表是想清楚过的（见 TraverseGrindingStepType.LiveEditableKeys 的注释）。
        // 往里加一项要有人重新想一遍，所以钉在这里。
        string[] live = new RoughGrindingStepType().Schema.Descriptors
            .Where(descriptor => descriptor.IsLiveEditable)
            .Select(descriptor => descriptor.Key)
            .ToArray();

        live.Should().BeEquivalentTo(new[]
        {
            StepParameterKeys.WorkpieceSpeedRpm,
            StepParameterKeys.FeedMmPerMin,
            StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin,
            StepParameterKeys.InfeedPerPassDiameterMicrometer,
            StepParameterKeys.PassCount,
            StepParameterKeys.StockDiameterMicrometer,
            StepParameterKeys.ReversalDwellSeconds,
            StepParameterKeys.SparkOutPassCount,
            StepParameterKeys.SpeedVariationTarget,
            StepParameterKeys.SpeedVariationPercent,
            StepParameterKeys.SpeedVariationPeriodSeconds,
        });

        live.Should().NotContain(StepParameterKeys.WheelSurfaceSpeedMPerSec, "砂轮主轴惯量大，不给磨削当中改");
        live.Should().NotContain(StepParameterKeys.InProcessMeasurement);
    }

    /// <summary>记下写了哪些逻辑名，用来确认握手标志没有被重新脉冲。</summary>
    private sealed class RecordingGateway : IMachineGateway
    {
        private readonly IMachineGateway inner;

        public RecordingGateway(IMachineGateway inner) => this.inner = inner;

        public System.Collections.Generic.List<string> WrittenNames { get; } = new();

        public GatewayConnectionState ConnectionState => this.inner.ConnectionState;

        public Task ConnectAsync(CancellationToken cancellationToken) => this.inner.ConnectAsync(cancellationToken);

        public Task DisconnectAsync(CancellationToken cancellationToken) => this.inner.DisconnectAsync(cancellationToken);

        public Task<MachineStateSnapshot> ReadStateAsync(
            System.Collections.Generic.IReadOnlyList<string> logicalNames, CancellationToken cancellationToken) =>
            this.inner.ReadStateAsync(logicalNames, cancellationToken);

        public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken) =>
            this.inner.ReadTagAsync(logicalName, cancellationToken);

        public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken)
        {
            WrittenNames.Add(logicalName);
            return this.inner.WriteTagAsync(logicalName, value, cancellationToken);
        }

        public Task WriteTagsAsync(
            System.Collections.Generic.IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken)
        {
            WrittenNames.AddRange(writes.Select(write => write.LogicalName));
            return this.inner.WriteTagsAsync(writes, cancellationToken);
        }

        public ValueTask DisposeAsync() => this.inner.DisposeAsync();
    }
}
