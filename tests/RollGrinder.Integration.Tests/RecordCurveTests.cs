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
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 记录页下半屏的四条曲线：磨前/磨后辊形、误差、圆度、补偿收敛过程。
///
/// 与结果指标一样是算出来的。没有数据时返回空曲线，界面照实说，
/// 不画一条编出来的线。
/// </summary>
public sealed class RecordCurveTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private const string JobId = "J-1";
    private const string RecordId = "G-1";

    private static readonly DateTimeOffset Started = DateTimeOffset.UnixEpoch;
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

        ServiceProvider provider = services.BuildServiceProvider();
        await SeedAsync(provider);
        return provider;
    }

    private static async Task SeedAsync(ServiceProvider services)
    {
        await services.GetRequiredService<IRollRepository>().UpsertAsync(
            new RollRecord("R-1", "WR-1", Geometry, null, Started), CancellationToken.None);
        await services.GetRequiredService<IJobRepository>().SaveAsync(
            GrindingJob.Create(
                JobId,
                "R-1",
                Geometry,
                ProfileTypeKeys.Cylindrical,
                new CylindricalProfileType().Schema.CreateDefaults(),
                new[]
                {
                    new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
                }),
            JobState.Completed,
            CancellationToken.None);
        await services.GetRequiredService<IGrindingRecordRepository>().AddAsync(
            new GrindingRecord(RecordId, JobId, Started, null, JobState.Handed, null), CancellationToken.None);
    }

    private static MeasurementRecord Measurement(string id, MeasurementStage stage, double radiusMm) =>
        new(id, JobId, Started, "gauge", new MeasuredProfile(Enumerable.Range(0, 11)
            .Select(i => new MeasurementPoint(i * 200.0, radiusMm))
            .ToArray()))
        {
            Stage = stage,
        };

    private static Task<RecordCurve> CurveAsync(ServiceProvider services, RecordCurveKind kind) =>
        services.GetRequiredService<IRecordService>().LoadCurveAsync(RecordId, kind, CancellationToken.None);

    [Fact]
    public async Task A_roll_with_no_data_says_so_instead_of_drawing_a_line()
    {
        await using ServiceProvider services = await BuildAsync();

        foreach (RecordCurveKind kind in Enum.GetValues<RecordCurveKind>())
        {
            RecordCurve curve = await CurveAsync(services, kind);
            curve.HasData.Should().BeFalse(kind.ToString());
        }
    }

    [Fact]
    public async Task The_before_and_after_profiles_are_two_lines_on_one_chart()
    {
        await using ServiceProvider services = await BuildAsync();
        IMeasurementRepository measurements = services.GetRequiredService<IMeasurementRepository>();

        await measurements.AddAsync(
            Measurement("m-pre", MeasurementStage.PreGrind, 325.5), CancellationToken.None);
        await measurements.AddAsync(
            Measurement("m-post", MeasurementStage.PostGrind, 325.0), CancellationToken.None);

        RecordCurve curve = await CurveAsync(services, RecordCurveKind.BeforeAfterProfile);

        curve.HasData.Should().BeTrue();
        curve.Series.Should().HaveCount(2);
        curve.Series.Select(series => series.LabelResourceKey).Should()
            .Equal("Curve_BeforeGrinding", "Curve_AfterGrinding");

        // 纵坐标是相对公称半径的偏差（直径量 µm），不是绝对直径——
        // 两条相差不到一毫米的线画在 800 mm 的量程上，肉眼看就是重合的。
        curve.Series[0].Points[0].Y.Should().BeApproximately(1000.0, 1e-6, "半径大 0.5 mm ⇒ 直径量 1000 µm");
        curve.Series[1].Points[0].Y.Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public async Task Only_a_post_grind_measurement_still_draws_one_line()
    {
        // 磨前没量不该让磨后那条也画不出来。
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IMeasurementRepository>().AddAsync(
            Measurement("m-post", MeasurementStage.PostGrind, 325.0), CancellationToken.None);

        RecordCurve curve = await CurveAsync(services, RecordCurveKind.BeforeAfterProfile);

        curve.Series.Should().ContainSingle();
        curve.Series[0].LabelResourceKey.Should().Be("Curve_AfterGrinding");
    }

    [Fact]
    public async Task The_deviation_curve_is_measured_against_the_target()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IMeasurementRepository>().AddAsync(
            Measurement("m-post", MeasurementStage.PostGrind, 325.005), CancellationToken.None);

        RecordCurve curve = await CurveAsync(services, RecordCurveKind.Deviation);

        curve.HasData.Should().BeTrue();
        curve.Series.Should().ContainSingle();

        // 实测比目标半径大 5 µm ⇒ 直径量 10 µm。
        curve.Series[0].Points.Should().OnlyContain(point => Math.Abs(point.Y - 10.0) < 1e-6);
    }

    [Fact]
    public async Task The_roundness_chart_carries_both_roundness_and_eccentricity()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IRoundnessRepository>().AddAsync(
            new RoundnessMeasurement("r-1", JobId, Started, "trace", new[]
            {
                new RoundnessPoint(0.0, 3.2, 18.0),
                new RoundnessPoint(1000.0, 2.8, 17.4),
                new RoundnessPoint(2000.0, 3.0, 16.9),
            }),
            CancellationToken.None);

        RecordCurve curve = await CurveAsync(services, RecordCurveKind.Roundness);

        curve.Series.Should().HaveCount(2);
        curve.Series[0].Points[0].Y.Should().Be(3.2);
        curve.Series[1].Points[0].Y.Should().Be(18.0);
    }

    [Fact]
    public async Task The_convergence_curve_runs_earliest_first()
    {
        // 横坐标是"第几次迭代"，倒着取就把曲线画反了——
        // 一条本来在收敛的曲线会看起来越补越大。
        await using ServiceProvider services = await BuildAsync();
        ICompensationRepository compensations = services.GetRequiredService<ICompensationRepository>();

        await compensations.AddAsync(
            new CompensationRecord("c-1", JobId, Started, null, new[] { new ProfilePoint(0.0, 0.008) }),
            CancellationToken.None);
        await compensations.AddAsync(
            new CompensationRecord("c-2", JobId, Started.AddMinutes(10.0), null, new[] { new ProfilePoint(0.0, 0.003) }),
            CancellationToken.None);
        await compensations.AddAsync(
            new CompensationRecord("c-3", JobId, Started.AddMinutes(20.0), null, new[] { new ProfilePoint(0.0, 0.001) }),
            CancellationToken.None);

        RecordCurve curve = await CurveAsync(services, RecordCurveKind.CompensationConvergence);

        curve.HasData.Should().BeTrue();
        curve.Series[0].Points.Select(point => point.X).Should().Equal(1.0, 2.0, 3.0);
        curve.Series[0].Points.Select(point => point.Y).Should().BeInDescendingOrder("这一支在收敛");
        curve.Series[0].Points[0].Y.Should().BeApproximately(16.0, 1e-6, "半径 0.008 mm ⇒ 直径量 16 µm");
    }

    [Fact]
    public async Task A_record_that_does_not_exist_gives_an_empty_curve_not_an_error()
    {
        await using ServiceProvider services = await BuildAsync();

        RecordCurve curve = await services.GetRequiredService<IRecordService>()
            .LoadCurveAsync("no-such-record", RecordCurveKind.Deviation, CancellationToken.None);

        curve.HasData.Should().BeFalse();
    }
}
