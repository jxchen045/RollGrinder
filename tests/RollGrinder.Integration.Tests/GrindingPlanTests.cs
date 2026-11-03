using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 第 1 期验收（设计说明《轧辊与磨削方案》第 10 节）：新辊 → 方案 → 磨 → 改方案（自动另存）→ 再磨，记录指向正确方案；
/// 实测长度短于名义时核对页显示截取并写入快照。
/// </summary>
public sealed class GrindingPlanTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private static CompositeRollProfile CrownWithEnds(double crown) => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
        new SequentialSegment(ProfileTypeKeys.Crown, 1720.0,
            new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(crown))),
        new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
    });

    private static GrindingProgram Program() => GrindingProgram.Create("G-STD", "工作辊标准", new[]
    {
        new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
        new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
        new GrindingJobStep(3, StepTypeKeys.SparkOut, new SparkOutStepType().Schema.CreateDefaults()),
    }, DateTimeOffset.UnixEpoch);

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

    /// <summary>登记一支新辊：选辊形模板与工艺模板（作业页"新登记"、台账登记都是这样）。</summary>
    private static async Task RegisterAsync(ServiceProvider services, string rollId)
    {
        var library = services.GetRequiredService<ILibraryService>();
        await library.SaveProfileAsync(
            RollProfileDefinition.Create("T-CROWN", "F3 标准凸度", 1800.0, CrownWithEnds(300.0), DateTimeOffset.UnixEpoch) with { ToleranceMicrometer = 5.0 },
            "tester", CancellationToken.None);
        await library.SaveProgramAsync(Program(), "tester", CancellationToken.None);
        await services.GetRequiredService<IRollRepository>().UpsertAsync(
            new RollRecord(rollId, rollId, RollGeometry.FromDiameter(1800.0, 600.0), "高铬铁", DateTimeOffset.UnixEpoch)
            {
                Kind = RollKind.WorkRoll,
                CurrentDiameterMm = 600.35,
                ScrapDiameterMm = 585.0,
                TargetProfileId = "T-CROWN",
                ProgramId = "G-STD",
            },
            CancellationToken.None);
    }

    /// <summary>作业页做的事：取当前方案，按这一次辊身长度对齐、核对，下发。</summary>
    private static async Task<(GrindingJob Job, JobCheckResult Checks, JobDownloadResult Download)> GrindAsync(
        ServiceProvider services, string rollId, string jobId)
    {
        var plans = services.GetRequiredService<IGrindingPlanService>();
        MachineDescription machine = services.GetRequiredService<MachineDescription>();
        HmiSettings settings = services.GetRequiredService<HmiSettings>();
        GrindingPlanRecord plan = (await plans.CurrentAsync(rollId, CancellationToken.None))!;
        RollRecord roll = (await services.GetRequiredService<IRollRepository>().GetAsync(rollId, CancellationToken.None))!;
        PlanContent content = (await plans.GetContentAsync(plan.PlanId, CancellationToken.None))!;

        LengthAlignment alignment = GrindingPlanService.AlignForJob(
            content.Profile, roll.GrindBodyLengthMm, plan.EndZoneMm, machine, services.GetRequiredService<RollProfileTypeRegistry>(), settings.ProfileSampleCount);
        JobCheckResult checks = JobChecklist.Evaluate(
            new JobCheckInput(roll, content.Profile, content.Program, 600.35, true, 350.0, JobDeviation.None, null, Alignment: alignment),
            machine);
        GrindingJob job = GrindingJob.Create(
            jobId, rollId, RollGeometry.FromDiameter(alignment.BodyLengthMm, 600.35), alignment.Profile!, checks.Stock.Steps) with
        {
            PlanId = plan.PlanId,
            PlanNo = plan.PlanNo,
            Alignment = alignment,
            ProfileId = content.Profile.ProfileId,
            ProfileName = content.Profile.Name,
            ProgramId = content.Program.ProgramId,
            ProgramName = content.Program.Name,
            StartDiameterMm = 600.35,
            StockMicrometer = 350.0,
        };
        JobDownloadResult download = await services.GetRequiredService<IJobDownloadService>()
            .DownloadAsync(job, checks.Checks, "操作工", CancellationToken.None);
        return (job, checks, download);
    }

    [Fact]
    public async Task New_roll_plan_grind_edit_grind_again_keeps_every_record_on_the_plan_it_used()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);
        await RegisterAsync(services, "R-100");
        var plans = services.GetRequiredService<IGrindingPlanService>();
        var jobs = services.GetRequiredService<IJobRepository>();

        // 新辊第一次用到：按登记时选的模板建方案 1，设为当前；模板列表里看不到方案自己的那一份。
        GrindingPlanRecord first = (await plans.CurrentAsync("R-100", CancellationToken.None))!;
        first.PlanNo.Should().Be(1);
        first.SourceKind.Should().Be(PlanSourceKind.Template);
        (await services.GetRequiredService<IRollProfileRepository>().ListAsync(100, CancellationToken.None))
            .Select(p => p.ProfileId).Should().Equal("T-CROWN");

        // 磨。
        (GrindingJob job1, _, JobDownloadResult download1) = await GrindAsync(services, "R-100", "J-1");
        download1.Succeeded.Should().BeTrue();
        (await plans.UsageAsync(first.PlanId, CancellationToken.None)).UseCount.Should().Be(1);

        // 改方案：方案 1 已用于磨削，内容不改，辊形与工艺一起另存为方案 2 并设为当前。
        PlanContent content1 = (await plans.GetContentAsync(first.PlanId, CancellationToken.None))!;
        PlanSaveOutcome saved = await plans.SaveProfileAsync(
            first.PlanId, content1.Profile with { Profile = CrownWithEnds(280.0) }, setCurrentIfNew: true, "工艺员", CancellationToken.None);
        saved.SavedAsNew.Should().BeTrue();
        saved.OriginalPlanNo.Should().Be(1);
        saved.Plan.PlanNo.Should().Be(2);
        saved.Plan.SourceKind.Should().Be(PlanSourceKind.ThisRollPlan);
        saved.Plan.SourceRef.Should().Be("1");
        (await plans.CurrentAsync("R-100", CancellationToken.None))!.PlanId.Should().Be(saved.Plan.PlanId);

        // 再磨：用方案 2。
        (GrindingJob job2, _, JobDownloadResult download2) = await GrindAsync(services, "R-100", "J-2");
        download2.Succeeded.Should().BeTrue();

        // 记录指向正确的方案；方案 1 的内容还是磨 J-1 时的那一份。
        (await jobs.GetAsync("J-1", CancellationToken.None))!.Value.Job.PlanNo.Should().Be(1);
        (await jobs.GetAsync("J-2", CancellationToken.None))!.Value.Job.PlanId.Should().Be(saved.Plan.PlanId);
        job1.PlanId.Should().Be(first.PlanId);
        job2.PlanNo.Should().Be(2);
        PlanContent frozen = (await plans.GetContentAsync(first.PlanId, CancellationToken.None))!;
        frozen.Profile.Profile.Segments[1].Parameters.Get(CrownProfileType.CrownDiameterMicrometerKey).Number.Should().Be(300.0);
        PlanContent current = (await plans.GetContentAsync(saved.Plan.PlanId, CancellationToken.None))!;
        current.Profile.Profile.Segments[1].Parameters.Get(CrownProfileType.CrownDiameterMicrometerKey).Number.Should().Be(280.0);
        current.Program.Name.Should().Be(frozen.Program.Name, "工艺跟着一起另存");
        current.Program.ProgramId.Should().NotBe(frozen.Program.ProgramId);

        // 用过的不能删；没用过的就地改、可以删。
        (await plans.DeleteAsync(first.PlanId, "工艺员", CancellationToken.None)).Should().Be("Plan_DeleteUsed");
        GrindingPlanRecord copy = await plans.CopyAsync(saved.Plan.PlanId, "凸度280 · 慢拖板", setCurrent: false, "工艺员", CancellationToken.None);
        copy.PlanNo.Should().Be(3);
        PlanSaveOutcome inPlace = await plans.SaveProgramAsync(
            copy.PlanId, current.Program with { Name = "慢拖板" }, setCurrentIfNew: false, "工艺员", CancellationToken.None);
        inPlace.SavedAsNew.Should().BeFalse("没用过的方案就地改");
        inPlace.Plan.PlanNo.Should().Be(3);
        (await plans.DeleteAsync(copy.PlanId, "工艺员", CancellationToken.None)).Should().BeNull();
        (await plans.ListAsync("R-100", CancellationToken.None)).Select(row => row.Plan.PlanNo).Should().Equal(2, 1);
    }

    [Fact]
    public async Task A_measured_body_shorter_than_nominal_is_trimmed_at_both_ends_shown_and_written_to_the_snapshot()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);
        await RegisterAsync(services, "R-200");
        var rolls = services.GetRequiredService<IRollRepository>();
        await services.GetRequiredService<IGrindingPlanService>().CurrentAsync("R-200", CancellationToken.None);
        RollRecord roll = (await rolls.GetAsync("R-200", CancellationToken.None))!;
        await rolls.UpsertAsync(roll with { MeasuredBodyLengthMm = 1797.0 }, CancellationToken.None);

        (GrindingJob job, JobCheckResult checks, JobDownloadResult download) = await GrindAsync(services, "R-200", "J-SHORT");

        // 核对页一行"实测比名义短 3.0 mm，两端各截 1.5 mm"，不拦人。
        JobCheck length = checks.Checks.Single(check => check.Item == JobCheckItems.Length);
        length.Status.Should().Be(JobCheckStatus.Notice);
        length.MessageKey.Should().Be("Check_Length_Trimmed");
        length.Args.Take(2).Should().Equal(3.0, 1.5);
        checks.CanDownload.Should().BeTrue();

        // 下发的是实测长度；辊形不伸缩，两端圆柱各短 1.5。
        download.Succeeded.Should().BeTrue();
        job.Geometry.BodyLengthMm.Should().Be(1797.0);
        job.Profile.Segments.Select(s => s.LengthMm).Should().Equal(38.5, 1720.0, 38.5);

        // 下发快照写明对齐方式与差值。
        string snapshot = (await services.GetRequiredService<IGrindingRecordRepository>().GetSnapshotAsync(download.RecordId!, CancellationToken.None))!;
        JsonNode node = JsonNode.Parse(snapshot)!;
        node["planNo"]!.GetValue<int>().Should().Be(1);
        node["bodyLengthMm"]!.GetValue<double>().Should().Be(1797.0);
        node["lengthAlignment"]!["kind"]!.GetValue<string>().Should().Be(nameof(LengthAlignmentKind.Trimmed));
        node["lengthAlignment"]!["differenceMm"]!.GetValue<double>().Should().Be(-3.0);
        node["lengthAlignment"]!["perEndMm"]!.GetValue<double>().Should().Be(-1.5);
        node["lengthAlignment"]!["nominalLengthMm"]!.GetValue<double>().Should().Be(1800.0);
    }

    [Fact]
    public async Task A_measured_body_that_would_cut_into_the_working_zone_is_blocked()
    {
        await using ServiceProvider services = await BuildAsync();
        await RegisterAsync(services, "R-300");
        var rolls = services.GetRequiredService<IRollRepository>();
        var plans = services.GetRequiredService<IGrindingPlanService>();
        GrindingPlanRecord plan = (await plans.CurrentAsync("R-300", CancellationToken.None))!;
        RollRecord roll = (await rolls.GetAsync("R-300", CancellationToken.None))! with { MeasuredBodyLengthMm = 1700.0 };
        PlanContent content = (await plans.GetContentAsync(plan.PlanId, CancellationToken.None))!;

        JobCheckResult checks = JobChecklist.Evaluate(
            new JobCheckInput(roll, content.Profile, content.Program, 600.35, true, 350.0, JobDeviation.None, null),
            services.GetRequiredService<MachineDescription>());

        checks.Checks.Single(check => check.Item == JobCheckItems.Length).MessageKey.Should().Be("Check_Length_OutOfTolerance");
        checks.CanDownload.Should().BeFalse();
        checks.Alignment.Profile.Should().BeNull();
    }

    [Fact]
    public async Task An_old_ledger_plan_becomes_plan_1_and_keeps_the_inferred_mark()
    {
        await using ServiceProvider services = await BuildAsync();
        await RegisterAsync(services, "R-OLD");
        var rolls = services.GetRequiredService<IRollRepository>();
        await rolls.UpsertAsync((await rolls.GetAsync("R-OLD", CancellationToken.None))! with { PlanInferred = true }, CancellationToken.None);

        GrindingPlanRecord plan = (await services.GetRequiredService<IGrindingPlanService>().CurrentAsync("R-OLD", CancellationToken.None))!;

        plan.SourceKind.Should().Be(PlanSourceKind.Legacy);
        RollRecord migrated = (await rolls.GetAsync("R-OLD", CancellationToken.None))!;
        migrated.CurrentPlanId.Should().Be(plan.PlanId);
        migrated.TargetProfileId.Should().Be(plan.ProfileId, "台账的目标辊形换成方案自己的那一份");
        migrated.PlanInferred.Should().BeTrue("推断来的计划下发一次才算确认");
    }
}
