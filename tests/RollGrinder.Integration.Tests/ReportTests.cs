using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 磨前 / 磨后报表。
///
/// 报表里没有排版，只有"报表上有哪些数、数是多少"——所以能在这里逐项核对。
/// 字面全是资源键，两种语言各打一张，所以键必须真实存在。
/// </summary>
public sealed class ReportTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private const string RecordId = "REC-1";
    private const string JobId = "J-1";
    private const string RollId = "R-1";

    private static readonly DateTimeOffset Started = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

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

    private static GrindingJob CreateJob() => GrindingJob.Create(
        JobId,
        RollId,
        RollGeometry.FromDiameter(2000.0, 650.0),
        ProfileTypeKeys.Cylindrical,
        new CylindricalProfileType().Schema.CreateDefaults(),
        new[]
        {
            new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
            new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
        }) with
    {
        ProfileName = "标准平辊",
    };

    /// <summary>铺一条已完工的记录：辊件、作业、记录，可选带一次测量。</summary>
    private static async Task SeedAsync(ServiceProvider services, bool withMeasurement)
    {
        GrindingJob job = CreateJob();

        await services.GetRequiredService<IRollRepository>().UpsertAsync(
            new RollRecord(RollId, "WR-0042", job.Geometry, "9Cr2Mo", Started), CancellationToken.None);
        await services.GetRequiredService<IJobRepository>().SaveAsync(
            job, JobState.Completed, CancellationToken.None);

        IGrindingRecordRepository records = services.GetRequiredService<IGrindingRecordRepository>();
        await records.AddAsync(
            new GrindingRecord(RecordId, JobId, Started, null, JobState.Handed, null), CancellationToken.None);
        await records.FinishAsync(
            RecordId, Started.AddHours(3.0), JobState.Completed, "试磨", CancellationToken.None);

        if (!withMeasurement)
        {
            return;
        }

        // 实测比目标半径大 5 µm（半径量）：磨后报表上应当看得出这个偏差。
        var points = Enumerable.Range(0, 11)
            .Select(i => new MeasurementPoint(
                i * job.Geometry.BodyLengthMm / 10.0, job.Geometry.NominalRadiusMm + 0.005))
            .ToArray();

        await services.GetRequiredService<IMeasurementService>()
            .SaveAsync(JobId, points, "gauge", CancellationToken.None);
    }

    private static IReadOnlySet<string> ResourceKeys()
    {
        string path = Path.Combine(
            RepositoryLayout.Root, "src", "RollGrinder.App", "Resources", "Strings.resx");
        return XDocument.Load(path).Root!
            .Elements("data")
            .Select(element => element.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_record_that_cannot_be_traced_produces_no_report()
    {
        // 记录在、作业不在：打出来是半张纸，不如明说。
        await using ServiceProvider services = await BuildAsync();

        GrindingReport? report = await services.GetRequiredService<IReportService>()
            .BuildAsync("no-such-record", ReportKind.PostGrind, CancellationToken.None);

        report.Should().BeNull();
    }

    [Fact]
    public async Task The_pre_grind_sheet_says_how_the_roll_is_going_to_be_ground()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, withMeasurement: false);

        GrindingReport report = (await services.GetRequiredService<IReportService>()
            .BuildAsync(RecordId, ReportKind.PreGrind, CancellationToken.None))!;

        report.Kind.Should().Be(ReportKind.PreGrind);
        Field(report, "Report_RollCode").Should().Be("WR-0042");
        Field(report, "Report_Material").Should().Be("9Cr2Mo");

        // 辊形记的是调出来时的名字：库里改了名，这支辊的记录不跟着变。
        Field(report, "Report_Profile").Should().Be("标准平辊");

        // 磨前还没有结果可言，所以没有结果表，曲线画的是目标辊形。
        report.Tables.Should().ContainSingle();
        report.Tables[0].TitleResourceKey.Should().Be("Report_StepsTable");
        report.CurveTitleResourceKey.Should().Be("Report_CurveTarget");
        report.CurvePoints.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_post_grind_report_says_how_the_roll_actually_came_out()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, withMeasurement: true);

        GrindingReport report = (await services.GetRequiredService<IReportService>()
            .BuildAsync(RecordId, ReportKind.PostGrind, CancellationToken.None))!;

        Field(report, "Report_Duration").Should().Be("180", "开始到结束是 3 小时");
        Field(report, "Report_Note").Should().Be("试磨");

        ReportTable results = report.Tables.Single(table => table.TitleResourceKey == "Report_ResultsTable");

        // 实测比目标大 5 µm（半径量）⇒ 直径量 10 µm。
        Row(results, "Report_WorstDeviation").Should().Be("10.00");
        Row(results, "Report_DeviationRms").Should().Be("10.00");
        Row(results, "Report_MeasurementSource").Should().Be("gauge");

        report.CurveTitleResourceKey.Should().Be("Report_CurveDeviation");
        report.CurvePoints.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_report_without_a_measurement_leaves_the_results_blank()
    {
        // 报表上留白比填一个算不出来的数强。
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, withMeasurement: false);

        GrindingReport report = (await services.GetRequiredService<IReportService>()
            .BuildAsync(RecordId, ReportKind.PostGrind, CancellationToken.None))!;

        report.Tables.Single(table => table.TitleResourceKey == "Report_ResultsTable")
            .Rows.Should().BeEmpty();
        report.CurvePoints.Should().BeEmpty("没有实测就没有偏差曲线");
    }

    [Fact]
    public async Task The_steps_table_lists_every_step_of_the_job()
    {
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, withMeasurement: false);

        GrindingReport report = (await services.GetRequiredService<IReportService>()
            .BuildAsync(RecordId, ReportKind.PreGrind, CancellationToken.None))!;

        ReportTable steps = report.Tables.Single(table => table.TitleResourceKey == "Report_StepsTable");

        steps.Rows.Should().HaveCount(2);
        steps.Rows.Select(row => row[1]).Should().Equal("StepType_Rough", "StepType_Finish");
        steps.Rows.Should().OnlyContain(row => row.Count == steps.ColumnHeaderResourceKeys.Count);
    }

    [Fact]
    public async Task Every_word_on_the_report_comes_from_the_resources()
    {
        // 报表跟着界面语言走（架构约束 ⑪）：键少一个，纸上就是 "!Key!"。
        await using ServiceProvider services = await BuildAsync();
        await SeedAsync(services, withMeasurement: true);

        IReadOnlySet<string> keys = ResourceKeys();
        IReportService reports = services.GetRequiredService<IReportService>();

        foreach (ReportKind kind in new[] { ReportKind.PreGrind, ReportKind.PostGrind })
        {
            GrindingReport report = (await reports.BuildAsync(RecordId, kind, CancellationToken.None))!;

            keys.Should().Contain(report.TitleResourceKey);
            keys.Should().Contain(report.CurveTitleResourceKey);
            keys.Should().Contain(report.Header.Select(field => field.LabelResourceKey));

            foreach (ReportTable table in report.Tables)
            {
                keys.Should().Contain(table.TitleResourceKey);

                // 名目列放的是资源键：列头为空的两列表在第 0 列，多列表在工序名那一列。
                IEnumerable<string> labels = table.ColumnHeaderResourceKeys.Count == 0
                    ? table.Rows.Select(row => row[0])
                    : table.ColumnHeaderResourceKeys.Concat(table.Rows.Select(row => row[1]));

                foreach (string label in labels)
                {
                    keys.Should().Contain(label, $"报表上的 {label} 需要界面文案");
                }
            }
        }
    }

    private static string Field(GrindingReport report, string labelResourceKey) =>
        report.Header.Single(field => field.LabelResourceKey == labelResourceKey).Value;

    private static string Row(ReportTable table, string labelResourceKey) =>
        table.Rows.Single(row => row[0] == labelResourceKey)[1];
}
