using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Nc;
using RollGrinder.Services.Alarms;

namespace RollGrinder.Services.Jobs;

/// <summary>改参数被挡下来的原因。</summary>
public enum StepUpdateRefusal
{
    /// <summary>没被挡。</summary>
    None = 0,

    /// <summary>什么都没改。</summary>
    NothingChanged = 1,

    /// <summary>改的不只是参数（工序序列、辊形或几何变了）——那得重新下发整支作业。</summary>
    NotJustParameters = 2,

    /// <summary>改到了已经磨完的工序上。</summary>
    StepAlreadyDone = 3,

    /// <summary>在正在跑的那一道上改了不允许在线调整的参数。</summary>
    NotLiveEditable = 4,

    /// <summary>改完之后作业校验不过。</summary>
    Invalid = 5,

    /// <summary>上一次修改 NC 还没采用（接口约定第 3 节）：生效前不收新的修改。</summary>
    PreviousEditPending = 6,

    /// <summary>暂存区一次只装一道工序：同时改了几道工序，要分开改。</summary>
    OneStepAtATime = 7,
}

/// <summary>改参数的结果。</summary>
/// <param name="Succeeded">是否已写到机床。</param>
/// <param name="Refusal">被挡下来的原因。</param>
/// <param name="Violations">校验失败项（<see cref="StepUpdateRefusal.Invalid"/> 时有值）。</param>
/// <param name="BlockedParameterKeys">不允许在线调整、却被改动了的参数键。</param>
/// <param name="ChangedStepOrders">实际写下去的工序序号。</param>
/// <param name="WriteCount">写入的变量条数。</param>
/// <param name="EditSequence">
/// 走暂存区握手时这次的改参数序号；NC 回报的已采用序号追上它，才算生效。直接改（老 tagmap）为空。
/// </param>
public sealed record StepUpdateResult(
    bool Succeeded,
    StepUpdateRefusal Refusal,
    IReadOnlyList<ParameterViolation> Violations,
    IReadOnlyList<string> BlockedParameterKeys,
    IReadOnlyList<int> ChangedStepOrders,
    int WriteCount,
    int? EditSequence = null)
{
    internal static StepUpdateResult Refused(
        StepUpdateRefusal refusal,
        IReadOnlyList<ParameterViolation>? violations = null,
        IReadOnlyList<string>? blockedKeys = null) =>
        new(
            false,
            refusal,
            violations ?? Array.Empty<ParameterViolation>(),
            blockedKeys ?? Array.Empty<string>(),
            Array.Empty<int>(),
            0);
}

