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
using RollGrinder.Core;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Services;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Measurement;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 测量 → 偏差 → 补偿 → 下一次下发：闭环的服务层部分。
/// </summary>
public sealed class CompensationServiceTests : IDisposable
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

    private static GrindingJob CreateJob() => GrindingJob.Create(
        "J-1",
        "R-1",
        RollGeometry.FromDiameter(2000.0, 650.0),
        ProfileTypeKeys.Cylindrical,
        ParameterSet.Empty,
        new[] { new GrindingJobStep(1, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()) });

    private static IReadOnlyList<MeasurementPoint> MeasuredTooLarge(double radiusOffsetMm) =>
        Enumerable.Range(0, 11)
            .Select(i => new MeasurementPoint(2000.0 * i / 10.0, 325.0 + radiusOffsetMm))
            .ToArray();

    [Fact]
    public async Task Compensation_is_computed_from_the_latest_measurement_and_archived()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(CreateJob(), CancellationToken.None);

        await services.GetRequiredService<IMeasurementService>()
            .SaveAsync("J-1", MeasuredTooLarge(0.004), "DiameterGauge", CancellationToken.None);

        CompensationResult result = await services.GetRequiredService<ICompensationService>()
            .ComputeAndStoreAsync("J-1", CancellationToken.None);

        result.CompensationId.Should().NotBeNullOrEmpty();
        result.Quality.WorstDeviationDiameterMicrometer.Should().BeApproximately(8.0, 1e-6);
        result.Compensation.Points.Should().OnlyContain(point => point.RadiusOffsetMm < 0.0);

        (await services.GetRequiredService<ICompensationRepository>()
            .GetLatestByJobAsync("J-1", CancellationToken.None))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task The_next_handover_carries_the_compensation()
    {
        await using ServiceProvider services = await BuildAsync();
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();
        await gateway.ConnectAsync(CancellationToken.None);
        IJobDownloadService download = services.GetRequiredService<IJobDownloadService>();

        await download.DownloadAsync(CreateJob(), CancellationToken.None);
        await services.GetRequiredService<IMeasurementService>()
            .SaveAsync("J-1", MeasuredTooLarge(0.004), "DiameterGauge", CancellationToken.None);
        CompensationResult result = await services.GetRequiredService<ICompensationService>()
            .ComputeAndStoreAsync("J-1", CancellationToken.None);

        await download.DownloadAsync(CreateJob(), CancellationToken.None);

        TagValue handedOver = await gateway.ReadTagAsync(
            TagKeySyntax.Indexed(MachineTagKeys.JobProfileRadiusOffsetMm, 0), CancellationToken.None);

        Convert.ToDouble(handedOver.Raw, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeApproximately(result.Compensation.Points[0].RadiusOffsetMm, 1e-9);
    }

    [Fact]
    public async Task Compensating_without_a_measurement_is_refused()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(CreateJob(), CancellationToken.None);

        await services.GetRequiredService<ICompensationService>()
            .Invoking(s => s.ComputeAndStoreAsync("J-1", CancellationToken.None))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Compensating_an_unknown_job_is_refused()
    {
        await using ServiceProvider services = await BuildAsync();

        await services.GetRequiredService<ICompensationService>()
            .Invoking(s => s.ComputeAndStoreAsync("nope", CancellationToken.None))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task A_measurement_point_can_be_captured_from_the_machine()
    {
        await using ServiceProvider services = await BuildAsync();
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();
        await gateway.ConnectAsync(CancellationToken.None);
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(CreateJob(), CancellationToken.None);

        MeasurementPoint point = await services.GetRequiredService<IMeasurementService>()
            .CapturePointAsync(CancellationToken.None);

        // 仿真刚起步：半径应为目标半径加上待磨余量，且已换算成半径量。
        point.MeasuredRadiusMm.Should().BeGreaterThan(325.0);
        point.MeasuredRadiusMm.Should().BeLessThan(326.0);
        point.BodyPositionMm.Should().BeGreaterThanOrEqualTo(0.0);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        this.workspace.Dispose();
    }
}
