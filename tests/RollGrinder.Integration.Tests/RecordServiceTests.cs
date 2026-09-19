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
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Measurement;
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

public sealed class RecordServiceTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    private async Task<ServiceProvider> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);

        await new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName))
            .MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);
        return services.BuildServiceProvider();
    }

    private static GrindingJob CreateJob(string jobId = "J-1") => GrindingJob.Create(
        jobId,
        "R-1",
        RollGeometry.FromDiameter(2000.0, 650.0),
        ProfileTypeKeys.Cylindrical,
        ParameterSet.Empty,
        new[] { new GrindingJobStep(1, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()) });

    private static async Task<string> HandOverAsync(ServiceProvider services, string jobId = "J-1")
    {
        await services.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);
        JobDownloadResult result = await services.GetRequiredService<IJobDownloadService>()
            .DownloadAsync(CreateJob(jobId), CancellationToken.None);
        result.Succeeded.Should().BeTrue();
        return result.RecordId!;
    }

    [Fact]
    public async Task A_handed_over_job_shows_up_in_the_record_query()
    {
        await using ServiceProvider services = await BuildAsync();
        string recordId = await HandOverAsync(services);

        IReadOnlyList<GrindingRecordView> views = await services.GetRequiredService<IRecordService>()
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), 50, CancellationToken.None);

        GrindingRecordView view = views.Should().ContainSingle().Subject;
        view.RecordId.Should().Be(recordId);
        view.RollId.Should().Be("R-1");
        view.ProfileTypeKey.Should().Be(ProfileTypeKeys.Cylindrical);
        view.State.Should().Be(JobState.Handed);
        view.Duration.Should().BeNull();
    }

    [Fact]
    public async Task Finishing_a_record_stores_its_state_and_duration()
    {
        await using ServiceProvider services = await BuildAsync();
        string recordId = await HandOverAsync(services);

        await services.GetRequiredService<IRecordService>()
            .FinishAsync(recordId, JobState.Completed, "手动收尾", CancellationToken.None);

        IReadOnlyList<GrindingRecordView> views = await services.GetRequiredService<IRecordService>()
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), 50, CancellationToken.None);

        views[0].State.Should().Be(JobState.Completed);
        views[0].FinishedAtUtc.Should().NotBeNull();
        views[0].Duration.Should().NotBeNull();
        views[0].Note.Should().Be("手动收尾");
    }

    [Fact]
    public async Task The_query_reports_the_worst_deviation_of_the_latest_measurement()
    {
        await using ServiceProvider services = await BuildAsync();
        await HandOverAsync(services);

        await services.GetRequiredService<IMeasurementService>().SaveAsync(
            "J-1",
            new[]
            {
                new MeasurementPoint(0.0, 325.003),
                new MeasurementPoint(1000.0, 325.000),
                new MeasurementPoint(2000.0, 324.998),
            },
            "DiameterGauge",
            CancellationToken.None);

        IReadOnlyList<GrindingRecordView> views = await services.GetRequiredService<IRecordService>()
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), 50, CancellationToken.None);

        views[0].WorstDeviationDiameterMicrometer.Should().BeApproximately(6.0, 1e-6);
    }

    [Fact]
    public async Task Records_export_to_csv_with_invariant_formatting()
    {
        await using ServiceProvider services = await BuildAsync();
        string recordId = await HandOverAsync(services);
        var recordService = services.GetRequiredService<IRecordService>();
        await recordService.FinishAsync(recordId, JobState.Completed, "含,逗号", CancellationToken.None);

        IReadOnlyList<GrindingRecordView> views = await recordService
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), 50, CancellationToken.None);
        string path = Path.Combine(this.workspace.Root, "export", "records.csv");

        await recordService.ExportCsvAsync(views, path, CancellationToken.None);

        string csv = await File.ReadAllTextAsync(path);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF }, "Excel 需要 BOM 才认 UTF-8");
        csv.Should().StartWith("recordId,jobId");
        csv.Should().Contain("\"含,逗号\"", "带逗号的字段必须转义");
        csv.Should().Contain(recordId);
    }

    [Fact]
    public async Task Expired_records_and_alarms_are_purged_by_the_retention_setting()
    {
        await using ServiceProvider services = await BuildAsync();
        await HandOverAsync(services);

        // 造一条远早于保留期的记录与报警。
        await services.GetRequiredService<IGrindingRecordRepository>().AddAsync(
            new GrindingRecord("G-old", "J-1", DateTimeOffset.UtcNow.AddYears(-50), null, JobState.Completed, null),
            CancellationToken.None);
        await services.GetRequiredService<IAlarmRepository>().AddAsync(
            DateTimeOffset.UtcNow.AddYears(-50), 2, "Alarm_GatewayFailure", null, AlarmCodes.GatewayFailure, CancellationToken.None);

        int purged = await services.GetRequiredService<IRecordService>().PurgeExpiredAsync(CancellationToken.None);

        purged.Should().Be(2);
        IReadOnlyList<GrindingRecordView> remaining = await services.GetRequiredService<IRecordService>()
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), 50, CancellationToken.None);
        remaining.Should().ContainSingle();
    }

    [Fact]
    public async Task The_query_window_is_honoured()
    {
        await using ServiceProvider services = await BuildAsync();
        await HandOverAsync(services);

        IReadOnlyList<GrindingRecordView> outsideWindow = await services.GetRequiredService<IRecordService>()
            .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), 50, CancellationToken.None);

        outsideWindow.Should().BeEmpty();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        this.workspace.Dispose();
    }
}
