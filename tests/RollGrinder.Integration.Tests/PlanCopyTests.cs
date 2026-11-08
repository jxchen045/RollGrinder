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
/// 第 2 期验收（设计说明第 10 节）：复制——从辊复制、跨辊复制方案（长度对比与套用确认）、来源标记、标准模板；
/// 长度相同直接复制；凸度类套用须确认；CVC / 点表更短截取、稍长延伸、超容差被拒；"必须采用"生效。
/// </summary>
public sealed class PlanCopyTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private static CompositeRollProfile Crown(double crown, double nominal = 1800.0) => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
        new SequentialSegment(ProfileTypeKeys.Crown, nominal - 80.0,
            new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(crown))),
        new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
    });

    private static CompositeRollProfile Cvc(double length = 1800.0) => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Cvc, length, new CvcProfileType().Schema.CreateDefaults()
            .With(CvcProfileType.A1DiameterMicrometerKey, ParameterValue.FromNumber(200.0))),
    });

    private static GrindingProgram Program(string id = "G-STD", string name = "工作辊标准") => GrindingProgram.Create(id, name, new[]
    {
        new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
        new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
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
        ServiceProvider provider = services.BuildServiceProvider();

        var library = provider.GetRequiredService<ILibraryService>();
        await library.SaveProfileAsync(RollProfileDefinition.Create("T-CROWN", "F3 标准凸度", 1800.0, Crown(300.0), DateTimeOffset.UnixEpoch), "工艺员", CancellationToken.None);
        await library.SaveProfileAsync(RollProfileDefinition.Create("T-CVC", "F2 CVC", 1800.0, Cvc(), DateTimeOffset.UnixEpoch), "工艺员", CancellationToken.None);
        await library.SaveProgramAsync(Program(), "工艺员", CancellationToken.None);
        await library.SaveProgramAsync(Program("G-SLOW", "慢拖板"), "工艺员", CancellationToken.None);
        return provider;
    }

    private static Task AddRollAsync(ServiceProvider services, string rollId, double nominal = 1800.0, string? profile = "T-CROWN", string? program = "G-STD") =>
        services.GetRequiredService<IRollRepository>().UpsertAsync(
            new RollRecord(rollId, rollId, RollGeometry.FromDiameter(nominal, 600.0), "高铬铁", DateTimeOffset.UnixEpoch)
            {
                Kind = RollKind.WorkRoll,
                CurrentDiameterMm = 600.0,
                ScrapDiameterMm = 585.0,
                TargetProfileId = profile,
                ProgramId = program,
            },
            CancellationToken.None);

    [Fact]
    public async Task Copying_a_plan_from_another_roll_of_the_same_length_is_direct_and_remembers_where_it_came_from()
    {
        await using ServiceProvider services = await BuildAsync();
        await AddRollAsync(services, "R-031");
        await AddRollAsync(services, "R-044", profile: null, program: null);
        var plans = services.GetRequiredService<IGrindingPlanService>();
        GrindingPlanRecord source = (await plans.CurrentAsync("R-031", CancellationToken.None))!;

        CopyPreview preview = await plans.PreviewCopyAsync("R-044", CopySource.FromPlan(source.PlanId), CopyScope.All, CancellationToken.None);
        preview.ProblemKey.Should().BeNull();
        preview.Alignment!.Kind.Should().Be(CopyAlignmentKind.Same);
        preview.NeedsConfirm.Should().BeFalse("长度相同直接复制");

        PlanCreateOutcome copied = await plans.CopyToRollAsync(
            "R-044", CopySource.FromPlan(source.PlanId), CopyScope.All, confirmed: false, setCurrent: true, "工艺员", CancellationToken.None);

        copied.Plan!.SourceKind.Should().Be(PlanSourceKind.OtherRollPlan);
        copied.Plan.SourceRef.Should().Be("R-031 / 1");
        copied.Plan.TemplateProfileId.Should().Be("T-CROWN", "模板来源跟着复制过去");
        copied.Plan.ProfileDiverged.Should().BeFalse();
        (await plans.CurrentAsync("R-044", CancellationToken.None))!.PlanId.Should().Be(copied.Plan.PlanId);
    }

    [Fact]
    public async Task A_crown_copied_to_a_shorter_roll_fills_the_middle_only_after_confirming()
    {
        await using ServiceProvider services = await BuildAsync();
        await AddRollAsync(services, "R-031");
        await AddRollAsync(services, "R-045", nominal: 1790.0, profile: null, program: null);
        var plans = services.GetRequiredService<IGrindingPlanService>();
        GrindingPlanRecord source = (await plans.CurrentAsync("R-031", CancellationToken.None))!;

        PlanCreateOutcome unconfirmed = await plans.CopyToRollAsync(
            "R-045", CopySource.FromPlan(source.PlanId), CopyScope.All, confirmed: false, setCurrent: false, "工艺员", CancellationToken.None);
        unconfirmed.Plan.Should().BeNull();
        unconfirmed.ProblemKey.Should().Be("PlanProblem_NeedsConfirm", "凸度类套用须确认");
        unconfirmed.Alignment!.Kind.Should().Be(CopyAlignmentKind.MiddleFilled);

        PlanCreateOutcome confirmed = await plans.CopyToRollAsync(
            "R-045", CopySource.FromPlan(source.PlanId), CopyScope.All, confirmed: true, setCurrent: false, "工艺员", CancellationToken.None);
        PlanContent content = (await plans.GetContentAsync(confirmed.Plan!.PlanId, CancellationToken.None))!;
        content.Profile.BodyLengthMm.Should().Be(1790.0);
        content.Profile.Profile.Segments.Select(s => s.LengthMm).Should().Equal(40.0, 1710.0, 40.0);
        (await services.GetRequiredService<IRollRepository>().GetAsync("R-045", CancellationToken.None))!.CurrentPlanId
            .Should().BeNull("复制出的方案默认不设为当前");
    }

    [Fact]
    public async Task A_cvc_template_is_trimmed_for_a_shorter_roll_extended_for_a_slightly_longer_one_and_refused_beyond_tolerance()
    {
        await using ServiceProvider services = await BuildAsync();
        await AddRollAsync(services, "R-S", nominal: 1790.0, profile: null, program: null);
        await AddRollAsync(services, "R-L", nominal: 1810.0, profile: null, program: null);
        await AddRollAsync(services, "R-XL", nominal: 1900.0, profile: null, program: null);
        var plans = services.GetRequiredService<IGrindingPlanService>();
        CopySource cvc = CopySource.FromTemplates("T-CVC", "G-STD");

        (await plans.PreviewCopyAsync("R-S", cvc, CopyScope.All, CancellationToken.None)).Alignment!.Kind.Should().Be(CopyAlignmentKind.Trimmed);
        (await plans.PreviewCopyAsync("R-L", cvc, CopyScope.All, CancellationToken.None)).Alignment!.Kind.Should().Be(CopyAlignmentKind.Extended);
        CopyPreview far = await plans.PreviewCopyAsync("R-XL", cvc, CopyScope.All, CancellationToken.None);
        far.ProblemKey.Should().Be("PlanProblem_Length");
        far.Alignment!.Kind.Should().Be(CopyAlignmentKind.Refused);

        PlanCreateOutcome trimmed = await plans.CreateFromTemplatesAsync("R-S", "T-CVC", "G-STD", setCurrent: true, "工艺员", CancellationToken.None);
        trimmed.Plan!.SourceKind.Should().Be(PlanSourceKind.Template);
        trimmed.Plan.TemplateProfileId.Should().Be("T-CVC");
        trimmed.Plan.TemplateProfileVersion.Should().Be(1);
        (await plans.CreateFromTemplatesAsync("R-XL", "T-CVC", "G-STD", setCurrent: true, "工艺员", CancellationToken.None))
            .ProblemKey.Should().Be("PlanProblem_Length");
    }

    [Fact]
    public async Task Copying_only_the_process_keeps_this_rolls_profile()
    {
        await using ServiceProvider services = await BuildAsync();
        await AddRollAsync(services, "R-031", program: "G-SLOW");
        await AddRollAsync(services, "R-044");
        var plans = services.GetRequiredService<IGrindingPlanService>();
        GrindingPlanRecord source = (await plans.CurrentAsync("R-031", CancellationToken.None))!;
        await plans.CurrentAsync("R-044", CancellationToken.None);

        PlanCreateOutcome copied = await plans.CopyToRollAsync(
            "R-044", CopySource.FromPlan(source.PlanId), CopyScope.ProgramOnly, confirmed: false, setCurrent: false, "工艺员", CancellationToken.None);

        PlanContent content = (await plans.GetContentAsync(copied.Plan!.PlanId, CancellationToken.None))!;
        content.Program.Name.Should().Be("慢拖板");
        content.Profile.Name.Should().Be("F3 标准凸度");
        copied.Alignment.Should().BeNull("没复制辊形，不对长度");
    }

    [Fact]
    public async Task Must_adopt_replaces_the_current_plan_of_consistent_rolls_when_they_come_to_be_ground()
    {
        await using ServiceProvider services = await BuildAsync();
        await AddRollAsync(services, "R-1");
        await AddRollAsync(services, "R-2");
        await AddRollAsync(services, "R-SPECIAL");
        var plans = services.GetRequiredService<IGrindingPlanService>();
        var templates = services.GetRequiredService<ITemplateService>();
        GrindingPlanRecord r1 = (await plans.CurrentAsync("R-1", CancellationToken.None))!;
        await plans.CurrentAsync("R-2", CancellationToken.None);
        GrindingPlanRecord special = (await plans.CurrentAsync("R-SPECIAL", CancellationToken.None))!;

        // R-SPECIAL 的方案是特批：复制以后改过。
        PlanContent specialContent = (await plans.GetContentAsync(special.PlanId, CancellationToken.None))!;
        await plans.SaveProfileAsync(special.PlanId, specialContent.Profile with { Profile = Crown(320.0) }, true, "工艺员", CancellationToken.None);

        TemplateUsage before = await templates.UsageAsync(TemplateKind.Profile, "T-CROWN", CancellationToken.None);
        before.Consistent.Should().Be(2);
        before.Diverged.Should().Be(1);

        // 工艺员改模板（凸度 300 → 280，第 2 版），保存并必须采用；默认：一致的强制，已改动的普通提示。
        var library = services.GetRequiredService<ILibraryService>();
        RollProfileDefinition template = (await services.GetRequiredService<IRollProfileRepository>().GetAsync("T-CROWN", CancellationToken.None))!;
        await library.SaveProfileAsync(template with { Profile = Crown(280.0) }, "工艺员", CancellationToken.None);
        IReadOnlyList<TemplateCopyRow> rows = await templates.CopiedRollsAsync(TemplateKind.Profile, "T-CROWN", CancellationToken.None);
        rows.Select(r => (r.Roll.RollId, r.Diverged)).Should().BeEquivalentTo(new[] { ("R-1", false), ("R-2", false), ("R-SPECIAL", true) });
        TemplateMandate mandate = await templates.CreateMandateAsync(
            TemplateKind.Profile, "T-CROWN", new Dictionary<string, MandateDecision> { ["R-2"] = MandateDecision.None }, "工艺员", CancellationToken.None);
        mandate.TemplateVersion.Should().Be(2);

        // R-1 来磨：核对页直接换成从新模板复制的方案，原方案留作历史。
        IReadOnlyList<TemplateUpdateNotice> notices = await templates.ApplyForJobAsync("R-1", "操作工", CancellationToken.None);
        notices.Should().ContainSingle().Which.Should().Match<TemplateUpdateNotice>(n => n.Forced && n.OldPlanNo == 1 && n.NewPlanNo == 2 && n.ProblemKey == null);
        GrindingPlanRecord replaced = (await plans.CurrentAsync("R-1", CancellationToken.None))!;
        replaced.PlanNo.Should().Be(2);
        replaced.TemplateProfileVersion.Should().Be(2);
        PlanContent replacedContent = (await plans.GetContentAsync(replaced.PlanId, CancellationToken.None))!;
        replacedContent.Profile.Profile.Segments[1].Parameters.Get(CrownProfileType.CrownDiameterMicrometerKey).Number.Should().Be(280.0);
        (await plans.ListAsync("R-1", CancellationToken.None)).Select(p => p.Plan.PlanId).Should().Contain(r1.PlanId, "原方案不抹掉");
        (await templates.ApplyForJobAsync("R-1", "操作工", CancellationToken.None)).Should().BeEmpty("只换一次");

        // R-2 工艺员选了不提示；R-SPECIAL 已改动：只提示，不换。
        (await templates.ApplyForJobAsync("R-2", "操作工", CancellationToken.None)).Should().BeEmpty();
        TemplateUpdateNotice notice = (await templates.ApplyForJobAsync("R-SPECIAL", "操作工", CancellationToken.None)).Single();
        notice.Forced.Should().BeFalse();
        GrindingPlanRecord stillSpecial = (await plans.CurrentAsync("R-SPECIAL", CancellationToken.None))!;
        stillSpecial.PlanId.Should().Be(special.PlanId, "特批方案不被强制换掉");
        stillSpecial.ProfileDiverged.Should().BeTrue("没用过的方案就地改，记为已改动");

        TemplateUsage progress = await templates.UsageAsync(TemplateKind.Profile, "T-CROWN", CancellationToken.None);
        progress.MandateTotal.Should().Be(1);
        progress.MandateApplied.Should().Be(1);
    }

    [Fact]
    public async Task A_revoked_must_adopt_no_longer_replaces_plans()
    {
        await using ServiceProvider services = await BuildAsync();
        await AddRollAsync(services, "R-1");
        var plans = services.GetRequiredService<IGrindingPlanService>();
        var templates = services.GetRequiredService<ITemplateService>();
        await plans.CurrentAsync("R-1", CancellationToken.None);
        RollProfileDefinition template = (await services.GetRequiredService<IRollProfileRepository>().GetAsync("T-CROWN", CancellationToken.None))!;
        await services.GetRequiredService<ILibraryService>().SaveProfileAsync(template with { Profile = Crown(280.0) }, "工艺员", CancellationToken.None);
        TemplateMandate mandate = await templates.CreateMandateAsync(
            TemplateKind.Profile, "T-CROWN", new Dictionary<string, MandateDecision>(), "工艺员", CancellationToken.None);

        await templates.RevokeMandateAsync(mandate.MandateId, CancellationToken.None);

        // 撤回以后不再强制，只剩普通的"标准已更新"提示。
        (await templates.ApplyForJobAsync("R-1", "操作工", CancellationToken.None)).Should().ContainSingle()
            .Which.Should().Match<TemplateUpdateNotice>(n => !n.Forced && n.CanAdopt);
        (await plans.CurrentAsync("R-1", CancellationToken.None))!.PlanNo.Should().Be(1);
    }

    [Fact]
    public async Task An_updated_template_is_offered_once_and_one_key_adopts_it()
    {
        await using ServiceProvider services = await BuildAsync();
        await AddRollAsync(services, "R-1");
        await AddRollAsync(services, "R-2");
        var plans = services.GetRequiredService<IGrindingPlanService>();
        var templates = services.GetRequiredService<ITemplateService>();
        GrindingPlanRecord before = (await plans.CurrentAsync("R-1", CancellationToken.None))!;
        await plans.CurrentAsync("R-2", CancellationToken.None);
        (await templates.ApplyForJobAsync("R-1", "操作工", CancellationToken.None)).Should().BeEmpty("模板没改过");

        // 工艺员只是保存了模板（没下"必须采用"）：来磨时核对页提示"标准已更新"（设计说明 4.6）。
        RollProfileDefinition template = (await services.GetRequiredService<IRollProfileRepository>().GetAsync("T-CROWN", CancellationToken.None))!;
        await services.GetRequiredService<ILibraryService>().SaveProfileAsync(template with { Profile = Crown(280.0) }, "工艺员", CancellationToken.None);
        TemplateUpdateNotice notice = (await templates.ApplyForJobAsync("R-1", "操作工", CancellationToken.None)).Single();
        notice.Forced.Should().BeFalse();
        notice.CanAdopt.Should().BeTrue();
        notice.TemplateVersion.Should().Be(2);
        notice.PlanId.Should().Be(before.PlanId);

        // 第 6 格"改用新模板…"：从新模板复制出新方案设为当前，原方案留作历史。
        PlanCreateOutcome adopted = await templates.AdoptAsync("R-1", new[] { notice }, "操作工", CancellationToken.None);
        adopted.Plan!.PlanNo.Should().Be(before.PlanNo + 1);
        GrindingPlanRecord now = (await plans.CurrentAsync("R-1", CancellationToken.None))!;
        now.PlanId.Should().Be(adopted.Plan.PlanId);
        now.TemplateProfileVersion.Should().Be(2);
        (await templates.ApplyForJobAsync("R-1", "操作工", CancellationToken.None)).Should().BeEmpty("已经是新版");

        // R-2 这次没改用：同一版只提示一次。
        IReadOnlyList<TemplateUpdateNotice> offered = await templates.ApplyForJobAsync("R-2", "操作工", CancellationToken.None);
        offered.Should().ContainSingle();
        await templates.DeclineAsync(offered, CancellationToken.None);
        (await templates.ApplyForJobAsync("R-2", "操作工", CancellationToken.None)).Should().BeEmpty("提示过一次，这支辊没改用");
    }
}
