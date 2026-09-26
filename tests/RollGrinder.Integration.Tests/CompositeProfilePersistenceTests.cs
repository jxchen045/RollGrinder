using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 多段辊形的出口：编辑器叠出来的曲线必须能存下去、读回来、下发出去。
/// 在这之前，辊形页上叠出来的段一离开那一页就没了（只存 profile_type_key 一列）。
/// </summary>
public sealed class CompositeProfilePersistenceTests : IDisposable
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    private readonly TempWorkspace workspace = new();
    private readonly SqliteDatabase database;

    public CompositeProfilePersistenceTests()
    {
        this.database = new SqliteDatabase(Path.Combine(this.workspace.Root, "data", SqliteDatabase.FileName));
    }

    public void Dispose() => this.workspace.Dispose();

    private async Task MigratedAsync()
    {
        await this.database.MigrateAsync(CancellationToken.None);
        await new SqliteRollRepository(this.database).UpsertAsync(
            new RollRecord("R-1", "WR", Geometry, null, DateTimeOffset.UnixEpoch), CancellationToken.None);
    }

    /// <summary>主凸度铺满全长 + 头架端一段锥度（镜像）+ 尾架端一段锥度。</summary>
    private static CompositeRollProfile ThreeSegments()
    {
        var crown = new CrownProfileType();
        var taper = new TaperProfileType();

        return new CompositeRollProfile(new[]
        {
            RollProfileSegment.Create(
                1,
                ProfileTypeKeys.Crown,
                0.0,
                Geometry.BodyLengthMm,
                crown.Schema.CreateDefaults()
                    .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(120.0))),
            RollProfileSegment.Create(
                2, ProfileTypeKeys.Taper, 0.0, 150.0, taper.Schema.CreateDefaults(), isMirrored: true),
            RollProfileSegment.Create(
                3, ProfileTypeKeys.Taper, 1850.0, Geometry.BodyLengthMm, taper.Schema.CreateDefaults()),
        });
    }

    private static GrindingJob JobWith(CompositeRollProfile profile) => GrindingJob.Create(
        "J-1",
        "R-1",
        Geometry,
        profile,
        new[] { new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()) });

    [Fact]
    public async Task Every_segment_survives_a_save_and_load()
    {
        await MigratedAsync();
        var jobs = new SqliteJobRepository(this.database);

        await jobs.SaveAsync(JobWith(ThreeSegments()), JobState.Draft, CancellationToken.None);
        (GrindingJob Job, JobState State)? loaded = await jobs.GetAsync("J-1", CancellationToken.None);

        IReadOnlyList<RollProfileSegment> segments = loaded!.Value.Job.Profile.Segments;
        segments.Should().HaveCount(3);
        segments.Select(segment => segment.Order).Should().Equal(1, 2, 3);
        segments[0].ProfileTypeKey.Should().Be(ProfileTypeKeys.Crown);
        segments[0].Parameters.GetNumber(CrownProfileType.CrownDiameterMicrometerKey).Should().Be(120.0);
        segments[1].Should().BeEquivalentTo(new { FromMm = 0.0, ToMm = 150.0, IsMirrored = true });
        segments[2].Should().BeEquivalentTo(new { FromMm = 1850.0, ToMm = 2000.0, IsMirrored = false });
    }

    [Fact]
    public async Task Saving_a_job_twice_replaces_its_segments_instead_of_appending()
    {
        await MigratedAsync();
        var jobs = new SqliteJobRepository(this.database);

        await jobs.SaveAsync(JobWith(ThreeSegments()), JobState.Draft, CancellationToken.None);
        await jobs.SaveAsync(
            JobWith(ThreeSegments().RemoveAt(3)), JobState.Draft, CancellationToken.None);

        (GrindingJob Job, JobState State)? loaded = await jobs.GetAsync("J-1", CancellationToken.None);
        loaded!.Value.Job.Profile.Segments.Should().HaveCount(2);
    }

    [Fact]
    public async Task Where_the_profile_came_from_is_recorded_for_traceability()
    {
        await MigratedAsync();
        var jobs = new SqliteJobRepository(this.database);

        GrindingJob job = JobWith(ThreeSegments()) with { ProfileId = "P-7", ProfileName = "CVC-1780" };
        await jobs.SaveAsync(job, JobState.Draft, CancellationToken.None);

        (GrindingJob Job, JobState State)? loaded = await jobs.GetAsync("J-1", CancellationToken.None);
        loaded!.Value.Job.ProfileId.Should().Be("P-7");
        loaded.Value.Job.ProfileName.Should().Be("CVC-1780", "库里改了名也不该改动已经磨过的那支辊的记录");
    }

    [Fact]
    public async Task A_job_stored_before_the_migration_still_opens()
    {
        // 迁移前的作业只有 profile_type_key 一列加 step_order 0 的参数，没有任何段记录。
        // 把段删光来模拟这种旧行：应当按单段主辊形读回来，而不是炸掉。
        await MigratedAsync();
        var jobs = new SqliteJobRepository(this.database);
        await jobs.SaveAsync(
            GrindingJob.Create(
                "J-1",
                "R-1",
                Geometry,
                ProfileTypeKeys.Crown,
                new CrownProfileType().Schema.CreateDefaults()
                    .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(90.0)),
                new[] { new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()) }),
            JobState.Draft,
            CancellationToken.None);

        await WriteLegacyProfileRowsAsync("J-1", ProfileTypeKeys.Crown, 90.0);

        (GrindingJob Job, JobState State)? loaded = await jobs.GetAsync("J-1", CancellationToken.None);

        loaded!.Value.Job.Profile.Segments.Should().ContainSingle();
        loaded.Value.Job.ProfileTypeKey.Should().Be(ProfileTypeKeys.Crown);
        loaded.Value.Job.Profile.Segments[0].Parameters
            .GetNumber(CrownProfileType.CrownDiameterMicrometerKey).Should().Be(90.0);
        loaded.Value.Job.Profile.Segments[0].ToMm.Should().Be(Geometry.BodyLengthMm, "旧的单曲线铺满全长");
    }

    [Fact]
    public async Task The_profile_library_round_trips()
    {
        await MigratedAsync();
        var library = new SqliteRollProfileRepository(this.database);

        RollProfileDefinition definition = RollProfileDefinition.Create(
            "P-1", "CVC-1780", Geometry.BodyLengthMm, ThreeSegments(), DateTimeOffset.UnixEpoch);
        await library.SaveAsync(definition, CancellationToken.None);

        RollProfileDefinition? loaded = await library.GetAsync("P-1", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("CVC-1780");
        loaded.SegmentCount.Should().Be(3);
        loaded.ProfileTypeKey.Should().Be(ProfileTypeKeys.Crown);

        IReadOnlyList<RollProfileSummary> listed = await library.ListAsync(10, CancellationToken.None);
        listed.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Name = "CVC-1780", SegmentCount = 3, BodyLengthMm = 2000.0 });
    }

    [Fact]
    public async Task A_profile_name_is_taken_only_by_another_profile()
    {
        await MigratedAsync();
        var library = new SqliteRollProfileRepository(this.database);
        await library.SaveAsync(
            RollProfileDefinition.Create("P-1", "工作辊-凸度300", Geometry.BodyLengthMm, ThreeSegments(), DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        (await library.IsNameTakenAsync("工作辊-凸度300", null, CancellationToken.None)).Should().BeTrue();
        (await library.IsNameTakenAsync("工作辊-凸度300 ", "P-2", CancellationToken.None)).Should().BeTrue("另一条不能叫同一个名字");
        (await library.IsNameTakenAsync("工作辊-凸度300", "P-1", CancellationToken.None)).Should().BeFalse("存回自己不算重名");
    }

    [Fact]
    public async Task Saving_a_library_profile_twice_replaces_its_segments()
    {
        await MigratedAsync();
        var library = new SqliteRollProfileRepository(this.database);

        RollProfileDefinition definition = RollProfileDefinition.Create(
            "P-1", "CVC-1780", Geometry.BodyLengthMm, ThreeSegments(), DateTimeOffset.UnixEpoch);
        await library.SaveAsync(definition, CancellationToken.None);
        await library.SaveAsync(
            definition with { Name = "CVC-1780 改", Profile = ThreeSegments().RemoveAt(2) },
            CancellationToken.None);

        RollProfileDefinition? loaded = await library.GetAsync("P-1", CancellationToken.None);
        loaded!.Name.Should().Be("CVC-1780 改");
        loaded.SegmentCount.Should().Be(2);
    }

    [Fact]
    public async Task Deleting_a_library_profile_leaves_jobs_that_used_it_intact()
    {
        // 作业存的是快照，不是引用——库里删掉一条辊形，已经磨过的那支辊照样打得开。
        await MigratedAsync();
        var library = new SqliteRollProfileRepository(this.database);
        var jobs = new SqliteJobRepository(this.database);

        await library.SaveAsync(
            RollProfileDefinition.Create("P-1", "CVC-1780", Geometry.BodyLengthMm, ThreeSegments(), DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        await jobs.SaveAsync(
            JobWith(ThreeSegments()) with { ProfileId = "P-1", ProfileName = "CVC-1780" },
            JobState.Handed,
            CancellationToken.None);

        await library.DeleteAsync("P-1", CancellationToken.None);

        (await library.ListAsync(10, CancellationToken.None)).Should().BeEmpty();
        (GrindingJob Job, JobState State)? loaded = await jobs.GetAsync("J-1", CancellationToken.None);
        loaded!.Value.Job.Profile.Segments.Should().HaveCount(3);
        loaded.Value.Job.ProfileName.Should().Be("CVC-1780");
    }

    /// <summary>补一份迁移前的表示：profile_type_key 那一列 + step_order 0 的参数行。</summary>
    private async Task WriteLegacyProfileRowsAsync(string jobId, string profileTypeKey, double crownMicrometer)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(CancellationToken.None);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                DELETE FROM job_profile_segment_parameter WHERE job_id = $job;
                DELETE FROM job_profile_segment WHERE job_id = $job;
                UPDATE job SET profile_type_key = $type WHERE job_id = $job;
                """;
            command.Parameters.AddWithValue("$job", jobId);
            command.Parameters.AddWithValue("$type", profileTypeKey);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO job_parameter (job_id, step_order, parameter_key, value_kind, value_text)
                VALUES ($job, 0, $key, $kind, $value);
                """;
            command.Parameters.AddWithValue("$job", jobId);
            command.Parameters.AddWithValue("$key", CrownProfileType.CrownDiameterMicrometerKey);
            command.Parameters.AddWithValue("$kind", (int)ParameterValueKind.Number);
            command.Parameters.AddWithValue(
                "$value", crownMicrometer.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
