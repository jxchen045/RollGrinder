using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 磨完自动收尾：按 NC 的"循环正常结束"位把挂着的那条记录收成已完成或已放弃，
/// 与操作员在记录页按"完成"走同一条路（定格砂轮直径、磨完的才出磨后报告）。
/// </summary>
public sealed class RecordCompletionServiceTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private const string RecordId = "REC-AUTO";
    private const string JobId = "J-AUTO";
    private const string RollId = "R-AUTO";

    private async Task<ServiceProvider> BuildAsync()
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
        return services.BuildServiceProvider();
    }

    /// <summary>铺一支下发过、还没收尾的作业，勾着"打印磨后数据"。</summary>
    private static async Task SeedOpenRecordAsync(ServiceProvider services)
    {
        var geometry = RollGeometry.FromDiameter(2000.0, 650.0);
        GrindingJob job = GrindingJob.Create(
            JobId,
            RollId,
            geometry,
            ProfileTypeKeys.Cylindrical,
            new CylindricalProfileType().Schema.CreateDefaults(),
            new[] { new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()) })
            with
            {
                ProgramOptions = new ParameterSet(new[]
                {
                    new KeyValuePair<string, ParameterValue>(ProgramOptionKeys.PrintPostGrindData, ParameterValue.FromBoolean(true)),
                }),
            };

        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-20);
        await services.GetRequiredService<IRollRepository>().UpsertAsync(
            new RollRecord(RollId, "WR-AUTO", geometry, null, started), CancellationToken.None);
        await services.GetRequiredService<IJobRepository>().SaveAsync(job, JobState.Handed, CancellationToken.None);
        await services.GetRequiredService<IGrindingRecordRepository>().AddAsync(
            new GrindingRecord(RecordId, JobId, started, null, JobState.Handed, null), CancellationToken.None);
    }

    private static RecordCompletionService Service(ServiceProvider services) =>
        services.GetServices<IHostedService>().OfType<RecordCompletionService>().Single();

    private static MachineStateSnapshot Snapshot(NcChannelState channel, int? cycleComplete)
    {
        var values = new List<TagValue>
        {
            new(MachineTagKeys.ChannelState, TagDataType.Int32, (int)channel, DateTimeOffset.UtcNow),
        };
        if (cycleComplete is int flag)
        {
            values.Add(new TagValue(MachineTagKeys.JobCycleComplete, TagDataType.Int32, flag, DateTimeOffset.UtcNow));
        }

        return new MachineStateSnapshot(DateTimeOffset.UtcNow, GatewayConnectionState.Connected, values);
    }

    private static Task<GrindingRecord?> RecordAsync(ServiceProvider services) =>
        services.GetRequiredService<IGrindingRecordRepository>().GetAsync(RecordId, CancellationToken.None);

    [Fact]
    public async Task A_normally_finished_cycle_closes_the_record_as_completed_and_prints_the_report()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedOpenRecordAsync(services);
        RecordCompletionService service = Service(services);

        service.Observe(Snapshot(NcChannelState.Running, 0)).Should().Be(CycleCompletionDecision.None);
        CycleCompletionDecision decision = service.Observe(Snapshot(NcChannelState.Reset, 1));
        decision.Should().Be(CycleCompletionDecision.Completed);
        await service.ApplyAsync(decision, CancellationToken.None);

        GrindingRecord? record = await RecordAsync(services);
        record!.State.Should().Be(JobState.Completed);
        record.FinishedAtUtc.Should().NotBeNull();
        record.WheelDiameterMm.Should().NotBeNull("收尾时定格当时的砂轮直径");

        services.GetRequiredService<IReportPrintQueue>().Drain()
            .Should().ContainSingle(r => r.Kind == ReportKind.PostGrind, "勾了打印磨后数据，磨完就出报告");
    }

    [Fact]
    public async Task A_reset_before_completion_closes_the_record_as_abandoned_without_a_report()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedOpenRecordAsync(services);
        RecordCompletionService service = Service(services);

        service.Observe(Snapshot(NcChannelState.Running, 0));
        CycleCompletionDecision decision = service.Observe(Snapshot(NcChannelState.Reset, 0));
        decision.Should().Be(CycleCompletionDecision.Abandoned);
        await service.ApplyAsync(decision, CancellationToken.None);

        (await RecordAsync(services))!.State.Should().Be(JobState.Abandoned);
        services.GetRequiredService<IReportPrintQueue>().Drain().Should().BeEmpty("半路停下的辊不出磨削报告");
    }

    [Fact]
    public async Task Without_the_flag_the_record_waits_for_the_operator()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedOpenRecordAsync(services);
        RecordCompletionService service = Service(services);

        service.Observe(Snapshot(NcChannelState.Running, null));
        CycleCompletionDecision decision = service.Observe(Snapshot(NcChannelState.Reset, null));
        decision.Should().Be(CycleCompletionDecision.NeedsOperator);
        await service.ApplyAsync(decision, CancellationToken.None);

        (await RecordAsync(services))!.FinishedAtUtc.Should().BeNull("上位机不猜，留给操作员");
        services.GetRequiredService<IAlarmLog>().Snapshot()
            .Should().Contain(a => a.MessageResourceKey == RecordCompletionService.NeedsOperatorResourceKey);
    }

    [Fact]
    public async Task A_record_already_closed_by_hand_is_left_alone()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedOpenRecordAsync(services);
        await services.GetRequiredService<IRecordService>()
            .FinishAsync(RecordId, JobState.Completed, "hand", CancellationToken.None);
        services.GetRequiredService<IReportPrintQueue>().Drain();
        RecordCompletionService service = Service(services);

        service.Observe(Snapshot(NcChannelState.Running, 0));
        await service.ApplyAsync(service.Observe(Snapshot(NcChannelState.Reset, 1)), CancellationToken.None);

        (await RecordAsync(services))!.Note.Should().Be("hand", "已经收过尾的不再动");
        services.GetRequiredService<IReportPrintQueue>().Drain().Should().BeEmpty("同一支辊不出两张报告");
    }
}
