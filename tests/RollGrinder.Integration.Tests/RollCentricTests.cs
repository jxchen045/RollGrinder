using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Library;
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>以轧辊为中心（关系设计最终版）：计划、2% 规则、核对清单、库版本与在用清单、待磨清单、改计划、导入、合格判定。</summary>
public sealed class RollCentricTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private static CompositeRollProfile CrownWithTapers(double middle = 1500.0, double crown = 300.0) => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Taper, 150.0, new TaperProfileType().Schema.CreateDefaults(), IsMirrored: true),
        new SequentialSegment(
            ProfileTypeKeys.Crown,
            middle,
            new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(crown))),
        new SequentialSegment(ProfileTypeKeys.Taper, 150.0, new TaperProfileType().Schema.CreateDefaults()),
    });

    private static CompositeRollProfile Cvc(double length) => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Cvc, length, new CvcProfileType().Schema.CreateDefaults()),
    });

    private static RollProfileDefinition Profile(string id = "P-1", double length = 1800.0) =>
        RollProfileDefinition.Create(id, "凸度300-" + id, length, CrownWithTapers(length - 300.0), DateTimeOffset.UnixEpoch) with
        {
            ToleranceMicrometer = 5.0,
        };

    private static GrindingProgram Program(string id = "G-1", RollKind kind = RollKind.Unspecified, string? material = null, double rpm = 40.0)
    {
        ParameterSet Rough(double stock) => new RoughGrindingStepType().Schema.CreateDefaults()
            .With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(stock))
            .With(StepParameterKeys.WorkpieceSpeedRpm, ParameterValue.FromNumber(rpm));
        ParameterSet Finish(double stock) => new FinishGrindingStepType().Schema.CreateDefaults()
            .With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(stock))
            .With(StepParameterKeys.WorkpieceSpeedRpm, ParameterValue.FromNumber(rpm));
        return GrindingProgram.Create(id, "工作辊-标准-" + id, new[]
        {
            new GrindingJobStep(1, StepTypeKeys.Rough, Rough(280.0)),
            new GrindingJobStep(2, StepTypeKeys.Finish, Finish(70.0)),
        }, DateTimeOffset.UnixEpoch) with
        {
            StandardStockMicrometer = 350.0,
            ApplicableRollKind = kind,
            ApplicableMaterial = material,
        };
    }

    private static RollRecord Roll(string id = "R-1", double body = 1800.0, double current = 600.35, double? scrap = 585.0) =>
        new RollRecord(id, id, RollGeometry.FromDiameter(body, 600.0), "高铬铁", DateTimeOffset.UnixEpoch)
        {
            Kind = RollKind.WorkRoll,
            CurrentDiameterMm = current,
            ScrapDiameterMm = scrap,
            TargetProfileId = "P-1",
            ProgramId = "G-1",
            Purpose = "F3 上",
            Data = RollDataSheet.Empty with { NetWeightKg = 6800.0 },
        };

    private static MachineDescription Machine(IReadOnlyList<HeadstockSpeedLimit>? table = null, double maxSpeed = 120.0) => new(
        1, "M", "M", new ControllerDescription("Sim", 1), Array.Empty<AxisDescription>(), Array.Empty<MeasurementChannelDescription>(),
        new Dictionary<string, bool>(),
        new Dictionary<string, double>
        {
            [MachineDescription.LengthTolerancePercentKey] = 2.0,
            [MachineDescription.MinWorkpieceSurfaceSpeedKey] = 30.0,
            [MachineDescription.MaxWorkpieceSurfaceSpeedKey] = maxSpeed,
        },
        new WorkpieceLimits(300.0, 3000.0, 200.0, 1600.0, 60000.0),
        new Dictionary<string, int>(),
        HeadstockRpmByWeight: table);

    private static JobCheckResult Check(
        RollRecord? roll = null, GrindingProgram? program = null, double stockUm = 350.0, MachineDescription? machine = null,
        JobDeviation deviation = JobDeviation.None, string? reason = null, RollProfileDefinition? profile = null, bool measured = true)
    {
        roll ??= Roll();
        return JobChecklist.Evaluate(
            new JobCheckInput(roll, profile ?? Profile(), program ?? Program(), roll.StartDiameterMm, measured, stockUm, deviation, reason),
            machine ?? Machine());
    }

    private static JobCheck Item(JobCheckResult result, string item) => result.Checks.Single(check => check.Item == item);

    // ───────────── 2% 规则 ─────────────

    [Fact]
    public void Tapers_keep_their_length_and_the_middle_fills_the_body()
    {
        BodyFitResult fit = BodyLengthFit.Fit(CrownWithTapers(), 1800.0, 2000.0, 2.0);

        fit.Kind.Should().Be(BodyFitKind.MiddleAdjusted, "一般辊形不限差多少");
        fit.Profile!.Segments.Select(s => s.LengthMm).Should().Equal(150.0, 1700.0, 150.0);
        fit.Profile.EndZMm.Should().BeApproximately(2000.0, 1e-9);
    }

    [Fact]
    public void A_cvc_profile_tolerates_two_percent_and_no_more()
    {
        BodyLengthFit.Fit(Cvc(1800.0), 1800.0, 1830.0, 2.0).Kind.Should().Be(BodyFitKind.MiddleAdjusted);
        BodyFitResult far = BodyLengthFit.Fit(Cvc(1800.0), 1800.0, 1900.0, 2.0);
        far.Kind.Should().Be(BodyFitKind.TooDifferent);
        far.Profile.Should().BeNull();
        BodyLengthFit.Fit(Cvc(1800.0), 1800.0, 1800.3, 2.0).Kind.Should().Be(BodyFitKind.Exact);
    }

    // ───────────── 核对清单 ─────────────

    [Fact]
    public void The_normal_case_passes_every_check()
    {
        JobCheckResult result = Check();

        result.CanDownload.Should().BeTrue();
        result.Checks.Should().NotContain(c => c.Status == JobCheckStatus.Block);
        result.Stock.Steps[0].Parameters.GetNumber(StepParameterKeys.StockDiameterMicrometer).Should().Be(280.0);
    }

    [Fact]
    public void Grinding_below_the_scrap_diameter_is_blocked_with_the_most_that_can_still_be_ground()
    {
        JobCheckResult result = Check(roll: Roll(current: 585.2), stockUm: 350.0);

        JobCheck stock = Item(result, JobCheckItems.Stock);
        stock.Status.Should().Be(JobCheckStatus.Block);
        stock.MessageKey.Should().Be("Check_Stock_BelowScrap");
        ((double)stock.Args[2]).Should().BeApproximately(0.2, 1e-9, "最多还能磨 0.2 mm");
        result.CanDownload.Should().BeFalse();
    }

    [Fact]
    public void Extra_stock_goes_to_rough_grinding_and_too_little_stock_is_blocked()
    {
        Check(stockUm: 500.0).Stock.Steps[0].Parameters.GetNumber(StepParameterKeys.StockDiameterMicrometer).Should().Be(430.0);
        Item(Check(stockUm: 50.0), JobCheckItems.StockSplit).Status.Should().Be(JobCheckStatus.Block, "少于精磨量不能靠少磨精磨凑");
    }

    [Fact]
    public void A_program_for_another_roll_kind_is_blocked_and_another_material_is_only_noticed()
    {
        Item(Check(program: Program(kind: RollKind.BackupRoll)), JobCheckItems.RollKind).Status.Should().Be(JobCheckStatus.Block);
        Item(Check(program: Program(material: "高速钢")), JobCheckItems.Material).Status.Should().Be(JobCheckStatus.Notice);
    }

    [Fact]
    public void Retired_rolls_disabled_entries_and_missing_reasons_cannot_be_downloaded()
    {
        Check(roll: Roll() with { Retired = true }).CanDownload.Should().BeFalse();
        Check(program: Program() with { Disabled = true }).CanDownload.Should().BeFalse();
        Check(deviation: JobDeviation.ThisTimeOnly).CanDownload.Should().BeFalse("仅本次必须选原因");
        Check(deviation: JobDeviation.ThisTimeOnly, reason: "Reason_Trial").CanDownload.Should().BeTrue();
    }

    [Fact]
    public void Workpiece_speed_is_checked_against_surface_speed_and_the_weight_table()
    {
        // 600 mm × 40 r/min ≈ 75 m/min；上限压到 60 就超了。
        Item(Check(machine: Machine(maxSpeed: 60.0)), JobCheckItems.WorkSpeed).Status.Should().Be(JobCheckStatus.Block);

        var table = new[] { new HeadstockSpeedLimit(5000.0, 60.0), new HeadstockSpeedLimit(10000.0, 30.0) };
        JobCheck weight = Item(Check(machine: Machine(table)), JobCheckItems.WeightSpeed);
        weight.Status.Should().Be(JobCheckStatus.Block, "6.8 t 的辊头架最高 30 r/min，程序要 40");
        Item(Check(program: Program(rpm: 25.0), machine: Machine(table)), JobCheckItems.WeightSpeed).Status.Should().Be(JobCheckStatus.Pass);
    }

    [Fact]
    public void A_new_roll_still_at_its_nominal_diameter_asks_to_be_measured_first()
    {
        RollRecord fresh = Roll() with { CurrentDiameterMm = null };
        Item(Check(roll: fresh, measured: false), JobCheckItems.StartDiameter).Status.Should().Be(JobCheckStatus.Notice);
        Item(Check(roll: fresh, measured: true), JobCheckItems.StartDiameter).Status.Should().Be(JobCheckStatus.Pass);
    }

    [Fact]
    public void A_roll_close_to_scrap_gets_a_reminder()
    {
        JobCheck life = Item(Check(roll: Roll(current: 586.0)), JobCheckItems.RemainingLife);
        life.Status.Should().Be(JobCheckStatus.Notice);
    }

    // ───────────── 合格判定 ─────────────

    [Fact]
    public void The_verdict_needs_a_measurement_and_fails_on_any_item_over_tolerance()
    {
        GrindingVerdict.Evaluate(null, 5.0, null, 3.0).Passed.Should().BeNull("没量不能当合格");
        GrindingVerdict.Evaluate(4.0, 5.0, 2.0, 3.0).Passed.Should().BeTrue();
        GrindingVerdict.Evaluate(6.1, 5.0, 2.0, 3.0).Passed.Should().BeFalse();
        GrindingVerdict.Evaluate(4.0, 5.0, null, 3.0).Passed.Should().BeTrue("只量了辊形也能判");
    }

    // ───────────── 快照 ─────────────

    [Fact]
    public void Library_entries_round_trip_through_their_archived_json()
    {
        GrindingProgram program = Program(kind: RollKind.WorkRoll, material: "高铬铁") with { Version = 3 };
        GrindingProgram back = LibrarySnapshotJson.ReadProgram(LibrarySnapshotJson.Write(program));
        back.Version.Should().Be(3);
        back.ApplicableRollKind.Should().Be(RollKind.WorkRoll);
        back.Steps.Select(s => s.Parameters.GetNumber(StepParameterKeys.StockDiameterMicrometer)).Should().Equal(280.0, 70.0);

        RollProfileDefinition profile = Profile() with { Version = 2 };
        RollProfileDefinition profileBack = LibrarySnapshotJson.ReadProfile(LibrarySnapshotJson.Write(profile));
        profileBack.Version.Should().Be(2);
        profileBack.ToleranceMicrometer.Should().Be(5.0);
        profileBack.Profile.Segments.Should().HaveCount(3);
    }

    // ───────────── 升级：旧辊的计划按最近一次作业推断 ─────────────

    [Fact]
    public async Task Upgrading_infers_each_rolls_plan_from_its_latest_job_and_flags_it()
    {
        var database = new SqliteDatabase(Path.Combine(this.workspace.Root, "data", SqliteDatabase.FileName));
        await database.MigrateToAsync(SqliteDatabase.ExpectedSchemaVersion - 1, CancellationToken.None);
        await using (SqliteConnection connection = await database.OpenAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO roll (roll_id, code, body_length_mm, nominal_radius_mm, created_at_utc) VALUES ('R-OLD', 'R-OLD', 1800, 300, '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO roll (roll_id, code, body_length_mm, nominal_radius_mm, created_at_utc) VALUES ('R-NEW', 'R-NEW', 1800, 300, '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO roll_profile (profile_id, name, body_length_mm, created_at_utc, modified_at_utc) VALUES ('P-A', 'A', 1800, 'x', 'x');
                INSERT INTO roll_profile (profile_id, name, body_length_mm, created_at_utc, modified_at_utc) VALUES ('P-B', 'B', 1800, 'x', 'x');
                INSERT INTO job (job_id, roll_id, profile_type_key, body_length_mm, nominal_radius_mm, state, created_at_utc, profile_id)
                    VALUES ('J1', 'R-OLD', 'Crown', 1800, 300, 2, '2026-02-01T00:00:00.0000000+00:00', 'P-A');
                INSERT INTO job (job_id, roll_id, profile_type_key, body_length_mm, nominal_radius_mm, state, created_at_utc, profile_id)
                    VALUES ('J2', 'R-OLD', 'Crown', 1800, 300, 2, '2026-03-01T00:00:00.0000000+00:00', 'P-B');
                INSERT INTO job (job_id, roll_id, profile_type_key, body_length_mm, nominal_radius_mm, state, created_at_utc, profile_id)
                    VALUES ('J3', 'R-OLD', 'Crown', 1800, 300, 2, '2026-04-01T00:00:00.0000000+00:00', 'P-GONE');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await database.MigrateAsync(CancellationToken.None);

        var rolls = new SqliteRollRepository(database);
        RollRecord old = (await rolls.GetAsync("R-OLD", CancellationToken.None))!;
        old.TargetProfileId.Should().Be("P-B", "最近一次作业用的、库里还在的那条");
        old.PlanInferred.Should().BeTrue();
        RollRecord fresh = (await rolls.GetAsync("R-NEW", CancellationToken.None))!;
        fresh.TargetProfileId.Should().BeNull();
        fresh.PlanInferred.Should().BeFalse();
    }

    // ───────────── 库：版本、在用清单、停用 ─────────────

    private async Task<ServiceProvider> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);
        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);
        await new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName)).MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);
        return services.BuildServiceProvider();
    }

    private static async Task SeedAsync(ServiceProvider services, params RollRecord[] rolls)
    {
        var library = services.GetRequiredService<ILibraryService>();
        await library.SaveProfileAsync(Profile(), "tester", CancellationToken.None);
        await library.SaveProfileAsync(Profile("P-2", 2400.0), "tester", CancellationToken.None);
        await library.SaveProgramAsync(Program(), "tester", CancellationToken.None);
        foreach (RollRecord roll in rolls)
        {
            await services.GetRequiredService<IRollRepository>().UpsertAsync(roll, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Saving_a_library_entry_archives_the_old_version_and_shows_who_uses_it()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, Roll("R-1"), Roll("R-2"), Roll("R-3") with { TargetProfileId = "P-2" });
        var library = services.GetRequiredService<ILibraryService>();

        RollProfileDefinition saved = await library.SaveProfileAsync(Profile() with { Name = "凸度280" }, "tester", CancellationToken.None);

        saved.Version.Should().Be(2);
        IReadOnlyList<LibraryVersionEntry> versions = await library.VersionsAsync(SqliteLibraryVersionRepository.ProfileKind, "P-1", CancellationToken.None);
        versions.Single().Version.Should().Be(1);
        LibrarySnapshotJson.ReadProfile(versions.Single().Payload).Name.Should().Be("凸度300-P-1", "旧版本只读留档");
        (await library.ProfileUsersAsync("P-1", CancellationToken.None)).Select(r => r.RollId).Should().Equal("R-1", "R-2");
        (await library.TryDeleteProfileAsync("P-1", "tester", CancellationToken.None)).Should().BeFalse("被引用的不能删，只能停用");

        await library.SetProfileDisabledAsync("P-1", true, "tester", CancellationToken.None);
        (await services.GetRequiredService<IRollProfileRepository>().GetAsync("P-1", CancellationToken.None))!.Disabled.Should().BeTrue();
    }

    // ───────────── 待磨清单、改计划、返磨、中断 ─────────────

    [Fact]
    public async Task The_overview_lists_rolls_near_scrap_failed_rolls_and_todays_plan_changes()
    {
        await using ServiceProvider services = await BuildAsync();
        // R-NEAR：磨后只剩不到两次标准余量；R-OK：远着呢。
        await SeedAsync(services, Roll("R-NEAR", current: 585.5, scrap: 585.0), Roll("R-OK"), Roll("R-FAIL"));
        var planning = services.GetRequiredService<IRollPlanningService>();
        var jobs = services.GetRequiredService<IJobRepository>();
        var records = services.GetRequiredService<IGrindingRecordRepository>();
        GrindingJob failedJob = GrindingJob.Create("J-F", "R-FAIL", RollGeometry.FromDiameter(1800.0, 600.0), CrownWithTapers(), Program().Steps);
        await jobs.SaveAsync(failedJob, JobState.Completed, CancellationToken.None);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await records.AddAsync(new GrindingRecord("REC-F", "J-F", now.AddHours(-1), now, JobState.Completed, null), CancellationToken.None);
        await records.SetVerdictAsync("REC-F", false, CancellationToken.None);
        await planning.ChangePlanAsync(new[] { "R-OK" }, "P-1", null, null, "admin", CancellationToken.None);

        RollOverview overview = await planning.OverviewAsync(now.AddDays(-1), CancellationToken.None);

        overview.NearScrap.Select(item => item.RollId).Should().Equal("R-NEAR");
        overview.Failed.Should().ContainSingle(item => item.RollId == "R-FAIL" && !item.RegrindCreated);
        await planning.CreateRegrindAsync("J-F", CancellationToken.None);
        (await planning.OverviewAsync(now.AddDays(-1), CancellationToken.None)).Failed
            .Should().ContainSingle(item => item.RollId == "R-FAIL" && item.RegrindCreated);
    }

    [Fact]
    public async Task Interrupted_and_regrind_rolls_are_pinned_on_top_of_the_queue()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, Roll("R-1"), Roll("R-2"), Roll("R-3"), Roll("R-9") with { Retired = true });
        var jobs = services.GetRequiredService<IJobRepository>();
        GrindingJob Job(string id, string roll) =>
            GrindingJob.Create(id, roll, RollGeometry.FromDiameter(1800.0, 600.0), CrownWithTapers(), Program().Steps);
        await jobs.SaveAsync(Job("J-INT", "R-2"), JobState.Interrupted, CancellationToken.None);
        await jobs.SaveAsync(Job("J-OK", "R-3"), JobState.Completed, CancellationToken.None);
        var planning = services.GetRequiredService<IRollPlanningService>();
        string regrind = await planning.CreateRegrindAsync("J-OK", CancellationToken.None);

        IReadOnlyList<QueueEntry> queue = await planning.LoadQueueAsync(100, CancellationToken.None);

        queue.Select(e => (e.Roll.RollId, e.Mark)).Take(2).Should().Equal(("R-2", QueueMark.Interrupted), ("R-3", QueueMark.Regrind));
        queue.Single(e => e.Mark == QueueMark.Regrind).JobId.Should().Be(regrind);
        queue.Should().NotContain(e => e.Roll.RollId == "R-9", "作废的辊不能再下作业");

        await planning.EndInterruptedAsync("J-INT", CancellationToken.None);
        (await planning.LoadQueueAsync(100, CancellationToken.None)).Should().NotContain(e => e.Mark == QueueMark.Interrupted);
    }

    [Fact]
    public async Task Changing_the_plan_of_several_rolls_checks_each_one_and_logs_every_change()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, Roll("R-1"), Roll("R-2"), Roll("R-L", body: 1800.0) with { Retired = true });
        var planning = services.GetRequiredService<IRollPlanningService>();

        IReadOnlyList<PlanChangeOutcome> outcomes = await planning.ChangePlanAsync(
            new[] { "R-1", "R-2", "R-L" }, "P-2", null, "轧制计划调整", "tester", CancellationToken.None);

        // P-2 设计长 2400，辊身 1800：一般辊形中段铺满，可以改；作废的不改。
        outcomes.Where(o => o.Changed).Select(o => o.RollId).Should().Equal("R-1", "R-2");
        outcomes.Single(o => o.RollId == "R-L").ProblemKey.Should().Be("PlanProblem_Retired");
        var rolls = services.GetRequiredService<IRollRepository>();
        (await rolls.GetAsync("R-1", CancellationToken.None))!.TargetProfileId.Should().Be("P-2");
        (await services.GetRequiredService<IChangeLogRepository>().ListAsync(50, CancellationToken.None))
            .Count(e => e.Area == ChangeLogAreas.RollPlan).Should().Be(2);
    }

    [Fact]
    public async Task Plan_changed_in_a_job_is_written_back_after_download_and_this_time_only_is_not()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, Roll("R-1") with { PlanInferred = true }, Roll("R-2"));
        var planning = services.GetRequiredService<IRollPlanningService>();
        var rolls = services.GetRequiredService<IRollRepository>();
        GrindingJob Job(string roll, JobDeviation deviation) =>
            GrindingJob.Create("J-" + roll, roll, RollGeometry.FromDiameter(1800.0, 600.0), CrownWithTapers(), Program().Steps) with
            {
                ProfileId = "P-2",
                ProgramId = "G-1",
                Deviation = deviation,
            };

        await planning.AfterDownloadAsync(Job("R-1", JobDeviation.PlanChanged), "tester", CancellationToken.None);
        await planning.AfterDownloadAsync(Job("R-2", JobDeviation.ThisTimeOnly), "tester", CancellationToken.None);

        RollRecord changed = (await rolls.GetAsync("R-1", CancellationToken.None))!;
        changed.TargetProfileId.Should().Be("P-2");
        changed.PlanInferred.Should().BeFalse();
        (await rolls.GetAsync("R-2", CancellationToken.None))!.TargetProfileId.Should().Be("P-1", "仅本次不改台账");
    }

    // ───────────── 台账导入 ─────────────

    [Fact]
    public async Task Import_previews_every_row_and_writes_only_the_good_ones()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, Roll("R-1"));
        var planning = services.GetRequiredService<IRollPlanningService>();
        string csv =
            "rollId,kind,bodyLengthMm,nominalDiameterMm,currentDiameterMm,scrapDiameterMm,material,purpose,profileName,programName,netWeightKg\r\n"
            + "R-50,工作辊,1800,600,,585,高铬铁,F3 上,凸度300-P-1,工作辊-标准-G-1,6800\r\n"
            + "R-1,WorkRoll,1800,600,598.5,585,,,凸度300-P-1,工作辊-标准-G-1,\r\n"
            + "R-51,工作辊,1800,600,,585,,,没有这条,工作辊-标准-G-1,\r\n"
            + "R-52,工作辊,9000,600,,585,,,凸度300-P-1,工作辊-标准-G-1,\r\n";

        IReadOnlyList<LedgerImportRow> preview = await planning.PreviewImportAsync(csv, CancellationToken.None);

        preview.Select(r => r.Kind).Should().Equal(LedgerImportKind.Add, LedgerImportKind.Update, LedgerImportKind.Error, LedgerImportKind.Error);
        preview[2].ProblemKeys.Should().Contain("Import_Problem_UnknownProfile");
        preview[3].ProblemKeys.Should().Contain("Ledger_Problem_" + RollLedgerProblem.BodyLengthOutOfRange);
        (await planning.ImportAsync(preview, "tester", CancellationToken.None)).Should().Be(2);
        var rolls = services.GetRequiredService<IRollRepository>();
        (await rolls.GetAsync("R-50", CancellationToken.None))!.Purpose.Should().Be("F3 上");
        (await rolls.GetAsync("R-1", CancellationToken.None))!.CurrentDiameterMm.Should().Be(598.5);

        string exported = await planning.ExportCsvAsync(includeRetired: false, CancellationToken.None);
        Csv.Parse(exported)[0].Should().Equal(RollPlanningService.CsvColumns);
        (await planning.PurposesAsync(CancellationToken.None)).Should().Equal("F3 上");
    }

    [Fact]
    public void Csv_handles_quotes_commas_and_a_byte_order_mark()
    {
        IReadOnlyList<IReadOnlyList<string>> rows = Csv.Parse("﻿a,\"b,c\",\"d\"\"e\"\r\n1,2,3\n");
        rows[0].Should().Equal("a", "b,c", "d\"e");
        rows[1].Should().Equal("1", "2", "3");
        Csv.Parse(Csv.Write(rows)).Should().BeEquivalentTo(rows, options => options.WithStrictOrdering());
    }
}
