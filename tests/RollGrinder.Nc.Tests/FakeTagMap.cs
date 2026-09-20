using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Nc.Tests;

/// <summary>
/// 测试用的最小 tagmap：只做键查找与下标展开，不依赖组合根。
/// </summary>
internal sealed class FakeTagMap : ITagMap
{
    private readonly Dictionary<string, TagDescriptor> byKey;

    public FakeTagMap(IEnumerable<TagDescriptor> tags)
    {
        this.byKey = tags.ToDictionary(tag => tag.Key, StringComparer.Ordinal);
        Tags = this.byKey.Values.ToArray();
    }

    public IReadOnlyList<TagDescriptor> Tags { get; }

    public bool TryResolve(string logicalName, out TagDescriptor? descriptor)
    {
        if (this.byKey.TryGetValue(logicalName, out descriptor))
        {
            return true;
        }

        if (TagKeySyntax.TrySplit(logicalName, out string baseKey, out int index)
            && this.byKey.TryGetValue(baseKey, out TagDescriptor? arrayDescriptor)
            && arrayDescriptor.IsArray
            && index < arrayDescriptor.ArrayLength)
        {
            descriptor = arrayDescriptor.AtIndex(index);
            return true;
        }

        descriptor = null;
        return false;
    }

    public TagDescriptor Resolve(string logicalName) =>
        TryResolve(logicalName, out TagDescriptor? descriptor) && descriptor is not null
            ? descriptor
            : throw new GatewayException($"Tag '{logicalName}' is not present in the tag map.");

    /// <summary>一份覆盖下发所需全部逻辑名的映射。</summary>
    public static FakeTagMap Complete(int stepSlots = 8, int profileSlots = 21) => new(new[]
    {
        Scalar(MachineTagKeys.JobRollRadiusMm, TagDataType.Double),
        Scalar(MachineTagKeys.JobBodyLengthMm, TagDataType.Double),
        Scalar(MachineTagKeys.JobFeedMmPerMin, TagDataType.Double),
        Scalar(MachineTagKeys.JobStepCount, TagDataType.Int32),
        Scalar(MachineTagKeys.JobProfilePointCount, TagDataType.Int32),
        Scalar(MachineTagKeys.JobParametersValid, TagDataType.Boolean),
        Array(MachineTagKeys.JobStepTypeCode, TagDataType.Int32, stepSlots),
        Array(MachineTagKeys.JobStepPassCount, TagDataType.Int32, stepSlots),
        Array(MachineTagKeys.JobStepInfeedPerPassRadiusMm, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobStepFeedMmPerMin, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobStepWorkpieceSpeedRpm, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobStepWheelSpeedRpm, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobStepSparkOutPassCount, TagDataType.Int32, stepSlots),
        Array(MachineTagKeys.JobStepFeedMode, TagDataType.Int32, stepSlots),
        Array(MachineTagKeys.JobStepContinuousInfeedRadiusMmPerMin, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobStepTargetStockRadiusMm, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobStepWheelSurfaceSpeedMPerSec, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobStepReversalDwellSeconds, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobStepInProcessMeasurement, TagDataType.Int32, stepSlots),
        Array(MachineTagKeys.JobStepSpeedVariationTarget, TagDataType.Int32, stepSlots),
        Array(MachineTagKeys.JobStepSpeedVariationPercent, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobStepSpeedVariationPeriodSeconds, TagDataType.Double, stepSlots),
        Array(MachineTagKeys.JobProfileBodyPositionMm, TagDataType.Double, profileSlots),
        Array(MachineTagKeys.JobProfileRadiusOffsetMm, TagDataType.Double, profileSlots),
    });

    public FakeTagMap Without(string key) => new(Tags.Where(tag => tag.Key != key));

    private static TagDescriptor Scalar(string key, TagDataType dataType) =>
        new(key, "sim://" + key, dataType, TagAccess.ReadWrite);

    private static TagDescriptor Array(string key, TagDataType dataType, int length) =>
        new(key, "sim://" + key + "/{index}", dataType, TagAccess.ReadWrite, ArrayLength: length);
}