/// <summary>
/// 磨削进行当中改工艺参数。
///
/// **这不是一条实时通道。** 改参数 = 把那一道工序的 R 参数重写一遍，
/// NC 在下一道次读取；上位机写完就脱手，被强制结束时 NC 拿最后收到的值
/// 把这支辊磨完（最高原则）。握手标志不重新脉冲——作业的身份没变，
/// 再脉冲一次会让 NC 以为来了一份新作业。
/// </summary>
public interface IStepParameterUpdateService
{
    /// <summary>
    /// 校验并下发改动过的工序参数。
    /// </summary>
    /// <param name="original">改之前的作业（库里那一份）。</param>
    /// <param name="edited">改之后的作业。只允许参数值不同。</param>
    /// <param name="currentStepOrder">机床正在跑第几道；0 表示还没开始跑。</param>
    /// <param name="changedBy">改动人，记进报警条目备查。</param>
    Task<StepUpdateResult> UpdateAsync(
        GrindingJob original,
        GrindingJob edited,
        int currentStepOrder,
        string changedBy,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IStepParameterUpdateService"/>
public sealed class StepParameterUpdateService : IStepParameterUpdateService
{
    /// <summary>改参数已下发的报警（提示级）资源键。</summary>
    public const string ParametersUpdatedResourceKey = "Alarm_StepParametersUpdated";

    /// <summary>已下发但记录没落库的资源键。</summary>
    public const string UpdateNotArchivedResourceKey = "Alarm_StepUpdateNotArchived";

    private readonly IMachineGateway gateway;
    private readonly GrindingJobValidator validator;
    private readonly NcJobTranslator translator;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly MachineCapability capability;
    private readonly IJobRepository jobs;
    private readonly IAlarmSink alarms;
    private readonly TimeProvider timeProvider;
    private readonly IJobAdjustmentService? adjustments;

    public StepParameterUpdateService(
        IMachineGateway gateway,
        GrindingJobValidator validator,
        NcJobTranslator translator,
        GrindingStepTypeRegistry stepTypes,
        MachineCapability capability,
        IJobRepository jobs,
        IAlarmSink alarms,
        TimeProvider timeProvider,
        IJobAdjustmentService? adjustments = null)
    {
        this.adjustments = adjustments;
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.translator = translator ?? throw new ArgumentNullException(nameof(translator));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.capability = capability ?? throw new ArgumentNullException(nameof(capability));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<StepUpdateResult> UpdateAsync(
        GrindingJob original,
        GrindingJob edited,
        int currentStepOrder,
        string changedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);

        // 只认参数值的改动。工序序列、辊形、几何变了就不是"改参数"，
        // 那得停下来重新下发整支作业。
        if (!SameShape(original, edited))
        {
            return StepUpdateResult.Refused(StepUpdateRefusal.NotJustParameters);
        }

        var changedOrders = new List<int>();
        var blockedKeys = new List<string>();
        var changedValues = new List<(int Order, string Key)>();

        for (int i = 0; i < edited.Steps.Count; i++)
        {
            IReadOnlyList<string> changedKeys = ChangedKeys(original.Steps[i].Parameters, edited.Steps[i].Parameters);
            if (changedKeys.Count == 0)
            {
                continue;
            }

            int order = edited.Steps[i].Order;

            // 已经磨完的工序改了也没用，而且会让记录对不上实际磨的东西。
            if (order < currentStepOrder)
            {
                return StepUpdateResult.Refused(StepUpdateRefusal.StepAlreadyDone);
            }

            // 正在跑的那一道：只许改标了可在线调整的参数。还没轮到的工序怎么改都行。
            if (order == currentStepOrder)
            {
                ParameterSchema schema = this.stepTypes.Get(edited.Steps[i].StepTypeKey).Schema;
                blockedKeys.AddRange(changedKeys.Where(key => !IsLiveEditable(schema, key)));
            }

            changedOrders.Add(order);
            changedValues.AddRange(changedKeys.Select(key => (order, key)));
        }

        if (blockedKeys.Count > 0)
        {
            return StepUpdateResult.Refused(StepUpdateRefusal.NotLiveEditable, blockedKeys: blockedKeys);
        }

        if (changedOrders.Count == 0)
        {
            return StepUpdateResult.Refused(StepUpdateRefusal.NothingChanged);
        }

        // 改完之后整支作业还得站得住：不然有人在磨削当中把拖板速度打成 5000。
        ParameterValidationResult validation = this.validator.Validate(edited, this.capability);
        if (!validation.IsValid)
        {
            return StepUpdateResult.Refused(StepUpdateRefusal.Invalid, validation.Violations);
        }

        DateTimeOffset now = this.timeProvider.GetUtcNow();

        // 按方案磨的：运行中改也在方案范围以内，并记成本次调整（设计说明 5.4：运行中改的也算）。
        if (this.adjustments is not null && edited.PlanId is { } planId)
        {
            IReadOnlyList<AdjustableParameter> rows = await this.adjustments.DescribeAsync(planId, cancellationToken).ConfigureAwait(false);
            var added = new List<JobAdjustment>();
            foreach ((int order, string key) in changedValues)
            {
                AdjustableParameter? row = rows.FirstOrDefault(item => item.StepOrder == order && item.ParameterKey == key);
                GrindingJobStep step = edited.Steps.First(item => item.Order == order);
                double value = step.Parameters.TryGet(key, out ParameterValue? raw) && raw is { Kind: ParameterValueKind.Number }
                    ? raw.Number
                    : double.NaN;
                added.Add(new JobAdjustment(order, key, row?.PlanValue ?? double.NaN, value, true, changedBy ?? string.Empty, now));
            }

            IReadOnlyList<AdjustProblem> problems = JobAdjustmentService.Validate(rows, added);
            if (problems.Count > 0)
            {
                return StepUpdateResult.Refused(StepUpdateRefusal.Invalid, JobAdjustmentService.ToViolations(problems));
            }

            edited = edited with { Adjustments = original.Adjustments.Concat(added).ToArray() };
        }

        var writes = new List<TagWrite>();
        int? editSequence = null;
        if (this.translator.SupportsStagedEdits)
        {
            // 暂存区 + 序号（接口约定第 3 节）：NC 在道次开始时整组采用，不会读到半组。
            if (changedOrders.Count > 1)
            {
                return StepUpdateResult.Refused(StepUpdateRefusal.OneStepAtATime);
            }

            MachineStateSnapshot handshake = await this.gateway.ReadStateAsync(
                new[] { MachineTagKeys.JobEditSeq, MachineTagKeys.JobEditAppliedSeq }, cancellationToken)
                .ConfigureAwait(false);
            int sent = (int)(handshake.GetNumberOrNull(MachineTagKeys.JobEditSeq) ?? 0.0);
            int applied = (int)(handshake.GetNumberOrNull(MachineTagKeys.JobEditAppliedSeq) ?? 0.0);

            // 上一次还在暂存区里没被采用：现在改写暂存区，NC 可能正拷到一半。
            if (sent != applied)
            {
                return StepUpdateResult.Refused(StepUpdateRefusal.PreviousEditPending);
            }

            editSequence = sent is < 0 or int.MaxValue ? 1 : sent + 1;
            writes.AddRange(this.translator.TranslateStepEdit(edited, changedOrders[0], editSequence.Value, now));
        }
        else
        {
            // 老 tagmap 没有暂存区：直接改那一道的 R 参数（不是原子的，诊断页写明）。
            foreach (int order in changedOrders)
            {
                writes.AddRange(this.translator.TranslateStepParameters(edited, order, now));
            }
        }

        // 先写机床：写不进去就不该在库里留下改过的样子。
        await this.gateway.WriteTagsAsync(writes, cancellationToken).ConfigureAwait(false);

        try
        {
            // 作业存的是"这支辊实际是按什么磨的"，所以改了就要更新。
            // 程序库里那支模板不动——那是模板，不是这一次的记录。
            await this.jobs.SaveAsync(edited, JobState.Handed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 新参数已经在 NC 手里，这支辊按新值磨；只是记录没更新，必须让人看见。
            this.alarms.Raise(AlarmSeverity.Error, UpdateNotArchivedResourceKey, ex.Message, AlarmCodes.HandoverNotArchived);
            return new StepUpdateResult(
                true, StepUpdateRefusal.None, Array.Empty<ParameterViolation>(), Array.Empty<string>(),
                changedOrders, writes.Count, editSequence);
        }

        this.alarms.Raise(
            AlarmSeverity.Information,
            ParametersUpdatedResourceKey,
            $"{changedBy}: {string.Join(", ", changedOrders)}",
            AlarmCodes.StepParametersUpdated);

        return new StepUpdateResult(
            true, StepUpdateRefusal.None, Array.Empty<ParameterViolation>(), Array.Empty<string>(),
            changedOrders, writes.Count, editSequence);
    }

    private static bool IsLiveEditable(ParameterSchema schema, string key) =>
        schema.Descriptors.Any(descriptor =>
            string.Equals(descriptor.Key, key, StringComparison.Ordinal) && descriptor.IsLiveEditable);

    /// <summary>除了参数值，两份作业的其余部分必须一模一样。</summary>
    private static bool SameShape(GrindingJob original, GrindingJob edited)
    {
        if (!string.Equals(original.JobId, edited.JobId, StringComparison.Ordinal)
            || original.Steps.Count != edited.Steps.Count
            || original.Geometry != edited.Geometry
            || original.Profile != edited.Profile
            || !original.ProgramOptions.Equals(edited.ProgramOptions))
        {
            return false;
        }

        for (int i = 0; i < original.Steps.Count; i++)
        {
            if (original.Steps[i].Order != edited.Steps[i].Order
                || !string.Equals(original.Steps[i].StepTypeKey, edited.Steps[i].StepTypeKey, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>两组参数里取值不同的键。</summary>
    private static IReadOnlyList<string> ChangedKeys(ParameterSet original, ParameterSet edited)
    {
        var changed = new List<string>();

        foreach (KeyValuePair<string, ParameterValue> pair in edited.ToOrderedPairs())
        {
            if (!original.TryGet(pair.Key, out ParameterValue? before)
                || before is null
                || before.Kind != pair.Value.Kind
                || !string.Equals(before.ToInvariantString(), pair.Value.ToInvariantString(), StringComparison.Ordinal))
            {
                changed.Add(pair.Key);
            }
        }

        return changed;
    }
}
