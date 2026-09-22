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
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services;
using RollGrinder.Services.Measurement;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 测量分磨前 / 磨中 / 磨后。
///
/// 磨前直径、锥度、实际凸度这些指标，靠的是"磨前量的那一次"与"磨后量的
/// 那一次"分别存着。光有一个 source 分不出来——同一个测头两次都叫 gauge。
/// </summary>
public sealed class MeasurementStageTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

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
        return services.BuildServiceProvider();
    }

    /// <summary>量一次 → 粗磨 → 量一次 → 精磨 → 量一次：三次测量各是一个阶段。</summary>
    private static IReadOnlyList<GrindingStepPlan> ThreeMeasurementsPlan()
    {
        var measure = new MeasureStepType();
        var rough = new RoughGrindingStepType();
        var finish = new FinishGrindingStepType();

        return new[]
        {
            measure.CreatePlan(Geometry, measure.Schema.CreateDefaults()),
            rough.CreatePlan(Geometry, rough.Schema.CreateDefaults()),
            measure.CreatePlan(Geometry, measure.Schema.CreateDefaults()),
            finish.CreatePlan(Geometry, finish.Schema.CreateDefaults()),
            measure.CreatePlan(Geometry, measure.Schema.CreateDefaults()),
        };
    }

    [Fact]
    public void The_stage_comes_from_where_the_measurement_sits_among_the_cutting_steps()
    {
        IReadOnlyList<GrindingStepPlan> plans = ThreeMeasurementsPlan();

        // 前面一道切削都没有 ⇒ 来料。
        MeasurementCaptureService.StageOf(plans, 1).Should().Be(MeasurementStage.PreGrind);

        // 夹在粗磨与精磨之间 ⇒ 中间测量，那是给行程间补偿用的。
        MeasurementCaptureService.StageOf(plans, 3).Should().Be(MeasurementStage.InProcess);

        // 后面一道切削都没有 ⇒ 成品。
        MeasurementCaptureService.StageOf(plans, 5).Should().Be(MeasurementStage.PostGrind);
    }

    [Fact]
    public void A_job_that_only_measures_at_the_end_has_no_pre_grind_measurement()
    {
        var rough = new RoughGrindingStepType();
        var measure = new MeasureStepType();
        GrindingStepPlan[] plans =
        {
            rough.CreatePlan(Geometry, rough.Schema.CreateDefaults()),
            measure.CreatePlan(Geometry, measure.Schema.CreateDefaults()),
        };

        MeasurementCaptureService.StageOf(plans, 2).Should().Be(MeasurementStage.PostGrind);
    }

    [Fact]
    public async Task Each_stage_is_stored_and_read_back_separately()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedJobAsync(services);

        IMeasurementRepository repository = services.GetRequiredService<IMeasurementRepository>();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        await repository.AddAsync(Measurement("m-pre", now, MeasurementStage.PreGrind, 325.5), CancellationToken.None);
        await repository.AddAsync(
            Measurement("m-post", now.AddHours(3.0), MeasurementStage.PostGrind, 325.0), CancellationToken.None);

        MeasurementRecord? pre = await repository
            .GetLatestByStageAsync("J-1", MeasurementStage.PreGrind, CancellationToken.None);
        MeasurementRecord? post = await repository
            .GetLatestByStageAsync("J-1", MeasurementStage.PostGrind, CancellationToken.None);

        pre!.Profile.Points[0].MeasuredRadiusMm.Should().Be(325.5);
        post!.Profile.Points[0].MeasuredRadiusMm.Should().Be(325.0);

        // 最近一次仍然是磨后那一次——补偿与报表看的都是它。
        (await repository.GetLatestByJobAsync("J-1", CancellationToken.None))!
            .Stage.Should().Be(MeasurementStage.PostGrind);
    }

    [Fact]
    public async Task A_stage_that_was_never_measured_reads_back_as_nothing()
    {
        // "没量过"与"量了是 0"是两回事。
        await using ServiceProvider services = await BuildAsync();
        await SeedJobAsync(services);

        IMeasurementRepository repository = services.GetRequiredService<IMeasurementRepository>();
        await repository.AddAsync(
            Measurement("m-post", DateTimeOffset.UnixEpoch, MeasurementStage.PostGrind, 325.0),
            CancellationToken.None);

        (await repository.GetLatestByStageAsync("J-1", MeasurementStage.PreGrind, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task Roundness_is_stored_beside_the_profile_not_inside_it()
    {
        // 辊形测量一个位置上是一个半径，圆度测量一个位置上是圆度与偏心两个数。
        await using ServiceProvider services = await BuildAsync();
        await SeedJobAsync(services);

        IRoundnessRepository repository = services.GetRequiredService<IRoundnessRepository>();
        await repository.AddAsync(
            new RoundnessMeasurement(
                "r-1", "J-1", DateTimeOffset.UnixEpoch, "trace",
                new[]
                {
                    new RoundnessPoint(0.0, 3.2, 18.0),
                    new RoundnessPoint(1000.0, 2.8, 17.4),
                }),
            CancellationToken.None);

        RoundnessMeasurement? loaded = await repository.GetLatestByJobAsync("J-1", CancellationToken.None);

        loaded!.Points.Should().HaveCount(2);
        loaded.Points[0].RoundnessMicrometer.Should().Be(3.2);
        loaded.Points[0].EccentricityMicrometer.Should().Be(18.0);
    }

    [Fact]
    public async Task Finishing_a_record_pins_down_how_big_the_wheel_was()
    {
        // 砂轮天天在磨小；事后回头查这支辊是用多大的砂轮磨的，只能靠当时记下来。
        await using ServiceProvider services = await BuildAsync();
        await SeedJobAsync(services);
        await services.GetRequiredService<RollGrinder.Services.Calibration.ICalibrationService>()
            .LoadAsync(CancellationToken.None);

        IGrindingRecordRepository records = services.GetRequiredService<IGrindingRecordRepository>();
        await records.AddAsync(
            new GrindingRecord("G-1", "J-1", DateTimeOffset.UnixEpoch, null, JobState.Handed, null),
            CancellationToken.None);

        await services.GetRequiredService<RollGrinder.Services.Records.IRecordService>()
            .FinishAsync("G-1", JobState.Completed, null, CancellationToken.None);

        GrindingRecord? finished = await records.GetAsync("G-1", CancellationToken.None);
        finished!.WheelDiameterMm.Should().NotBeNull();
        finished.WheelDiameterMm.Should().BePositive();
    }

    private static MeasurementRecord Measurement(
        string id, DateTimeOffset at, MeasurementStage stage, double radiusMm) =>
        new(id, "J-1", at, "gauge", new MeasuredProfile(new[]
        {
            new MeasurementPoint(0.0, radiusMm),
            new MeasurementPoint(1000.0, radiusMm),
        }))
        {
            Stage = stage,
        };

    private static async Task SeedJobAsync(ServiceProvider services)
    {
        await services.GetRequiredService<IRollRepository>().UpsertAsync(
            new RollRecord("R-1", "WR-1", Geometry, null, DateTimeOffset.UnixEpoch), CancellationToken.None);

        await services.GetRequiredService<IJobRepository>().SaveAsync(
            GrindingJob.Create(
                "J-1",
                "R-1",
                Geometry,
                ProfileTypeKeys.Cylindrical,
                new CylindricalProfileType().Schema.CreateDefaults(),
                new[]
                {
                    new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
                }),
            JobState.Handed,
            CancellationToken.None);
    }
}
