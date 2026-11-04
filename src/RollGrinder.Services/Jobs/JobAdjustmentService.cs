using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using RollGrinder.Data;

namespace RollGrinder.Services.Jobs;

/// <summary>可调范围从哪来（设计稿 v4 5.5.3 "来源"列）。</summary>
public enum RangeSource
{
    /// <summary>默认范围表（机床配置或内置初稿）。</summary>
    Default = 0,

    /// <summary>本方案单独设的。</summary>
    Plan = 1,
}

/// <summary>方案里一个参数在作业里能怎么调。</summary>
/// <param name="StepOrder">工序序号。</param>
/// <param name="StepTypeKey">工序类型键。</param>
/// <param name="ParameterKey">参数键。</param>
/// <param name="PlanValue">方案值（数值参数；不是数值的为 NaN）。</param>
/// <param name="Rule">可调规则。</param>
/// <param name="Source">规则来源。</param>
/// <param name="Bounds">按方案值换算的绝对范围（已与机床硬限取交集）。</param>
/// <param name="Descriptor">参数描述（单位、机床硬限）。</param>
public sealed record AdjustableParameter(
    int StepOrder,
    string StepTypeKey,
    string ParameterKey,
    double PlanValue,
    AdjustRule Rule,
    RangeSource Source,
    AdjustBounds Bounds,
    ParameterDescriptor Descriptor)
{
    public bool Adjustable => Bounds.Adjustable;

    public AdjustRefusal Check(double value) => AdjustRanges.Check(Rule, PlanValue, value, Descriptor);
}

/// <summary>一项调整被拒（超范围不收）。</summary>
public sealed record AdjustProblem(int StepOrder, string ParameterKey, AdjustRefusal Refusal, double? Limit);

/// <summary>
/// 两层参数（设计说明第 5 节）：方案里的可调范围（默认表 + 方案覆盖）与作业里的本次调整。
/// 超范围不收；每项调整记下参数、方案值、本次值、谁、何时。
/// </summary>
public interface IJobAdjustmentService
{
    /// <summary>机床配置的默认范围表（读不懂的项忽略，取内置初稿）。</summary>
    IReadOnlyDictionary<string, AdjustRule> MachineDefaults { get; }

    /// <summary>一个程序在给定方案覆盖下每道工序每个数值参数的范围。工序序号按整理成"开始 … 结束"以后的（与作业一致）。</summary>
    IReadOnlyList<AdjustableParameter> Describe(GrindingProgram program, IReadOnlyList<PlanParamRange> overrides);

    /// <summary>方案的范围（取方案的工艺与覆盖）；方案不在为空。</summary>
    Task<IReadOnlyList<AdjustableParameter>> DescribeAsync(string planId, CancellationToken cancellationToken);

    /// <summary>
    /// 核一份作业的本次调整：方案值以方案为准（不信记录里的），超范围、不可调的列出来。
    /// 没有方案（旧作业）不核。
    /// </summary>
    Task<IReadOnlyList<AdjustProblem>> ValidateAsync(GrindingJob job, CancellationToken cancellationToken);

