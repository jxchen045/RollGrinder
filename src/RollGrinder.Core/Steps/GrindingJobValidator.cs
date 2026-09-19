using System;
using System.Collections.Generic;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 作业校验：参数是否符合各自 schema、工序展开后是否超出本台机床的能力。
/// 只报事实，不产出界面文字；界面按 <see cref="ParameterViolation"/> 取本地化文案。
/// </summary>
public sealed class GrindingJobValidator
{
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly GrindingStepTypeRegistry stepTypes;

    public GrindingJobValidator(RollProfileTypeRegistry profileTypes, GrindingStepTypeRegistry stepTypes)
    {
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
    }

    /// <summary>校验一份作业。</summary>
    public ParameterValidationResult Validate(GrindingJob job, MachineCapability capability)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(capability);

        var violations = new List<ParameterViolation>();

        IRollProfileType profileType = this.profileTypes.Get(job.ProfileTypeKey);
        violations.AddRange(profileType.Schema.Validate(job.ProfileParameters).Violations);
        violations.AddRange(ValidateGeometry(job, capability));

        foreach (GrindingJobStep step in job.Steps)
        {
            IGrindingStepType stepType = this.stepTypes.Get(step.StepTypeKey);
            ParameterValidationResult schemaResult = stepType.Schema.Validate(step.Parameters);
            violations.AddRange(schemaResult.Violations);
            if (!schemaResult.IsValid)
            {
                // 参数本身不合法时不再展开计划，避免二次报错掩盖根因。
                continue;
            }

            violations.AddRange(ValidatePlan(stepType.CreatePlan(job.Geometry, step.Parameters), capability));
        }

        return new ParameterValidationResult(violations);
    }

    private static IEnumerable<ParameterViolation> ValidateGeometry(GrindingJob job, MachineCapability capability)
    {
        if (job.Geometry.BodyLengthMm < capability.MinBodyLengthMm)
        {
            yield return new ParameterViolation(nameof(job.Geometry.BodyLengthMm), ParameterViolationKind.ExceedsMachineLimit, capability.MinBodyLengthMm);
        }

        if (job.Geometry.BodyLengthMm > capability.MaxBodyLengthMm)
        {
            yield return new ParameterViolation(nameof(job.Geometry.BodyLengthMm), ParameterViolationKind.ExceedsMachineLimit, capability.MaxBodyLengthMm);
        }

        if (job.Geometry.NominalRadiusMm < capability.MinRadiusMm)
        {
            yield return new ParameterViolation(nameof(job.Geometry.NominalRadiusMm), ParameterViolationKind.ExceedsMachineLimit, capability.MinRadiusMm);
        }

        if (job.Geometry.NominalRadiusMm > capability.MaxRadiusMm)
        {
            yield return new ParameterViolation(nameof(job.Geometry.NominalRadiusMm), ParameterViolationKind.ExceedsMachineLimit, capability.MaxRadiusMm);
        }
    }

    private static IEnumerable<ParameterViolation> ValidatePlan(GrindingStepPlan plan, MachineCapability capability)
    {
        if (plan.InfeedPerPassRadiusMm > capability.MaxInfeedPerPassRadiusMm)
        {
            yield return new ParameterViolation(
                StepParameterKeys.InfeedPerPassDiameterMicrometer,
                ParameterViolationKind.ExceedsMachineLimit,
                capability.MaxInfeedPerPassDiameterMicrometer);
        }

        if (plan.FeedMmPerMin > capability.MaxFeedMmPerMin)
        {
            yield return new ParameterViolation(
                StepParameterKeys.FeedMmPerMin,
                ParameterViolationKind.ExceedsMachineLimit,
                capability.MaxFeedMmPerMin);
        }

        if (plan.WorkpieceSpeedRpm > capability.MaxWorkpieceSpeedRpm)
        {
            yield return new ParameterViolation(
                StepParameterKeys.WorkpieceSpeedRpm,
                ParameterViolationKind.ExceedsMachineLimit,
                capability.MaxWorkpieceSpeedRpm);
        }

        if (plan.WheelSpeedRpm > capability.MaxWheelSpeedRpm)
        {
            yield return new ParameterViolation(
                StepParameterKeys.WheelSpeedRpm,
                ParameterViolationKind.ExceedsMachineLimit,
                capability.MaxWheelSpeedRpm);
        }
    }
}
