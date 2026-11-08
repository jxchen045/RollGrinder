using System;
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
/// 第 3 期验收（设计说明《轧辊与磨削方案》第 10 节）：两层参数——超范围不收；记录列出调整；存为新方案来源正确。
/// </summary>
public sealed class PlanAdjustmentTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private static CompositeRollProfile Crown() => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
        new SequentialSegment(ProfileTypeKeys.Crown, 1720.0,
            new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(300.0))),
        new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
    });

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
        ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);

        var library = provider.GetRequiredService<ILibraryService>();
        await library.SaveProfileAsync(
            RollProfileDefinition.Create("T-CROWN", "F3 标准凸度", 1800.0, Crown(), DateTimeOffset.UnixEpoch) with { ToleranceMicrometer = 5.0 },
            "tester", CancellationToken.None);
        await library.SaveProgramAsync(
            GrindingProgram.Create("G-STD", "工作辊标准", new[]
            {
                new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
                new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
                new GrindingJobStep(3, StepTypeKeys.SparkOut, new SparkOutStepType().Schema.CreateDefaults()),
            }, DateTimeOffset.UnixEpoch),
            "tester", CancellationToken.None);
        await provider.GetRequiredService<IRollRepository>().UpsertAsync(
            new RollRecord("R-300", "R-300", RollGeometry.FromDiameter(1800.0, 600.0), "高铬铁", DateTimeOffset.UnixEpoch)
            {
                Kind = RollKind.WorkRoll,
                CurrentDiameterMm = 600.35,
                ScrapDiameterMm = 585.0,
                TargetProfileId = "T-CROWN",
                ProgramId = "G-STD",
            },
            CancellationToken.None);
        return provider;
    }

    /// <summary>作业页做的事：取当前方案，套上本次调整（方案值 → 本次值），下发。</summary>
    private static async Task<(GrindingJob Job, JobDownloadResult Download)> GrindAsync(
        ServiceProvider services, string jobId, params (int Step, string Key, double Value, bool Running)[] changes)
    {
        var plans = services.GetRequiredService<IGrindingPlanService>();
        MachineDescription machine = services.GetRequiredService<MachineDescription>();
        HmiSettings settings = services.GetRequiredService<HmiSettings>();
        GrindingPlanRecord plan = (await plans.CurrentAsync("R-300", CancellationToken.None))!;
        RollRecord roll = (await services.GetRequiredService<IRollRepository>().GetAsync("R-300", CancellationToken.None))!;
        PlanContent content = (await plans.GetContentAsync(plan.PlanId, CancellationToken.None))!;
        LengthAlignment alignment = GrindingPlanService.AlignForJob(
            content.Profile, roll.GrindBodyLengthMm, plan.EndZoneMm, machine, services.GetRequiredService<RollProfileTypeRegistry>(), settings.ProfileSampleCount);
        GrindingProgram normalized = content.Program with
        {
            Steps = ProgramFrame.Normalize(content.Program.Steps, services.GetRequiredService<GrindingStepTypeRegistry>()),
        };
        JobCheckResult checks = JobChecklist.Evaluate(
            new JobCheckInput(roll, content.Profile, normalized, 600.35, true, 350.0, JobDeviation.None, null, Alignment: alignment),
            machine);
        var rows = await services.GetRequiredService<IJobAdjustmentService>().DescribeAsync(plan.PlanId, CancellationToken.None);

        GrindingJobStep[] steps = checks.Stock.Steps.ToArray();
        var adjustments = new System.Collections.Generic.List<JobAdjustment>();
        foreach ((int step, string key, double value, bool running) in changes)
        {
            int index = Array.FindIndex(steps, item => item.Order == step);
            double planValue = rows.Single(row => row.StepOrder == step && row.ParameterKey == key).PlanValue;
            steps[index] = steps[index] with { Parameters = steps[index].Parameters.With(key, ParameterValue.FromNumber(value)) };
            adjustments.Add(new JobAdjustment(step, key, planValue, value, running, "操作工", DateTimeOffset.UnixEpoch.AddHours(step)));
        }

        GrindingJob job = GrindingJob.Create(
            jobId, "R-300", RollGeometry.FromDiameter(alignment.BodyLengthMm, 600.35), alignment.Profile!, steps) with
        {
            PlanId = plan.PlanId,
            PlanNo = plan.PlanNo,
            Alignment = alignment,
            StartDiameterMm = 600.35,
            StockMicrometer = 350.0,
            Adjustments = adjustments,
        };
        JobDownloadResult download = await services.GetRequiredService<IJobDownloadService>()
            .DownloadAsync(job, checks.Checks, "操作工", CancellationToken.None);
        return (job, download);
    }

    private static async Task<double> PlanFeedAsync(ServiceProvider services, int step)
    {
        var plans = services.GetRequiredService<IGrindingPlanService>();
        GrindingPlanRecord plan = (await plans.CurrentAsync("R-300", CancellationToken.None))!;
        return (await services.GetRequiredService<IJobAdjustmentService>().DescribeAsync(plan.PlanId, CancellationToken.None))
            .Single(row => row.StepOrder == step && row.ParameterKey == StepParameterKeys.FeedMmPerMin).PlanValue;
    }

    [Fact]
    public async Task Default_ranges_follow_the_draft_table_and_a_plan_override_wins()
    {
        await using ServiceProvider services = await BuildAsync();
        var plans = services.GetRequiredService<IGrindingPlanService>();
        var adjust = services.GetRequiredService<IJobAdjustmentService>();
        GrindingPlanRecord plan = (await plans.CurrentAsync("R-300", CancellationToken.None))!;

        var rows = await adjust.DescribeAsync(plan.PlanId, CancellationToken.None);
        AdjustableParameter feed = rows.Single(row => row.StepOrder == 2 && row.ParameterKey == StepParameterKeys.FeedMmPerMin);
        feed.Rule.Should().Be(AdjustRule.PlusMinus(10.0));
        feed.Source.Should().Be(RangeSource.Default);
        feed.Bounds.Lower.Should().BeApproximately(feed.PlanValue * 0.9, 1e-6);
        feed.Bounds.Upper.Should().BeApproximately(feed.PlanValue * 1.1, 1e-6);
        rows.Single(row => row.StepOrder == 3 && row.ParameterKey == StepParameterKeys.SparkOutPassCount).Rule.Mode
            .Should().Be(AdjustMode.IncreaseOnly);
        rows.Should().Contain(row => !row.Adjustable, "有不可调（🔒）的参数");

        // 工艺员在方案里把粗磨拖板速度放到 ±20%。
        PlanSaveOutcome saved = await plans.SavePlanAsync(
            plan.PlanId, new PlanEdit(null, null) { Ranges = new[] { new PlanParamRange(2, StepParameterKeys.FeedMmPerMin, "±20%") } },
            false, "工艺员", CancellationToken.None);
        saved.SavedAsNew.Should().BeFalse("没用过的方案就地改");
        AdjustableParameter widened = (await adjust.DescribeAsync(plan.PlanId, CancellationToken.None))
            .Single(row => row.StepOrder == 2 && row.ParameterKey == StepParameterKeys.FeedMmPerMin);
        widened.Source.Should().Be(RangeSource.Plan);
        widened.Check(widened.PlanValue * 1.15).Should().Be(AdjustRefusal.None);
        widened.Check(widened.PlanValue * 1.25).Should().Be(AdjustRefusal.AboveRange);

        // 复制方案时范围一起带走。
        GrindingPlanRecord copy = await plans.CopyAsync(plan.PlanId, "副本", false, "工艺员", CancellationToken.None);
        (await plans.GetRangesAsync(copy.PlanId, CancellationToken.None)).Should().ContainSingle()
            .Which.Rule.Should().Be("±20%");
    }

    [Fact]
    public async Task Out_of_range_is_refused_and_the_record_lists_the_adjustments()
    {
        await using ServiceProvider services = await BuildAsync();
        double feed = await PlanFeedAsync(services, 2);

        // 超范围（+20%，默认 ±10%）不收：什么都不下发。
        (_, JobDownloadResult refused) = await GrindAsync(services, "J-OVER", (2, StepParameterKeys.FeedMmPerMin, feed * 1.2, false));
        refused.Succeeded.Should().BeFalse();
        refused.Violations.Should().ContainSingle().Which.Kind.Should().Be(ParameterViolationKind.OutsideAdjustRange);
        refused.Violations[0].Limit.Should().BeApproximately(feed * 1.1, 1e-6);

        // 周期进给只能调小：调大不收。
        double infeed = (await services.GetRequiredService<IJobAdjustmentService>().DescribeAsync(
            (await services.GetRequiredService<IGrindingPlanService>().CurrentAsync("R-300", CancellationToken.None))!.PlanId, CancellationToken.None))
            .Single(row => row.StepOrder == 2 && row.ParameterKey == StepParameterKeys.InfeedPerPassDiameterMicrometer).PlanValue;
        (_, JobDownloadResult deeper) = await GrindAsync(services, "J-DEEP", (2, StepParameterKeys.InfeedPerPassDiameterMicrometer, infeed + 5.0, false));
        deeper.Succeeded.Should().BeFalse();

        // 范围内收；记录列出调整（参数、方案值、本次值、人、时间）。
        (GrindingJob job, JobDownloadResult ok) = await GrindAsync(
            services, "J-1",
            (2, StepParameterKeys.FeedMmPerMin, feed * 1.05, false),
            (3, StepParameterKeys.SparkOutPassCount, 4.0, false));
        ok.Succeeded.Should().BeTrue();
        GrindingJob stored = (await services.GetRequiredService<IJobRepository>().GetAsync("J-1", CancellationToken.None))!.Value.Job;
        stored.Adjustments.Should().HaveCount(2);
        stored.Adjustments[0].Should().BeEquivalentTo(job.Adjustments[0]);
        stored.Adjustments[0].PlanValue.Should().Be(feed);
        stored.Adjustments[0].ChangedBy.Should().Be("操作工");
        string? snapshot = await services.GetRequiredService<IGrindingRecordRepository>().GetSnapshotAsync(ok.RecordId!, CancellationToken.None);
        snapshot.Should().Contain("\"adjustments\"").And.Contain(StepParameterKeys.SparkOutPassCount);

        // 下次默认回到方案值；上一次调整过的，能取回来提示"要沿用吗"。
        var adjust = services.GetRequiredService<IJobAdjustmentService>();
        string planId = stored.PlanId!;
        (await adjust.LastAdjustmentsAsync("R-300", planId, null, CancellationToken.None)).Should().HaveCount(2);
        (await adjust.LastAdjustmentsAsync("R-300", "PL-other", null, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Save_as_new_plan_takes_the_ticked_adjustments_and_records_the_job_as_source()
    {
        await using ServiceProvider services = await BuildAsync();
        var plans = services.GetRequiredService<IGrindingPlanService>();
        double feed = await PlanFeedAsync(services, 2);
        GrindingPlanRecord original = (await plans.CurrentAsync("R-300", CancellationToken.None))!;
        await plans.SavePlanAsync(
            original.PlanId, new PlanEdit(null, null) { Ranges = new[] { new PlanParamRange(3, StepParameterKeys.FeedMmPerMin, "±5%") } },
            false, "工艺员", CancellationToken.None);
        (GrindingJob job, JobDownloadResult ok) = await GrindAsync(
            services, "J-7",
            (2, StepParameterKeys.FeedMmPerMin, feed * 0.95, false),
            (3, StepParameterKeys.SparkOutPassCount, 5.0, true));
        ok.Succeeded.Should().BeTrue();

        // 只勾核对时那一项。
        PlanFromJobOutcome outcome = await plans.CreateFromJobAsync(
            "J-7", job.Adjustments.Where(item => !item.Running).ToArray(), "慢一点的粗磨", false, "工艺员", CancellationToken.None);
        outcome.ProblemKey.Should().BeNull();
        GrindingPlanRecord created = outcome.Plan!;
        created.SourceKind.Should().Be(PlanSourceKind.Job);
        created.SourceRef.Should().Be("J-7");
        created.PlanNo.Should().Be(original.PlanNo + 1);
        created.TemplateProgramId.Should().Be(original.TemplateProgramId);
        created.ProgramDiverged.Should().BeTrue();
        PlanContent content = (await plans.GetContentAsync(created.PlanId, CancellationToken.None))!;
        content.Program.Steps[1].Parameters.GetNumber(StepParameterKeys.FeedMmPerMin).Should().BeApproximately(feed * 0.95, 1e-9);
        content.Program.Steps[2].Parameters.GetNumber(StepParameterKeys.SparkOutPassCount)
            .Should().Be((await plans.GetContentAsync(original.PlanId, CancellationToken.None))!.Program.Steps[1].Parameters.GetNumber(StepParameterKeys.SparkOutPassCount),
                "运行中那一项没勾");
        (await plans.GetRangesAsync(created.PlanId, CancellationToken.None)).Should().ContainSingle().Which.Rule.Should().Be("±5%");
        (await plans.CurrentAsync("R-300", CancellationToken.None))!.PlanId.Should().Be(original.PlanId, "另存（不设当前）");

        // 名字重复不建。
        (await plans.CreateFromJobAsync("J-7", Array.Empty<JobAdjustment>(), "慢一点的粗磨", true, "工艺员", CancellationToken.None))
            .ProblemKey.Should().Be("PlanProblem_NameTaken");
    }

    [Fact]
    public async Task A_running_edit_must_stay_in_range_and_is_recorded_as_running()
    {
        await using ServiceProvider services = await BuildAsync();
        double feed = await PlanFeedAsync(services, 3);
        (_, JobDownloadResult ok) = await GrindAsync(services, "J-RUN");
        ok.Succeeded.Should().BeTrue();
        GrindingJob original = (await services.GetRequiredService<IJobRepository>().GetAsync("J-RUN", CancellationToken.None))!.Value.Job;

        GrindingJob With(double value)
        {
            GrindingJobStep[] steps = original.Steps.ToArray();
            steps[2] = steps[2] with { Parameters = steps[2].Parameters.With(StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(value)) };
            return original with { Steps = steps };
        }

        var update = services.GetRequiredService<IStepParameterUpdateService>();
        StepUpdateResult refused = await update.UpdateAsync(original, With(feed * 1.5), 0, "操作工", CancellationToken.None);
        refused.Succeeded.Should().BeFalse();
        refused.Refusal.Should().Be(StepUpdateRefusal.Invalid);
        refused.Violations.Should().ContainSingle().Which.Kind.Should().Be(ParameterViolationKind.OutsideAdjustRange);

        StepUpdateResult accepted = await update.UpdateAsync(original, With(feed * 1.08), 0, "班长", CancellationToken.None);
        accepted.Succeeded.Should().BeTrue();
        JobAdjustment recorded = (await services.GetRequiredService<IJobRepository>().GetAsync("J-RUN", CancellationToken.None))!.Value.Job
            .Adjustments.Should().ContainSingle().Subject;
        recorded.Running.Should().BeTrue();
        recorded.StepOrder.Should().Be(3);
        recorded.PlanValue.Should().Be(feed);
        recorded.JobValue.Should().BeApproximately(feed * 1.08, 1e-9);
        recorded.ChangedBy.Should().Be("班长");
    }

    [Fact]
    public async Task The_same_adjustment_three_times_in_a_row_is_suggested_for_a_new_plan()
    {
        await using ServiceProvider services = await BuildAsync();
        double feed = await PlanFeedAsync(services, 2);
        for (int i = 1; i <= 3; i++)
        {
            (_, JobDownloadResult ok) = await GrindAsync(services, "J-R" + i, (2, StepParameterKeys.FeedMmPerMin, feed * 0.95, false));
            ok.Succeeded.Should().BeTrue();
            await Task.Delay(5); // 建立时间排序用
        }

        RollOverview overview = await services.GetRequiredService<IRollPlanningService>()
            .OverviewAsync(DateTimeOffset.UnixEpoch, CancellationToken.None);
        RepeatedAdjustment repeated = overview.Repeated.Should().ContainSingle().Subject;
        repeated.RollId.Should().Be("R-300");
        repeated.StepOrder.Should().Be(2);
        repeated.ParameterKey.Should().Be(StepParameterKeys.FeedMmPerMin);
        repeated.Times.Should().Be(3);
        repeated.LastValue.Should().BeApproximately(feed * 0.95, 1e-9);
    }
}
