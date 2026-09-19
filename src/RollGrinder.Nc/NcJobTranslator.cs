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

        IRollProfileType profileType = this.profileTypes.Get(job.ProfileTypeKey);
        RollProfile targetProfile = profileType.CreateProfile(job.Geometry, job.ProfileParameters, profilePointCount);
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
        GrindingStepPlan? leadingPlan = plans.FirstOrDefault();
        if (leadingPlan is not null)
        {
            AddNumber(writes, MachineTagKeys.JobFeedMmPerMin, leadingPlan.FeedMmPerMin, timestampUtc);
        }

        for (int i = 0; i < plans.Length; i++)
        {
            GrindingStepPlan plan = plans[i];
            AddInteger(writes, Indexed(MachineTagKeys.JobStepTypeCode, i), StepTypeCode(plan.StepTypeKey), timestampUtc);
            AddInteger(writes, Indexed(MachineTagKeys.JobStepPassCount, i), plan.PassCount, timestampUtc);
            AddNumber(writes, Indexed(MachineTagKeys.JobStepInfeedPerPassRadiusMm, i), plan.InfeedPerPassRadiusMm, timestampUtc);
            AddNumber(writes, Indexed(MachineTagKeys.JobStepFeedMmPerMin, i), plan.FeedMmPerMin, timestampUtc);
            AddNumber(writes, Indexed(MachineTagKeys.JobStepWorkpieceSpeedRpm, i), plan.WorkpieceSpeedRpm, timestampUtc);
            AddNumber(writes, Indexed(MachineTagKeys.JobStepWheelSpeedRpm, i), plan.WheelSpeedRpm, timestampUtc);
            AddInteger(writes, Indexed(MachineTagKeys.JobStepSparkOutPassCount, i), plan.SparkOutPassCount, timestampUtc);
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
