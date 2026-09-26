using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;

namespace RollGrinder.Nc;

/// <summary>
/// 把一份作业翻译成对 NC 的写入序列。
/// 逻辑名的物理地址、数组长度、工序类型代码全部来自配置，这里不写任何机床数字。
/// </summary>
public sealed class NcJobTranslator
{
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly ITagMap tagMap;
    private readonly MachineDescription machine;

    public NcJobTranslator(
        RollProfileTypeRegistry profileTypes,
        GrindingStepTypeRegistry stepTypes,
        ITagMap tagMap,
        MachineDescription machine)
    {
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
    }

    /// <summary>
    /// 生成下发内容。
    /// </summary>
    /// <param name="job">作业。</param>
    /// <param name="compensation">补偿曲线（半径量 mm），无补偿传 null。</param>
    /// <param name="requestedProfileSampleCount">期望的辊形采样点数；实际点数还受 tagmap 的数组长度限制。</param>
    /// <param name="timestampUtc">写入值的时间戳。</param>
    public NcDownload Translate(
        GrindingJob job,
        RollProfile? compensation,
        int requestedProfileSampleCount,
        DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (requestedProfileSampleCount < 2)
        {
            throw new DomainException("A profile needs at least two samples.");
        }

        int profilePointCount = Math.Min(requestedProfileSampleCount, ArrayLength(MachineTagKeys.JobProfileBodyPositionMm));
        profilePointCount = Math.Min(profilePointCount, ArrayLength(MachineTagKeys.JobProfileRadiusOffsetMm));
        if (profilePointCount < 2)
        {
            throw new GatewayException(
                "tagmap.json does not provide at least two profile slots; the roll profile cannot be handed over.");
        }

        // 辊形按段合成之后才下发：NC 只收到一条点列，不关心它是由几段叠出来的。
        RollProfile targetProfile = job.Profile.Compose(job.Geometry, this.profileTypes, profilePointCount);
        if (compensation is not null)
        {
            targetProfile = targetProfile.Add(compensation);
        }

        GrindingStepPlan[] plans = job.Steps
            .Select(step => this.stepTypes.Get(step.StepTypeKey).CreatePlan(job.Geometry, step.Parameters))
            .ToArray();

        int stepSlots = ArrayLength(MachineTagKeys.JobStepTypeCode);
        if (plans.Length > stepSlots)
        {
            throw new GatewayException(
                $"The job has {plans.Length} steps but tagmap.json only provides {stepSlots} step slots.");
        }

        var writes = new List<TagWrite>();

        AddNumber(writes, MachineTagKeys.JobRollRadiusMm, job.Geometry.NominalRadiusMm, timestampUtc);
        AddNumber(writes, MachineTagKeys.JobBodyLengthMm, job.Geometry.BodyLengthMm, timestampUtc);
        AddInteger(writes, MachineTagKeys.JobStepCount, plans.Length, timestampUtc);
        AddInteger(writes, MachineTagKeys.JobProfilePointCount, targetProfile.Points.Count, timestampUtc);

        // 首道磨削工序的进给同时写到通用进给变量，方便老程序直接引用。
        //
        // 必须挑"真的走刀"的那一道：开始、结束、暂停这类工序没有拖板进给（0）。
        // 以前直接取第一道，程序以"开始"打头时这里写的是 F0——第一轮甲方测试里
        // 仿真机床因此 3 秒"磨完"一支辊。没有进给的道次一律跳过。
        GrindingStepPlan? leadingPlan = LeadingTraversePlan(plans);
        if (leadingPlan is not null)
        {
            AddNumber(writes, MachineTagKeys.JobFeedMmPerMin, leadingPlan.FeedMmPerMin, timestampUtc);
        }

        for (int i = 0; i < plans.Length; i++)
        {
            AppendStep(writes, job.Steps[i], plans[i], i, timestampUtc);
        }

        // 程序步骤开关：NC 程序按它决定要不要走那几段辅助子程序。
        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All)
        {
            AddBoolean(writes, MachineTagKeys.JobOption(option.Key), job.IsProgramOptionEnabled(option.Key), timestampUtc);
        }

        for (int i = 0; i < targetProfile.Points.Count; i++)
        {
            ProfilePoint point = targetProfile.Points[i];
            AddNumber(writes, Indexed(MachineTagKeys.JobProfileBodyPositionMm, i), point.BodyPositionMm, timestampUtc);
            AddNumber(writes, Indexed(MachineTagKeys.JobProfileRadiusOffsetMm, i), point.RadiusOffsetMm, timestampUtc);
        }

