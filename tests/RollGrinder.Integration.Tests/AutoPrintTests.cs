using System;
using System.Collections.Generic;
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
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// "打印磨前数据 / 打印磨后数据"这两个程序步骤。
///
/// 服务层知道什么时候该打，界面层知道怎么打，中间隔着一条队列。
/// 这里守的是"该进队列的进了、不该进的没进、打不出来也不耽误磨削"。
/// </summary>
public sealed class AutoPrintTests : IDisposable
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

    private static GrindingJob CreateJob(bool printPre, bool printPost)
    {
        GrindingJob job = GrindingJob.Create(
            "J-1",
            "R-1",
            RollGeometry.FromDiameter(2000.0, 650.0),
            ProfileTypeKeys.Cylindrical,
            new CylindricalProfileType().Schema.CreateDefaults(),
            new[]
            {
                new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
                new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
            });

        return job with
        {
            ProgramOptions = job.ProgramOptions
                .With(ProgramOptionKeys.PrintPreGrindData, ParameterValue.FromBoolean(printPre))
                .With(ProgramOptionKeys.PrintPostGrindData, ParameterValue.FromBoolean(printPost)),
        };
    }

    private static async Task<string> DownloadAsync(ServiceProvider services, GrindingJob job)
    {
        JobDownloadResult result = await services.GetRequiredService<IJobDownloadService>()
            .DownloadAsync(job, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        return result.RecordId!;
    }

    [Fact]
    public void Both_switches_are_off_by_default()
    {
        // 打印要有纸有墨、要有人去拿。默认关着，现场要打勾上就是。
        ParameterSet defaults = ProgramOptionCatalog.Defaults;

        defaults.GetBoolean(ProgramOptionKeys.PrintPreGrindData).Should().BeFalse();
        defaults.GetBoolean(ProgramOptionKeys.PrintPostGrindData).Should().BeFalse();
    }

    [Fact]
    public async Task Downloading_with_the_switch_on_queues_a_pre_grind_sheet()
    {
        await using ServiceProvider services = await BuildAsync();
        IReportPrintQueue queue = services.GetRequiredService<IReportPrintQueue>();

        await DownloadAsync(services, CreateJob(printPre: true, printPost: false));

        IReadOnlyList<GrindingReport> queued = queue.Drain();
        queued.Should().ContainSingle();
        queued[0].Kind.Should().Be(ReportKind.PreGrind);
    }

    [Fact]
    public async Task Downloading_with_the_switch_off_queues_nothing()
    {
        await using ServiceProvider services = await BuildAsync();
        IReportPrintQueue queue = services.GetRequiredService<IReportPrintQueue>();

        await DownloadAsync(services, CreateJob(printPre: false, printPost: false));

        queue.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task Finishing_a_record_with_the_switch_on_queues_a_grinding_report()
    {
        await using ServiceProvider services = await BuildAsync();
        IReportPrintQueue queue = services.GetRequiredService<IReportPrintQueue>();

        string recordId = await DownloadAsync(services, CreateJob(printPre: false, printPost: true));
        queue.Drain().Should().BeEmpty("磨前那张没勾");

        await services.GetRequiredService<IRecordService>()
            .FinishAsync(recordId, JobState.Completed, null, CancellationToken.None);

        IReadOnlyList<GrindingReport> queued = queue.Drain();
        queued.Should().ContainSingle();
        queued[0].Kind.Should().Be(ReportKind.PostGrind);
    }

    [Fact]
    public async Task Finishing_a_record_with_the_switch_off_queues_nothing()
    {
        await using ServiceProvider services = await BuildAsync();
        IReportPrintQueue queue = services.GetRequiredService<IReportPrintQueue>();

        string recordId = await DownloadAsync(services, CreateJob(printPre: false, printPost: false));
        await services.GetRequiredService<IRecordService>()
            .FinishAsync(recordId, JobState.Completed, null, CancellationToken.None);

        queue.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task Taking_the_queue_twice_does_not_print_the_same_sheet_twice()
    {
        // 取是一个原子动作：取过就不在队里了，同一张不会被打两遍。
        await using ServiceProvider services = await BuildAsync();
        IReportPrintQueue queue = services.GetRequiredService<IReportPrintQueue>();

        await DownloadAsync(services, CreateJob(printPre: true, printPost: false));

        queue.Drain().Should().ContainSingle();
        queue.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task A_sheet_queued_before_anyone_is_listening_is_not_lost()
    {
        // 启动早期还没人订：这一张也不能丢，不然操作工勾了却什么都没出来。
        await using ServiceProvider services = await BuildAsync();
        IReportPrintQueue queue = services.GetRequiredService<IReportPrintQueue>();

        await DownloadAsync(services, CreateJob(printPre: true, printPost: false));

        // 订上之后先取一次就拿到了。
        var seen = new List<GrindingReport>();
        queue.Enqueued += (_, _) => seen.AddRange(queue.Drain());
        seen.AddRange(queue.Drain());

        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Printing_is_never_a_reason_to_fail_the_handover()
    {
        // 参数已经在 NC 手里，这支辊照磨；没纸没墨不是停机的理由（最高原则）。
        await using ServiceProvider services = await BuildAsync();

        JobDownloadResult result = await services.GetRequiredService<IJobDownloadService>()
            .DownloadAsync(CreateJob(printPre: true, printPost: true), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.RecordId.Should().NotBeNullOrEmpty();
    }
}