    /// <summary>这支辊上一次作业（排除 <paramref name="exceptJobId"/>）若按同一方案并做过调整，返回那些调整；否则为空。</summary>
    Task<IReadOnlyList<JobAdjustment>> LastAdjustmentsAsync(
        string rollId, string planId, string? exceptJobId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IJobAdjustmentService"/>
public sealed class JobAdjustmentService : IJobAdjustmentService
{
    private readonly IGrindingPlanRepository plans;
    private readonly IGrindingPlanService planService;
    private readonly IJobRepository jobs;
    private readonly GrindingStepTypeRegistry stepTypes;

    public JobAdjustmentService(
        IGrindingPlanRepository plans,
        IGrindingPlanService planService,
        IJobRepository jobs,
        GrindingStepTypeRegistry stepTypes,
        MachineDescription machine)
    {
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
        this.planService = planService ?? throw new ArgumentNullException(nameof(planService));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        ArgumentNullException.ThrowIfNull(machine);
        MachineDefaults = ParseMachineDefaults(machine);
    }

    public IReadOnlyDictionary<string, AdjustRule> MachineDefaults { get; }

    /// <summary>机床配置的默认范围表：键 → 规则文本，读不懂的忽略。</summary>
    public static IReadOnlyDictionary<string, AdjustRule> ParseMachineDefaults(MachineDescription machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var map = new Dictionary<string, AdjustRule>(StringComparer.Ordinal);
        foreach ((string key, string text) in machine.AdjustRanges ?? new Dictionary<string, string>())
        {
            if (AdjustRule.Parse(text) is { } rule)
            {
                map[key] = rule;
            }
        }

        return map;
    }

    public IReadOnlyList<AdjustableParameter> Describe(GrindingProgram program, IReadOnlyList<PlanParamRange> overrides)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(overrides);
        var byKey = overrides
            .GroupBy(range => (range.StepOrder, range.ParameterKey))
            .ToDictionary(group => group.Key, group => AdjustRule.Parse(group.Last().Rule));
        var list = new List<AdjustableParameter>();

        // 序号按作业里的（整理成"开始 … 结束"以后）：作业、记录、范围说的"第 n 道"是同一道。
        foreach (GrindingJobStep step in ProgramFrame.Normalize(program.Steps, this.stepTypes))
        {
            if (!this.stepTypes.TryGet(step.StepTypeKey, out IGrindingStepType? type) || type is null)
            {
                continue;
            }

            foreach (ParameterDescriptor descriptor in type.Schema.Descriptors)
            {
                if (descriptor.Kind != ParameterValueKind.Number)
                {
                    continue;
                }

                double planValue = step.Parameters.TryGet(descriptor.Key, out ParameterValue? value) && value is { Kind: ParameterValueKind.Number }
                    ? value.Number
                    : descriptor.DefaultValue.Kind == ParameterValueKind.Number ? descriptor.DefaultValue.Number : double.NaN;
                if (double.IsNaN(planValue))
                {
                    continue;
                }

                AdjustRule? own = byKey.TryGetValue((step.Order, descriptor.Key), out AdjustRule? parsed) ? parsed : null;
                AdjustRule rule = AdjustRanges.RuleFor(descriptor.Key, own, MachineDefaults);
                list.Add(new AdjustableParameter(
                    step.Order, step.StepTypeKey, descriptor.Key, planValue, rule, own is null ? RangeSource.Default : RangeSource.Plan,
                    AdjustRanges.Resolve(rule, planValue, descriptor), descriptor));
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<AdjustableParameter>> DescribeAsync(string planId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        PlanContent? content = await this.planService.GetContentAsync(planId, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return Array.Empty<AdjustableParameter>();
        }

        IReadOnlyList<PlanParamRange> overrides = await this.plans.GetRangesAsync(planId, cancellationToken).ConfigureAwait(false);
        return Describe(content.Program, overrides);
    }

    public async Task<IReadOnlyList<AdjustProblem>> ValidateAsync(GrindingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.PlanId is not { } planId || job.Adjustments.Count == 0)
        {
            return Array.Empty<AdjustProblem>();
        }

        IReadOnlyList<AdjustableParameter> rows = await DescribeAsync(planId, cancellationToken).ConfigureAwait(false);
        return Validate(rows, job.Adjustments);
    }

    /// <summary>按范围核一串调整（同一参数改了几次以最后一次为准）。</summary>
    public static IReadOnlyList<AdjustProblem> Validate(IReadOnlyList<AdjustableParameter> rows, IEnumerable<JobAdjustment> adjustments)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(adjustments);
        var problems = new List<AdjustProblem>();
        foreach (JobAdjustment adjustment in adjustments
            .GroupBy(item => (item.StepOrder, item.ParameterKey))
            .Select(group => group.Last()))
        {
            AdjustableParameter? row = rows.FirstOrDefault(
                item => item.StepOrder == adjustment.StepOrder && item.ParameterKey == adjustment.ParameterKey);
            if (row is null)
            {
                problems.Add(new AdjustProblem(adjustment.StepOrder, adjustment.ParameterKey, AdjustRefusal.Locked, null));
                continue;
            }

            AdjustRefusal refusal = row.Check(adjustment.JobValue);
            if (refusal != AdjustRefusal.None)
            {
                problems.Add(new AdjustProblem(
                    adjustment.StepOrder, adjustment.ParameterKey, refusal,
                    refusal switch
                    {
                        AdjustRefusal.BelowRange => row.Bounds.Lower,
                        AdjustRefusal.AboveRange => row.Bounds.Upper,
                        _ => null,
                    }));
            }
        }

        return problems;
    }

    /// <summary>把调整折成参数校验失败（下发、运行中改参数拦住用）。</summary>
    public static IReadOnlyList<ParameterViolation> ToViolations(IEnumerable<AdjustProblem> problems) =>
        problems.Select(problem => new ParameterViolation(
            problem.ParameterKey,
            problem.Refusal == AdjustRefusal.Locked ? ParameterViolationKind.AdjustLocked : ParameterViolationKind.OutsideAdjustRange,
            problem.Limit)).ToArray();

    public async Task<IReadOnlyList<JobAdjustment>> LastAdjustmentsAsync(
        string rollId, string planId, string? exceptJobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        IReadOnlyList<string> ids = await this.jobs.ListJobIdsByRollAsync(rollId, 3, cancellationToken).ConfigureAwait(false);
        foreach (string id in ids)
        {
            if (id == exceptJobId)
            {
                continue;
            }

            (GrindingJob Job, Data.Model.JobState State)? last = await this.jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return last is { } found && found.Job.PlanId == planId ? found.Job.Adjustments : Array.Empty<JobAdjustment>();
        }

        return Array.Empty<JobAdjustment>();
    }
}