        // 必须最后一条：NC 见到它才认这组参数。
        writes.Add(new TagWrite(
            MachineTagKeys.JobParametersValid,
            new TagValue(MachineTagKeys.JobParametersValid, TagDataType.Boolean, true, timestampUtc)));

        return new NcDownload(writes, targetProfile, plans);
    }

    /// <summary>
    /// 只把**一道工序**的参数翻译成写入，**不碰"参数有效"标志**。
    ///
    /// 磨削进行当中改参数走这一条：作业的身份没变，重新脉冲一次握手标志
    /// 会让 NC 以为来了一份新作业。新值落在那一道的 R 参数上，
    /// NC 在下一道次读取——上位机写完就脱手，不参与实时控制回路（最高原则）。
    ///
    /// &gt; **NC 侧已确认（2026-09）**：工序参数在每个道次开始时重读 R 参数，
    /// &gt; 不在作业启动时 latch 进局部变量。磨削当中改参数因此下一道次即生效。
    /// </summary>
    /// <param name="job">改过参数之后的整支作业（用来重新展开计划）。</param>
    /// <param name="stepOrder">要更新的工序序号，从 1 起。</param>
    /// <param name="timestampUtc">时间戳。</param>
    public IReadOnlyList<TagWrite> TranslateStepParameters(
        GrindingJob job,
        int stepOrder,
        DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(job);

        int index = stepOrder - 1;
        if (index < 0 || index >= job.Steps.Count)
        {
            throw new GatewayException($"Job '{job.JobId}' has no step {stepOrder}.");
        }

        int stepSlots = ArrayLength(MachineTagKeys.JobStepTypeCode);
        if (index >= stepSlots)
        {
            throw new GatewayException(
                $"Step {stepOrder} is beyond the {stepSlots} step slots tagmap.json provides.");
        }

        GrindingJobStep step = job.Steps[index];
        GrindingStepPlan plan = this.stepTypes.Get(step.StepTypeKey).CreatePlan(job.Geometry, step.Parameters);

        var writes = new List<TagWrite>();
        AppendStep(writes, step, plan, index, timestampUtc);
        return writes;
    }

    /// <summary>
    /// 第一道真正走刀的工序：优先有切入进给的磨削工序，其次任何拖板在动的工序（如测量）。
    /// 一道都没有就返回 null，此时不写通用进给变量——作业校验会先把这种作业拦下来。
    /// </summary>
    public static GrindingStepPlan? LeadingTraversePlan(IReadOnlyList<GrindingStepPlan> plans) =>
        plans.FirstOrDefault(p => p.FeedMmPerMin > 0.0 && p.FeedMode != StepFeedMode.None)
        ?? plans.FirstOrDefault(p => p.FeedMmPerMin > 0.0);

    /// <summary>一道工序的全部参数写入。全量下发与单道更新共用这一份，免得两边漂移。</summary>
    private void AppendStep(
        List<TagWrite> writes,
        GrindingJobStep step,
        GrindingStepPlan plan,
        int i,
        DateTimeOffset timestampUtc)
    {
            AddInteger(writes, Indexed(MachineTagKeys.JobStepTypeCode, i), StepTypeCode(plan.StepTypeKey), timestampUtc);
            AddInteger(writes, Indexed(MachineTagKeys.JobStepPassCount, i), plan.PassCount, timestampUtc);
            AddNumber(writes, Indexed(MachineTagKeys.JobStepInfeedPerPassRadiusMm, i), plan.InfeedPerPassRadiusMm, timestampUtc);
            AddNumber(writes, Indexed(MachineTagKeys.JobStepFeedMmPerMin, i), plan.FeedMmPerMin, timestampUtc);
            AddNumber(writes, Indexed(MachineTagKeys.JobStepWorkpieceSpeedRpm, i), plan.WorkpieceSpeedRpm, timestampUtc);
            AddNumber(writes, Indexed(MachineTagKeys.JobStepWheelSpeedRpm, i), plan.WheelSpeedRpm, timestampUtc);
            AddInteger(writes, Indexed(MachineTagKeys.JobStepSparkOutPassCount, i), plan.SparkOutPassCount, timestampUtc);

            // 两路进给分量都照原值下发，NC 侧把它们相加；哪一路是 0 就自然不起作用。
            // feedMode 只是这两个值的分类（0 不进给 / 1 仅连续 / 2 仅周期 / 3 两者），
            // 方便 NC 侧分支，不是另一个独立的设定。
            AddInteger(writes, Indexed(MachineTagKeys.JobStepFeedMode, i), (int)plan.FeedMode, timestampUtc);
            AddNumber(
                writes,
                Indexed(MachineTagKeys.JobStepContinuousInfeedRadiusMmPerMin, i),
                plan.ContinuousInfeedRadiusMmPerMin,
                timestampUtc);
            AddNumber(
                writes,
                Indexed(MachineTagKeys.JobStepTargetStockRadiusMm, i),
                plan.TargetStockRadiusMm,
                timestampUtc);
            AddNumber(
                writes,
                Indexed(MachineTagKeys.JobStepWheelSurfaceSpeedMPerSec, i),
                plan.WheelSurfaceSpeedMPerSec,
                timestampUtc);
            AddNumber(
                writes,
                Indexed(MachineTagKeys.JobStepReversalDwellSeconds, i),
                plan.ReversalDwellSeconds,
                timestampUtc);
            AddInteger(
                writes,
                Indexed(MachineTagKeys.JobStepInProcessMeasurement, i),
                plan.InProcessMeasurement ? 1 : 0,
                timestampUtc);
            AddInteger(
                writes,
                Indexed(MachineTagKeys.JobStepSpeedVariationTarget, i),
                (int)plan.SpeedVariation.Target,
                timestampUtc);
            AddNumber(
                writes,
                Indexed(MachineTagKeys.JobStepSpeedVariationPercent, i),
                plan.SpeedVariation.AmplitudePercent,
                timestampUtc);
            AddNumber(
                writes,
                Indexed(MachineTagKeys.JobStepSpeedVariationPeriodRevolutions, i),
                plan.SpeedVariation.PeriodRevolutions,
                timestampUtc);

        AppendStepExtras(writes, step, i, timestampUtc);
    }

    /// <summary>
    /// 工序专属参数：只有某一类工序才有的量（倒角几何、修整道次、探伤螺距、
    /// 圆度采样格）按约定顺序摆进扁平数组，含义由同一槽位的工序类型码决定。
    ///
    /// **顺序就是协议。** 这里只按工序类型声明的顺序摆，不做任何解释；
    /// 怎么读是 NC 那一类工序的子程序的事。
    ///
    /// 没声明专属参数的工序类型，整块留空——不清零：同一槽位上一支辊留下的
    /// 值由 NC 按类型码判断该不该读，清零反而要多写 8 条没人看的数。
    /// </summary>
    private void AppendStepExtras(
        List<TagWrite> writes, GrindingJobStep step, int i, DateTimeOffset timestampUtc)
    {
        IGrindingStepType stepType = this.stepTypes.Get(step.StepTypeKey);
        IReadOnlyList<string> keys = stepType.NcExtraParameterKeys;
        if (keys.Count == 0)
        {
            return;
        }

        if (keys.Count > MachineTagKeys.JobStepExtraCount)
        {
            throw new GatewayException(
                $"Step type '{step.StepTypeKey}' declares {keys.Count} extra parameters but only "
                + $"{MachineTagKeys.JobStepExtraCount} slots exist per step.");
        }

        ParameterSet values = stepType.Schema.ApplyDefaults(step.Parameters);
        for (int k = 0; k < keys.Count; k++)
        {
            AddNumber(writes, MachineTagKeys.JobStepExtraAt(i, k), NumericValue(stepType, values, keys[k]), timestampUtc);
        }
    }

    /// <summary>
    /// 把一个参数值折成 NC 能收的数：数值原样，开关 0/1，
    /// 选项按它在声明顺序里的位置（倒角类型的 0 斜坡 / 1 圆弧就是这么来的）。
    /// </summary>
    private static double NumericValue(IGrindingStepType stepType, ParameterSet values, string key)
    {
        ParameterDescriptor descriptor = stepType.Schema.Get(key);

        return descriptor.Kind switch
        {
            ParameterValueKind.Number => values.GetNumber(key),
            ParameterValueKind.Boolean => values.GetBoolean(key) ? 1.0 : 0.0,
            ParameterValueKind.Choice => IndexOfChoice(descriptor, values.GetChoice(key)),
            var other => throw new GatewayException($"Parameter '{key}' has unsupported kind {other}."),
        };
    }

    private static double IndexOfChoice(ParameterDescriptor descriptor, string choice)
    {
        IReadOnlyList<string> options = descriptor.AllowedValues
            ?? throw new GatewayException($"Choice parameter '{descriptor.Key}' declares no options.");

        for (int i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i], choice, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new GatewayException($"Choice parameter '{descriptor.Key}' has no option '{choice}'.");
    }

    /// <summary>下发前检查必需的逻辑名是否都在 tagmap 里，返回缺失的键。</summary>
    public IReadOnlyList<string> FindMissingRequiredTags()
    {
        string[] required =
        {
            MachineTagKeys.JobRollRadiusMm,
            MachineTagKeys.JobBodyLengthMm,
            MachineTagKeys.JobStepCount,
            MachineTagKeys.JobProfilePointCount,
            MachineTagKeys.JobStepTypeCode,
            MachineTagKeys.JobStepPassCount,
            MachineTagKeys.JobStepInfeedPerPassRadiusMm,
            MachineTagKeys.JobStepFeedMmPerMin,

            // 进给方式与两个进给量一起决定这道工序切多少，缺一个就下发不了。
            MachineTagKeys.JobStepFeedMode,
            MachineTagKeys.JobStepContinuousInfeedRadiusMmPerMin,
            MachineTagKeys.JobStepTargetStockRadiusMm,
            MachineTagKeys.JobProfileBodyPositionMm,
            MachineTagKeys.JobProfileRadiusOffsetMm,
            MachineTagKeys.JobParametersValid,
        };

        return required.Where(key => !this.tagMap.TryResolve(FirstSlot(key), out _)).ToArray();
    }

    private static string FirstSlot(string key) => key switch
    {
        MachineTagKeys.JobStepTypeCode or
        MachineTagKeys.JobStepPassCount or
        MachineTagKeys.JobStepInfeedPerPassRadiusMm or
        MachineTagKeys.JobStepFeedMmPerMin or
        MachineTagKeys.JobStepFeedMode or
        MachineTagKeys.JobStepContinuousInfeedRadiusMmPerMin or
        MachineTagKeys.JobStepTargetStockRadiusMm or
        MachineTagKeys.JobProfileBodyPositionMm or
        MachineTagKeys.JobProfileRadiusOffsetMm => TagKeySyntax.Indexed(key, 0),
        _ => key,
    };

    private static string Indexed(string key, int index) => TagKeySyntax.Indexed(key, index);

    private int StepTypeCode(string stepTypeKey) =>
        this.machine.StepTypeCodes.TryGetValue(stepTypeKey, out int code)
            ? code
            : throw new GatewayException(
                $"machine.json does not define an NC step type code for '{stepTypeKey}'.");

    private int ArrayLength(string baseKey) =>
        this.tagMap.TryResolve(baseKey, out TagDescriptor? descriptor) && descriptor is not null
            ? descriptor.ArrayLength
            : 0;

    private void AddBoolean(ICollection<TagWrite> writes, string logicalName, bool value, DateTimeOffset timestampUtc)
    {
        if (!this.tagMap.TryResolve(logicalName, out TagDescriptor? descriptor) || descriptor is null)
        {
            return;
        }

        // 有些机床把开关映射成 R 参数而不是布尔位，所以按 tagmap 声明的类型写。
        object raw = descriptor.DataType switch
        {
            TagDataType.Boolean => value,
            TagDataType.Double => value ? 1.0 : 0.0,
            _ => value ? 1 : 0,
        };

        writes.Add(new TagWrite(logicalName, new TagValue(logicalName, descriptor.DataType, raw, timestampUtc)));
    }

    private void AddNumber(ICollection<TagWrite> writes, string logicalName, double value, DateTimeOffset timestampUtc)
    {
        if (!this.tagMap.TryResolve(logicalName, out TagDescriptor? descriptor) || descriptor is null)
        {
            return;
        }

        writes.Add(new TagWrite(logicalName, new TagValue(logicalName, descriptor.DataType, value, timestampUtc)));
    }

    private void AddInteger(ICollection<TagWrite> writes, string logicalName, int value, DateTimeOffset timestampUtc)
    {
        if (!this.tagMap.TryResolve(logicalName, out TagDescriptor? descriptor) || descriptor is null)
        {
            return;
        }

        object raw = descriptor.DataType == TagDataType.Double ? (double)value : value;
        writes.Add(new TagWrite(logicalName, new TagValue(logicalName, descriptor.DataType, raw, timestampUtc)));
    }
}
