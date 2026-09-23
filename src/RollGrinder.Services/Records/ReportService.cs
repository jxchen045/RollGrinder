using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Records;

/// <summary>磨前 / 磨后报表的内容组装。</summary>
public interface IReportService
{
    /// <summary>
    /// 按一条磨削记录出一张报表。记录、作业与辊件都从库里取——
    /// 报表说的是"这支辊按什么磨的、磨成了什么样"，全是已经落库的事实。
    /// </summary>
    /// <returns>记录不存在时返回 null。</returns>
    Task<GrindingReport?> BuildAsync(string recordId, ReportKind kind, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReportService"/>
public sealed class ReportService : IReportService
{
    private readonly IGrindingRecordRepository records;
    private readonly IJobRepository jobs;
    private readonly IRollRepository rolls;
    private readonly IMeasurementRepository measurements;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly HmiSettings settings;
    private readonly TimeProvider timeProvider;

    public ReportService(
        IGrindingRecordRepository records,
        IJobRepository jobs,
        IRollRepository rolls,
        IMeasurementRepository measurements,
        GrindingStepTypeRegistry stepTypes,
        RollProfileTypeRegistry profileTypes,
        HmiSettings settings,
        TimeProvider timeProvider)
    {
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.measurements = measurements ?? throw new ArgumentNullException(nameof(measurements));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GrindingReport?> BuildAsync(
        string recordId, ReportKind kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        GrindingRecord? record = await this.records.GetAsync(recordId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        (GrindingJob Job, JobState State)? stored = await this.jobs
            .GetAsync(record.JobId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        GrindingJob job = stored.Value.Job;
        RollRecord? roll = await this.rolls.GetAsync(job.RollId, cancellationToken).ConfigureAwait(false);
        MeasurementRecord? measurement = await this.measurements
            .GetLatestByJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);

        var tables = new List<ReportTable> { Steps(job) };
        RollProfile target = job.Profile.Compose(job.Geometry, this.profileTypes, this.settings.ProfileSampleCount);

        IReadOnlyList<(double, double)> curve;
        string curveTitle;
        if (kind == ReportKind.PreGrind)
        {
            // 磨前打的是"准备怎么磨"：目标辊形。这时候还没有实测可谈。
            curve = target.Points
                .Select(point => (
                    point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
                .ToArray();
            curveTitle = "Report_CurveTarget";
        }
        else
        {
            // 磨后打的是"磨成了什么样"：实测相对目标的偏差，外加几个结果指标。
            tables.Add(Results(job, measurement, target));
            curve = measurement is null
                ? Array.Empty<(double, double)>()
                : CompensationCalculator.ComputeDeviation(measurement.Profile, target, job.Geometry).Points
                    .Select(point => (
                        point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
                    .ToArray();
            curveTitle = "Report_CurveDeviation";
        }

        return new GrindingReport(
            kind,
            kind == ReportKind.PreGrind ? "Report_TitlePreGrind" : "Report_TitlePostGrind",
            this.timeProvider.GetUtcNow(),
            Header(record, job, roll, kind),
            tables,
            curve,
            curveTitle);
    }

    private static IReadOnlyList<ReportField> Header(
        GrindingRecord record, GrindingJob job, RollRecord? roll, ReportKind kind)
    {
        var fields = new List<ReportField>
        {
            new("Report_RollCode", roll?.Code ?? job.RollId),
            new("Report_JobId", job.JobId),
            new("Report_Material", roll?.Material ?? string.Empty),
            new("Report_BodyLength", Number(job.Geometry.BodyLengthMm, "F1")),
            new("Report_NominalDiameter", Number(job.Geometry.NominalDiameterMm, "F3")),

            // 辊形与程序记的是**调出来时的名字**：库里之后改了名，这支辊的记录不跟着变。
            job.ProfileName is { Length: > 0 } profileName
                ? new("Report_Profile", profileName)
                : new("Report_Profile", "ProfileType_" + job.ProfileTypeKey, ValueIsResourceKey: true),
            new("Report_Program", job.ProgramName ?? string.Empty),
            new("Report_StartedAt", Instant(record.StartedAtUtc)),
        };

        if (kind == ReportKind.PostGrind)
        {
            fields.Add(new ReportField("Report_FinishedAt", record.FinishedAtUtc is null
                ? string.Empty
                : Instant(record.FinishedAtUtc.Value)));
            fields.Add(new ReportField("Report_Duration", record.FinishedAtUtc is null
                ? string.Empty
                : Number((record.FinishedAtUtc.Value - record.StartedAtUtc).TotalMinutes, "F0")));
            // 状态给资源键，由排版时按界面语言取字——报表上不能出现 "Handed" 这种枚举名。
            fields.Add(new ReportField("Report_State", "JobState_" + record.State, ValueIsResourceKey: true));
        }

        if (!string.IsNullOrWhiteSpace(record.Note))
        {
            fields.Add(new ReportField("Report_Note", record.Note));
        }

        return fields;
    }

    /// <summary>工序清单：这支辊是按哪几道、什么参数磨的。</summary>
    private ReportTable Steps(GrindingJob job)
    {
        var rows = new List<IReadOnlyList<string>>(job.Steps.Count);
        foreach (GrindingJobStep step in job.Steps)
        {
            GrindingStepPlan plan = this.stepTypes.Get(step.StepTypeKey)
                .CreatePlan(job.Geometry, step.Parameters);

            rows.Add(new[]
            {
                step.Order.ToString("00", CultureInfo.InvariantCulture),

                // 工序名是资源键，界面层按当前语言取字（架构约束 ⑪）。
                "StepType_" + step.StepTypeKey,
                plan.PassCount.ToString(CultureInfo.InvariantCulture),

                // 切深与余量对外一律直径量 µm，与界面上看到的数一致。
                Number(UnitConversion.RadiusMmToDiameterMicrometer(plan.InfeedPerPassRadiusMm), "F1"),
                Number(plan.FeedMmPerMin, "F0"),
                Number(plan.WorkpieceSpeedRpm, "F1"),
            });
        }

        return new ReportTable(
            "Report_StepsTable",
            new[]
            {
                "Report_StepOrder", "Report_StepType", "Report_PassCount",
                "Report_InfeedPerPass", "Report_Feed", "Report_WorkpieceSpeed",
            },
            rows);
    }

    /// <summary>
    /// 结果指标。没有测量就照实说没有——报表上留白比填一个算不出来的数强。
    /// </summary>
    private static ReportTable Results(GrindingJob job, MeasurementRecord? measurement, RollProfile target)
    {
        var rows = new List<IReadOnlyList<string>>();

        if (measurement is null)
        {
            return new ReportTable("Report_ResultsTable", Array.Empty<string>(), rows);
        }

        RollProfile deviation = CompensationCalculator.ComputeDeviation(measurement.Profile, target, job.Geometry);
        double[] micrometres = deviation.Points
            .Select(point => UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm))
            .ToArray();

        double worst = micrometres.Length == 0
            ? 0.0
            : micrometres.Max(value => Math.Abs(value));
        double rms = micrometres.Length == 0
            ? 0.0
            : Math.Sqrt(micrometres.Sum(value => value * value) / micrometres.Length);

        rows.Add(new[] { "Report_MeasuredAt", Instant(measurement.RecordedAtUtc) });
        rows.Add(new[] { "Report_MeasurementSource", measurement.Source });
        rows.Add(new[] { "Report_PointCount", measurement.Profile.Points.Count.ToString(CultureInfo.InvariantCulture) });
        rows.Add(new[] { "Report_WorstDeviation", Number(worst, "F2") });
        rows.Add(new[] { "Report_DeviationRms", Number(rms, "F2") });

        return new ReportTable("Report_ResultsTable", Array.Empty<string>(), rows);
    }

    /// <summary>
    /// 数字一律按不变文化格式化：报表上的小数点与界面语言无关，
    /// 换台机器、换个语言打出来的同一张报表，数得是同一个数。
    /// </summary>
    private static string Number(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
