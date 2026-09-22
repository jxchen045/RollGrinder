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
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services;
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 磨削日报 / 月报与轧辊台账。
///
/// 日报与月报是同一件事，差别只在取哪一段时间——不必为"日"与"月"各写一份。
/// </summary>
public sealed class RecordSummaryTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private static readonly DateTimeOffset Day = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
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

    /// <summary>铺一支辊上的一次磨削。</summary>
    private static async Task GrindAsync(
        ServiceProvider services,
        string rollCode,
        string jobId,
        string recordId,
        DateTimeOffset startedAt,
        JobState state,
        double hours)
    {
        string rollId = "R-" + rollCode;

        await services.GetRequiredService<IRollRepository>().UpsertAsync(
            new RollRecord(rollId, rollCode, Geometry, "9Cr2Mo", startedAt), CancellationToken.None);
        await services.GetRequiredService<IJobRepository>().SaveAsync(
            GrindingJob.Create(
                jobId,
                rollId,
                Geometry,
                ProfileTypeKeys.Cylindrical,
                new CylindricalProfileType().Schema.CreateDefaults(),
                new[]
                {
                    new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
                }),
            state,
            CancellationToken.None);

        IGrindingRecordRepository records = services.GetRequiredService<IGrindingRecordRepository>();
        await records.AddAsync(
            new GrindingRecord(recordId, jobId, startedAt, null, JobState.Handed, null), CancellationToken.None);
        await records.FinishAsync(
            recordId, startedAt.AddHours(hours), state, null, null, CancellationToken.None);
    }

    [Fact]
    public async Task An_empty_day_has_no_pass_rate_rather_than_zero_percent()
    {
        // "这段时间没干活"与"干了活全不合格"是两回事。
        await using ServiceProvider services = await BuildAsync();

        GrindingSummary summary = await services.GetRequiredService<IRecordService>()
            .SummariseAsync(Day, Day.AddDays(1.0), CancellationToken.None);

        summary.TotalCount.Should().Be(0);
        summary.CompletionRate.Should().BeNull();
        summary.AverageDuration.Should().BeNull();
    }

    [Fact]
    public async Task The_summary_counts_rolls_completed_and_hours()
    {
        await using ServiceProvider services = await BuildAsync();

        await GrindAsync(services, "WR-1", "J-1", "G-1", Day, JobState.Completed, 2.0);
        await GrindAsync(services, "WR-2", "J-2", "G-2", Day.AddHours(3.0), JobState.Completed, 1.5);
        await GrindAsync(services, "WR-3", "J-3", "G-3", Day.AddHours(6.0), JobState.Abandoned, 0.5);

        GrindingSummary summary = await services.GetRequiredService<IRecordService>()
            .SummariseAsync(Day.AddHours(-1.0), Day.AddDays(1.0), CancellationToken.None);

        summary.TotalCount.Should().Be(3);
        summary.CompletedCount.Should().Be(2);
        summary.CompletionRate.Should().BeApproximately(2.0 / 3.0, 1e-9);
        summary.TotalDuration.Should().Be(TimeSpan.FromHours(4.0));
        summary.DistinctRollCount.Should().Be(3);
    }

    [Fact]
    public async Task The_same_roll_ground_twice_counts_as_one_roll_but_two_jobs()
    {
        // 返修的那一支不该把"这个月磨了几支辊"算多。
        await using ServiceProvider services = await BuildAsync();

        await GrindAsync(services, "WR-1", "J-1", "G-1", Day, JobState.Completed, 2.0);
        await GrindAsync(services, "WR-1", "J-2", "G-2", Day.AddHours(4.0), JobState.Completed, 1.0);

        GrindingSummary summary = await services.GetRequiredService<IRecordService>()
            .SummariseAsync(Day.AddHours(-1.0), Day.AddDays(1.0), CancellationToken.None);

        summary.TotalCount.Should().Be(2);
        summary.DistinctRollCount.Should().Be(1);
    }

    [Fact]
    public async Task The_ledger_says_how_often_each_roll_has_been_ground()
    {
        await using ServiceProvider services = await BuildAsync();

        await GrindAsync(services, "WR-1", "J-1", "G-1", Day, JobState.Completed, 2.0);
        await GrindAsync(services, "WR-1", "J-2", "G-2", Day.AddDays(30.0), JobState.Completed, 1.0);
        await GrindAsync(services, "WR-2", "J-3", "G-3", Day.AddDays(2.0), JobState.Completed, 1.0);

        IReadOnlyList<RollLedgerRow> ledger = await services.GetRequiredService<IRecordService>()
            .LoadLedgerAsync(100, CancellationToken.None);

        RollLedgerRow first = ledger.Single(row => row.Code == "WR-1");
        first.GrindCount.Should().Be(2);
        first.LastGroundAtUtc.Should().BeCloseTo(Day.AddDays(30.0).AddHours(1.0), TimeSpan.FromSeconds(1.0));
        first.Material.Should().Be("9Cr2Mo");

        ledger.Single(row => row.Code == "WR-2").GrindCount.Should().Be(1);
    }

    [Fact]
    public async Task A_roll_that_was_never_ground_shows_no_date_rather_than_a_made_up_one()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IRollRepository>().UpsertAsync(
            new RollRecord("R-new", "WR-NEW", Geometry, null, Day), CancellationToken.None);

        IReadOnlyList<RollLedgerRow> ledger = await services.GetRequiredService<IRecordService>()
            .LoadLedgerAsync(100, CancellationToken.None);

        RollLedgerRow row = ledger.Single(candidate => candidate.Code == "WR-NEW");
        row.GrindCount.Should().Be(0);
        row.LastGroundAtUtc.Should().BeNull();
    }
}
