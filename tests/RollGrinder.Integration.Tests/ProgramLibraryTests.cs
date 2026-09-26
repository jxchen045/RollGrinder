using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Core;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 程序库：程序是可复用的模板，不再挂在某一份作业下面。
/// 作业引用它的时候复制一份快照，库里之后改了不动已经磨过的那支辊的记录。
/// </summary>
public sealed class ProgramLibraryTests : IDisposable
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    private readonly TempWorkspace workspace = new();
    private readonly SqliteDatabase database;

    public ProgramLibraryTests()
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

    private static GrindingJobStep[] ThreeSteps() => new[]
    {
        new GrindingJobStep(1, StepTypeKeys.ShortStroke, new ShortStrokeStepType().Schema.CreateDefaults()),
        new GrindingJobStep(
            2,
            StepTypeKeys.Rough,
            new RoughGrindingStepType().Schema.CreateDefaults()
                .With(StepParameterKeys.PassCount, ParameterValue.FromNumber(7.0))),
        new GrindingJobStep(3, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
    };

    private static GrindingProgram Program(string id = "G-1", string name = "热轧工作辊 粗到精") =>
        GrindingProgram.Create(id, name, ThreeSteps(), DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_program_round_trips_with_its_steps_and_switches()
    {
        await MigratedAsync();
        var library = new SqliteProgramRepository(this.database);

        ParameterSet options = ProgramOptionCatalog.Defaults
            .With(ProgramOptionKeys.EddyCurrentTest, ParameterValue.FromBoolean(true));
        await library.SaveAsync(
            GrindingProgram.Create("G-1", "热轧工作辊 粗到精", ThreeSteps(), DateTimeOffset.UnixEpoch, options),
            CancellationToken.None);

        GrindingProgram? loaded = await library.GetAsync("G-1", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("热轧工作辊 粗到精");
        loaded.Steps.Select(step => step.StepTypeKey)
            .Should().Equal(StepTypeKeys.ShortStroke, StepTypeKeys.Rough, StepTypeKeys.Finish);
        loaded.Steps[1].Parameters.GetNumber(StepParameterKeys.PassCount).Should().Be(7.0);
        loaded.IsProgramOptionEnabled(ProgramOptionKeys.EddyCurrentTest).Should().BeTrue();
    }

    [Fact]
    public async Task A_program_read_back_has_every_switch_defined()
    {
        // 少一个键不该让"这个开关开没开"变成未定义。
        await MigratedAsync();
        var library = new SqliteProgramRepository(this.database);
        await library.SaveAsync(Program(), CancellationToken.None);

        GrindingProgram? loaded = await library.GetAsync("G-1", CancellationToken.None);

        loaded!.ProgramOptions.Count.Should().Be(ProgramOptionCatalog.All.Count);
        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All)
        {
            loaded.IsProgramOptionEnabled(option.Key).Should().Be(option.DefaultEnabled);
        }
    }

    [Fact]
    public async Task A_name_is_taken_only_by_another_program()
    {
        // 第一轮甲方测试：另存为不问名字，库里出了两支 "Test"。现在存之前先问一句。
        await MigratedAsync();
        var library = new SqliteProgramRepository(this.database);
        await library.SaveAsync(Program("G-1", "Test"), CancellationToken.None);

        (await library.FindIdByNameAsync("Test", null, CancellationToken.None)).Should().NotBeNull("新建一支同名的不行");
        (await library.FindIdByNameAsync(" test ", null, CancellationToken.None)).Should().NotBeNull("首尾空白、大小写不同也算同名");
        (await library.FindIdByNameAsync("Test", "G-1", CancellationToken.None)).Should().BeNull("存回自己不算重名");
        (await library.FindIdByNameAsync("Test-副本", null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Saving_a_program_twice_replaces_its_steps_instead_of_appending()
    {
        await MigratedAsync();
        var library = new SqliteProgramRepository(this.database);

        await library.SaveAsync(Program(), CancellationToken.None);
        await library.SaveAsync(
            GrindingProgram.Create("G-1", "只留两道", ThreeSteps().Take(2), DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        GrindingProgram? loaded = await library.GetAsync("G-1", CancellationToken.None);
        loaded!.StepCount.Should().Be(2);
        loaded.Name.Should().Be("只留两道");
    }

    [Fact]
    public async Task The_listing_is_newest_first_and_carries_the_step_count()
    {
        await MigratedAsync();
        var library = new SqliteProgramRepository(this.database);

        await library.SaveAsync(
            Program("G-1", "旧的") with { ModifiedAtUtc = DateTimeOffset.UnixEpoch },
            CancellationToken.None);
        await library.SaveAsync(
            Program("G-2", "新的") with { ModifiedAtUtc = DateTimeOffset.UnixEpoch.AddDays(1) },
            CancellationToken.None);

        IReadOnlyList<ProgramSummary> listed = await library.ListAsync(10, CancellationToken.None);

        listed.Select(entry => entry.Name).Should().Equal("新的", "旧的");
        listed[0].StepCount.Should().Be(3);
    }

    [Fact]
    public async Task Deleting_a_program_leaves_jobs_that_used_it_intact()
    {
        // 作业存的是快照，不是引用——库里删掉一支程序，已经磨过的那支辊照样打得开。
        await MigratedAsync();
        var library = new SqliteProgramRepository(this.database);
        var jobs = new SqliteJobRepository(this.database);

        await library.SaveAsync(Program(), CancellationToken.None);
        GrindingProgram? program = await library.GetAsync("G-1", CancellationToken.None);

        GrindingJob job = GrindingJob.Create(
            "J-1",
            "R-1",
            Geometry,
            ProfileTypeKeys.Cylindrical,
            new CylindricalProfileType().Schema.CreateDefaults(),
            program!.Steps,
            program.ProgramOptions) with
        {
            ProgramId = program.ProgramId,
            ProgramName = program.Name,
        };
        await jobs.SaveAsync(job, JobState.Handed, CancellationToken.None);

        await library.DeleteAsync("G-1", CancellationToken.None);

        (await library.ListAsync(10, CancellationToken.None)).Should().BeEmpty();

        (GrindingJob Job, JobState State)? loaded = await jobs.GetAsync("J-1", CancellationToken.None);
        loaded!.Value.Job.Steps.Should().HaveCount(3);
        loaded.Value.Job.ProgramId.Should().Be("G-1");
        loaded.Value.Job.ProgramName.Should().Be("热轧工作辊 粗到精", "记录要记当时用的是哪一支程序");
    }

    [Fact]
    public async Task Editing_a_program_afterwards_does_not_change_a_job_already_ground()
    {
        await MigratedAsync();
        var library = new SqliteProgramRepository(this.database);
        var jobs = new SqliteJobRepository(this.database);

        await library.SaveAsync(Program(), CancellationToken.None);
        GrindingProgram? program = await library.GetAsync("G-1", CancellationToken.None);

        await jobs.SaveAsync(
            GrindingJob.Create(
                "J-1",
                "R-1",
                Geometry,
                ProfileTypeKeys.Cylindrical,
                new CylindricalProfileType().Schema.CreateDefaults(),
                program!.Steps) with { ProgramId = "G-1", ProgramName = program.Name },
            JobState.Handed,
            CancellationToken.None);

        // 库里把粗磨道次从 7 改成 20，并砍掉一道工序。
        await library.SaveAsync(
            GrindingProgram.Create(
                "G-1",
                "热轧工作辊 粗到精",
                new[]
                {
                    new GrindingJobStep(
                        1,
                        StepTypeKeys.Rough,
                        new RoughGrindingStepType().Schema.CreateDefaults()
                            .With(StepParameterKeys.PassCount, ParameterValue.FromNumber(20.0))),
                },
                DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        (GrindingJob Job, JobState State)? loaded = await jobs.GetAsync("J-1", CancellationToken.None);
        loaded!.Value.Job.Steps.Should().HaveCount(3, "作业存的是快照");
        loaded.Value.Job.Steps[1].Parameters.GetNumber(StepParameterKeys.PassCount).Should().Be(7.0);
    }

    [Fact]
    public async Task A_program_entry_without_steps_is_refused_rather_than_guessed()
    {
        await MigratedAsync();
        var library = new SqliteProgramRepository(this.database);
        await library.SaveAsync(Program(), CancellationToken.None);

        // 把工序删光，模拟一个坏掉的库条目。
        await using (Microsoft.Data.Sqlite.SqliteConnection connection =
                     await this.database.OpenAsync(CancellationToken.None))
        {
            await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM program_step WHERE program_id = 'G-1';";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await library.Invoking(repository => repository.GetAsync("G-1", CancellationToken.None))
            .Should().ThrowAsync<DataStoreException>();
    }

    [Fact]
    public void A_program_with_a_gap_in_its_step_order_is_refused()
    {
        Action create = () => GrindingProgram.Create(
            "G-1",
            "有洞",
            new[]
            {
                new GrindingJobStep(1, StepTypeKeys.Rough, ParameterSet.Empty),
                new GrindingJobStep(3, StepTypeKeys.Finish, ParameterSet.Empty),
            },
            DateTimeOffset.UnixEpoch);

        create.Should().Throw<DomainException>();
    }
}
