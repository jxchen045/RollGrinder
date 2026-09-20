using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using Xunit;

namespace RollGrinder.Integration.Tests;

public sealed class SqliteStoreTests : IDisposable
{
    private readonly TempWorkspace workspace = new();
    private readonly SqliteDatabase database;

    public SqliteStoreTests()
    {
        this.database = new SqliteDatabase(Path.Combine(this.workspace.Root, "data", SqliteDatabase.FileName));
    }

    private async Task<SqliteDatabase> MigratedAsync()
    {
        await this.database.MigrateAsync(CancellationToken.None);
        return this.database;
    }

    private static GrindingJob CreateJob(string jobId = "J-1", ParameterSet? programOptions = null)
    {
        var rough = new RoughGrindingStepType();
        var sparkOut = new SparkOutStepType();
        var crown = new CrownProfileType();

        return GrindingJob.Create(
            jobId,
            "R-1",
            RollGeometry.FromDiameter(2000.0, 650.0),
            ProfileTypeKeys.Crown,
            crown.Schema.CreateDefaults()
                .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(120.0)),
            new[]
            {
                new GrindingJobStep(1, StepTypeKeys.Rough, rough.Schema.CreateDefaults()),
                new GrindingJobStep(2, StepTypeKeys.SparkOut, sparkOut.Schema.CreateDefaults()),
            },
            programOptions);
    }

    [Fact]
    public async Task Program_step_switches_survive_a_save_and_load()
    {
        await MigratedAsync();
        await new SqliteRollRepository(this.database).UpsertAsync(
            new RollRecord("R-1", "WR", RollGeometry.FromDiameter(2000.0, 650.0), null, DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        var jobs = new SqliteJobRepository(this.database);

        // 八个开关里挑两个反着设，存进去再读出来必须一模一样。
        ParameterSet options = ProgramOptionCatalog.Defaults
            .With(ProgramOptionKeys.PreGrindMeasure, ParameterValue.FromBoolean(false))
            .With(ProgramOptionKeys.EddyCurrentTest, ParameterValue.FromBoolean(true));

        GrindingJob job = CreateJob("J-opt", options);
        await jobs.SaveAsync(job, JobState.Draft, CancellationToken.None);

        (GrindingJob Job, JobState State)? stored = await jobs.GetAsync("J-opt", CancellationToken.None);

        stored.Should().NotBeNull();
        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All)
        {
            stored!.Value.Job.IsProgramOptionEnabled(option.Key)
                .Should().Be(job.IsProgramOptionEnabled(option.Key), $"开关 {option.Key} 没存住");
        }

        stored!.Value.Job.IsProgramOptionEnabled(ProgramOptionKeys.PreGrindMeasure).Should().BeFalse();
        stored.Value.Job.IsProgramOptionEnabled(ProgramOptionKeys.EddyCurrentTest).Should().BeTrue();
    }

    [Fact]
    public async Task Migration_brings_a_fresh_file_to_the_expected_version()
    {
        await MigratedAsync();

        (await this.database.ReadSchemaVersionAsync(CancellationToken.None))
            .Should().Be(SqliteDatabase.ExpectedSchemaVersion);
    }

    [Fact]
    public async Task Migration_is_idempotent_and_keeps_existing_data()
    {
        await MigratedAsync();
        var rolls = new SqliteRollRepository(this.database);
        await rolls.UpsertAsync(
            new RollRecord("R-1", "WR-4711", RollGeometry.FromDiameter(2000.0, 650.0), "9Cr2Mo", DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        await this.database.MigrateAsync(CancellationToken.None);

        (await rolls.GetAsync("R-1", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Roll_round_trips()
    {
        await MigratedAsync();
        var rolls = new SqliteRollRepository(this.database);
        var roll = new RollRecord("R-2", "BUR-9", RollGeometry.FromDiameter(3000.0, 1200.0), "Forged", DateTimeOffset.UnixEpoch);

        await rolls.UpsertAsync(roll, CancellationToken.None);
        RollRecord? loaded = await rolls.GetAsync("R-2", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Code.Should().Be("BUR-9");
        loaded.Geometry.NominalRadiusMm.Should().Be(600.0);
        loaded.Material.Should().Be("Forged");
    }

    [Fact]
    public async Task Upsert_updates_rather_than_duplicates()
    {
        await MigratedAsync();
        var rolls = new SqliteRollRepository(this.database);
        var roll = new RollRecord("R-3", "old", RollGeometry.FromDiameter(2000.0, 650.0), null, DateTimeOffset.UnixEpoch);

        await rolls.UpsertAsync(roll, CancellationToken.None);
        await rolls.UpsertAsync(roll with { Code = "new" }, CancellationToken.None);

        (await rolls.ListAsync(10, CancellationToken.None)).Should().ContainSingle()
            .Which.Code.Should().Be("new");
    }

    [Fact]
    public async Task Job_round_trips_with_its_parameters_and_steps()
    {
        await MigratedAsync();
        await new SqliteRollRepository(this.database).UpsertAsync(
            new RollRecord("R-1", "WR", RollGeometry.FromDiameter(2000.0, 650.0), null, DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        var jobs = new SqliteJobRepository(this.database);
        GrindingJob job = CreateJob();

        await jobs.SaveAsync(job, JobState.Draft, CancellationToken.None);
        (GrindingJob Job, JobState State)? loaded = await jobs.GetAsync("J-1", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Value.State.Should().Be(JobState.Draft);
        loaded.Value.Job.ProfileTypeKey.Should().Be(ProfileTypeKeys.Crown);
        loaded.Value.Job.ProfileParameters.GetNumber(CrownProfileType.CrownDiameterMicrometerKey).Should().Be(120.0);
        loaded.Value.Job.Steps.Select(step => step.StepTypeKey)
            .Should().Equal(StepTypeKeys.Rough, StepTypeKeys.SparkOut);
        loaded.Value.Job.Steps[0].Parameters.GetNumber(StepParameterKeys.FeedMmPerMin)
            .Should().Be(job.Steps[0].Parameters.GetNumber(StepParameterKeys.FeedMmPerMin));
    }

    [Fact]
    public async Task Saving_a_job_twice_replaces_its_steps_instead_of_appending()
    {
        await MigratedAsync();
        await new SqliteRollRepository(this.database).UpsertAsync(
            new RollRecord("R-1", "WR", RollGeometry.FromDiameter(2000.0, 650.0), null, DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        var jobs = new SqliteJobRepository(this.database);
        GrindingJob job = CreateJob();
        await jobs.SaveAsync(job, JobState.Draft, CancellationToken.None);

        GrindingJob shortened = GrindingJob.Create(
            job.JobId, job.RollId, job.Geometry, job.ProfileTypeKey, job.ProfileParameters, new[] { job.Steps[0] });
        await jobs.SaveAsync(shortened, JobState.Handed, CancellationToken.None);

        (GrindingJob Job, JobState State)? loaded = await jobs.GetAsync(job.JobId, CancellationToken.None);
        loaded!.Value.Job.Steps.Should().HaveCount(1);
        loaded.Value.State.Should().Be(JobState.Handed);
    }

    [Fact]
    public async Task Setting_the_state_of_an_unknown_job_is_an_error()
    {
        await MigratedAsync();
        var jobs = new SqliteJobRepository(this.database);

        await jobs.Invoking(r => r.SetStateAsync("nope", JobState.Completed, CancellationToken.None))
            .Should().ThrowAsync<DataStoreException>();
    }

    [Fact]
    public async Task Grinding_records_can_be_started_finished_and_queried()
    {
        await MigratedAsync();
        await SeedJobAsync();
        var records = new SqliteGrindingRecordRepository(this.database);
        DateTimeOffset start = DateTimeOffset.UnixEpoch.AddHours(1);

        await records.AddAsync(new GrindingRecord("G-1", "J-1", start, null, JobState.Handed, null), CancellationToken.None);
        await records.FinishAsync("G-1", start.AddHours(2), JobState.Completed, "ok", CancellationToken.None);

        GrindingRecord? loaded = await records.GetAsync("G-1", CancellationToken.None);
        loaded!.FinishedAtUtc.Should().Be(start.AddHours(2));
        loaded.State.Should().Be(JobState.Completed);
        loaded.Note.Should().Be("ok");

        IReadOnlyList<GrindingRecord> queried = await records.QueryAsync(
            start.AddMinutes(-1), start.AddMinutes(1), 10, CancellationToken.None);
        queried.Should().ContainSingle();
    }

    [Fact]
    public async Task Old_records_can_be_purged()
    {
        await MigratedAsync();
        await SeedJobAsync();
        var records = new SqliteGrindingRecordRepository(this.database);
        await records.AddAsync(
            new GrindingRecord("G-old", "J-1", DateTimeOffset.UnixEpoch, null, JobState.Completed, null),
            CancellationToken.None);
        await records.AddAsync(
            new GrindingRecord("G-new", "J-1", DateTimeOffset.UnixEpoch.AddYears(10), null, JobState.Completed, null),
            CancellationToken.None);

        int purged = await records.PurgeOlderThanAsync(DateTimeOffset.UnixEpoch.AddYears(1), CancellationToken.None);

        purged.Should().Be(1);
        (await records.GetAsync("G-new", CancellationToken.None)).Should().NotBeNull();
        (await records.GetAsync("G-old", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Measurements_round_trip_with_their_points()
    {
        await MigratedAsync();
        await SeedJobAsync();
        var measurements = new SqliteMeasurementRepository(this.database);
        var profile = new MeasuredProfile(new[]
        {
            new MeasurementPoint(0.0, 325.010),
            new MeasurementPoint(1000.0, 325.055),
            new MeasurementPoint(2000.0, 325.012),
        });

        await measurements.AddAsync(
            new MeasurementRecord("M-1", "J-1", DateTimeOffset.UnixEpoch, "DiameterGauge", profile),
            CancellationToken.None);

        MeasurementRecord? loaded = await measurements.GetLatestByJobAsync("J-1", CancellationToken.None);
        loaded!.Source.Should().Be("DiameterGauge");
        loaded.Profile.Points.Should().HaveCount(3);
        loaded.Profile.MeasuredRadiusAtMm(1000.0).Should().BeApproximately(325.055, 1e-9);
    }

    [Fact]
    public async Task Compensations_round_trip_with_their_points()
    {
        await MigratedAsync();
        await SeedJobAsync();
        await new SqliteMeasurementRepository(this.database).AddAsync(
            new MeasurementRecord(
                "M-1",
                "J-1",
                DateTimeOffset.UnixEpoch,
                "DiameterGauge",
                new MeasuredProfile(new[]
                {
                    new MeasurementPoint(0.0, 325.0),
                    new MeasurementPoint(2000.0, 325.0),
                })),
            CancellationToken.None);
        var compensations = new SqliteCompensationRepository(this.database);

        await compensations.AddAsync(
            new CompensationRecord("C-1", "J-1", DateTimeOffset.UnixEpoch, "M-1", new[]
            {
                new ProfilePoint(0.0, -0.001),
                new ProfilePoint(2000.0, 0.002),
            }),
            CancellationToken.None);

        CompensationRecord? loaded = await compensations.GetLatestByJobAsync("J-1", CancellationToken.None);
        loaded!.Points.Should().HaveCount(2);
        loaded.BasedOnMeasurementId.Should().Be("M-1");
    }

    [Fact]
    public async Task A_compensation_cannot_reference_a_measurement_that_does_not_exist()
    {
        await MigratedAsync();
        await SeedJobAsync();
        var compensations = new SqliteCompensationRepository(this.database);

        await compensations.Invoking(repository => repository.AddAsync(
            new CompensationRecord("C-x", "J-1", DateTimeOffset.UnixEpoch, "missing", new[]
            {
                new ProfilePoint(0.0, 0.0),
                new ProfilePoint(2000.0, 0.0),
            }),
            CancellationToken.None))
            .Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>("外键约束应当拦住悬空引用");
    }

    [Fact]
    public async Task Alarms_are_archived_newest_first_and_can_be_purged()
    {
        await MigratedAsync();
        var alarms = new SqliteAlarmRepository(this.database);

        await alarms.AddAsync(DateTimeOffset.UnixEpoch, 2, "Alarm_GatewayFailure", "boom", AlarmCodes.GatewayFailure, CancellationToken.None);
        await alarms.AddAsync(DateTimeOffset.UnixEpoch.AddYears(10), 1, "Alarm_ConnectionLost", null, AlarmCodes.ConnectionLost, CancellationToken.None);

        IReadOnlyList<AlarmRecord> listed = await alarms.ListAsync(10, CancellationToken.None);
        listed.Should().HaveCount(2);
        listed[0].MessageResourceKey.Should().Be("Alarm_ConnectionLost");
        listed[0].Code.Should().Be(AlarmCodes.ConnectionLost);
        AlarmCodes.IsHmiCode(listed[0].Code).Should().BeTrue("上位机报警必须落在 800000–800999 号段");

        (await alarms.PurgeOlderThanAsync(DateTimeOffset.UnixEpoch.AddYears(1), CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task A_newer_schema_than_this_build_supports_is_refused()
    {
        await MigratedAsync();
        await using Microsoft.Data.Sqlite.SqliteConnection connection =
            await this.database.OpenAsync(CancellationToken.None);
        await using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 999;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await this.database.Invoking(db => db.MigrateAsync(CancellationToken.None))
            .Should().ThrowAsync<DataStoreException>();
    }

    private async Task SeedJobAsync()
    {
        await new SqliteRollRepository(this.database).UpsertAsync(
            new RollRecord("R-1", "WR", RollGeometry.FromDiameter(2000.0, 650.0), null, DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        await new SqliteJobRepository(this.database).SaveAsync(CreateJob(), JobState.Draft, CancellationToken.None);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        this.workspace.Dispose();
    }
}
