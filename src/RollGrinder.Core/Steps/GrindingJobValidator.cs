using System;
using System.Collections.Generic;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;

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

        violations.AddRange(ValidateProfile(job));
        violations.AddRange(ValidateGeometry(job, capability));
        violations.AddRange(ProgramOptionCatalog.Schema.Validate(job.ProgramOptions).Violations);
        violations.AddRange(ValidateProgramOptions(job, capability));

        foreach (GrindingJobStep step in job.Steps)
        {
            IGrindingStepType stepType = this.stepTypes.Get(step.StepTypeKey);

            if (!capability.Supports(stepType))
            {
                // 这台机床没装这道工序要用的装置：参数再对也没用，不再往下展开。
                violations.Add(new ParameterViolation(stepType.Key, ParameterViolationKind.MachineOptionMissing));
                continue;
            }

            ParameterValidationResult schemaResult = stepType.Schema.Validate(step.Parameters);
            violations.AddRange(schemaResult.Violations);
            if (!schemaResult.IsValid)
            {
                // 参数本身不合法时不再展开计划，避免二次报错掩盖根因。
                continue;
            }

            violations.AddRange(ValidatePlan(
                stepType.CreatePlan(job.Geometry, step.Parameters), capability));
        }

        return new ParameterValidationResult(violations);
    }

    /// <summary>
    /// 辊形：每一段各自按自己那条曲线的 schema 校验，区间还得落在辊身之内。
    ///
    /// 逐段报，参数键前面带上段号（<c>seg2.crownDiameterMicrometer</c>）——
    /// 叠了四段的时候，只说"凸度超限"操作员不知道该改哪一段。
    /// </summary>
    private IEnumerable<ParameterViolation> ValidateProfile(GrindingJob job)
    {
        foreach (RollProfileSegment segment in job.Profile.Segments)
        {
            IRollProfileType profileType = this.profileTypes.Get(segment.ProfileTypeKey);

            foreach (ParameterViolation violation in profileType.Schema.Validate(segment.Parameters).Violations)
            {
                yield return violation with { ParameterKey = SegmentKey(segment, violation.ParameterKey) };
            }

            if (segment.ToMm > job.Geometry.BodyLengthMm)
            {
                yield return new ParameterViolation(
                    SegmentKey(segment, nameof(RollProfileSegment.ToMm)),
                    ParameterViolationKind.ExceedsMachineLimit,
                    job.Geometry.BodyLengthMm);
            }
        }
    }

    /// <summary>段号前缀，让报出来的键指得到具体是哪一段。</summary>
    private static string SegmentKey(RollProfileSegment segment, string parameterKey) =>
        $"seg{segment.Order}.{parameterKey}";

    /// <summary>
    /// 程序步骤开关：开着的那些，机床得做得了。
    /// 这条挡得住从程序库里调出来、在别台机床上编好的程序。
    /// </summary>
    private static IEnumerable<ParameterViolation> ValidateProgramOptions(
        GrindingJob job,
        MachineCapability capability)
    {
        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All)
        {
            if (job.IsProgramOptionEnabled(option.Key) && !capability.Supports(option))
            {
                yield return new ParameterViolation(option.Key, ParameterViolationKind.MachineOptionMissing);
            }
        }
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

    private static IEnumerable<ParameterViolation> ValidatePlan(
        GrindingStepPlan plan,
        MachineCapability capability)
    {
        // 两路进给分量各有各的限幅，和机床侧的软件保护一一对应
        // （MGK84160 操作说明书：端部周期进给 >0.2 mm 或连续进给 >0.5 mm/min 时该参数不生效）。
        // 不做"把连续折算成每道次再合并卡一次"——那样一根 5 m 的辊子按说明书自己的
        // 粗磨参数就会被判超限，而机床本身并不这么卡。
        if (plan.InfeedPerPassRadiusMm > capability.MaxInfeedPerPassRadiusMm)
        {
            yield return new ParameterViolation(
                StepParameterKeys.InfeedPerPassDiameterMicrometer,
                ParameterViolationKind.ExceedsMachineLimit,
                capability.MaxInfeedPerPassDiameterMicrometer);
        }

        if (capability.MaxContinuousInfeedRadiusMmPerMin is double maxContinuousRadiusMmPerMin
            && plan.ContinuousInfeedRadiusMmPerMin > maxContinuousRadiusMmPerMin)
        {
            yield return new ParameterViolation(
                StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin,
                ParameterViolationKind.ExceedsMachineLimit,
                UnitConversion.RadiusMmToDiameterMicrometer(maxContinuousRadiusMmPerMin));
        }

        // 只有周期分量单独用时，"道次 × 每道次"才等于"磨削量"，对不上就是设错了。
        // 一旦叠了连续分量，实际去除量还取决于行程时间（辊身长度 ÷ 拖板速度），
        // 两个终止条件谁先到先停（见 docs/design/工艺参数语义.md 三），不再是可以对账的等式。
        if (plan.FeedMode == StepFeedMode.PerReversal
            && plan.TargetStockRadiusMm > 0.0)
        {
            double plannedRadiusMm = plan.TotalInfeedRadiusMm;
            double toleranceRadiusMm = Math.Max(
                UnitConversion.DiameterMicrometerToRadiusMm(1.0),
                plan.TargetStockRadiusMm * 0.05);

            if (Math.Abs(plannedRadiusMm - plan.TargetStockRadiusMm) > toleranceRadiusMm)
            {
                yield return new ParameterViolation(
                    StepParameterKeys.StockDiameterMicrometer,
                    ParameterViolationKind.Inconsistent,
                    UnitConversion.RadiusMmToDiameterMicrometer(plannedRadiusMm));
            }
        }

        if (plan.FeedMmPerMin > capability.MaxFeedMmPerMin)
        {
            yield return new ParameterViolation(
                StepParameterKeys.FeedMmPerMin,
                ParameterViolationKind.ExceedsMachineLimit,
                capability.MaxFeedMmPerMin);
        }

        // 变速的峰值才是机床真正要跑到的转速，按峰值卡限幅，不按设定值。
        double workpiecePeakRpm = plan.SpeedVariation.AffectsWorkpiece
            ? plan.SpeedVariation.PeakOf(plan.WorkpieceSpeedRpm)
            : plan.WorkpieceSpeedRpm;

        if (workpiecePeakRpm > capability.MaxWorkpieceSpeedRpm)
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

        foreach (ParameterViolation violation in ValidateWheelSurfaceSpeed(plan, capability))
        {
            yield return violation;
        }

        // 要测量的工序得有测头。没有测头还编"磨后测量"，到现场就是一道空转的工序。
        if (plan.RequiresMeasurement && !capability.CanMeasureDiameter)
        {
            yield return new ParameterViolation(plan.StepTypeKey, ParameterViolationKind.MachineOptionMissing);
        }

        if (plan.InProcessMeasurement && !capability.CanMeasureDiameter)
        {
            yield return new ParameterViolation(
                StepParameterKeys.InProcessMeasurement, ParameterViolationKind.MachineOptionMissing);
        }
    }

    /// <summary>
    /// 砂轮线速度。砂轮变速会同步改变线速度，所以上下限要按变速后的峰谷算——
    /// 线速度高了烧伤，低了磨不动，两头都要管。
    /// machine.json 没给这两个阈值时跳过，不猜。
    /// </summary>
    private static IEnumerable<ParameterViolation> ValidateWheelSurfaceSpeed(
        GrindingStepPlan plan,
        MachineCapability capability)
    {
        if (plan.WheelSurfaceSpeedMPerSec <= 0.0)
        {
            yield break;
        }

        double peak = plan.SpeedVariation.AffectsWheel
            ? plan.SpeedVariation.PeakOf(plan.WheelSurfaceSpeedMPerSec)
            : plan.WheelSurfaceSpeedMPerSec;
        double trough = plan.SpeedVariation.AffectsWheel
            ? plan.SpeedVariation.TroughOf(plan.WheelSurfaceSpeedMPerSec)
            : plan.WheelSurfaceSpeedMPerSec;

        if (capability.MaxWheelSurfaceSpeedMPerSec is double maximum && peak > maximum)
        {
            yield return new ParameterViolation(
                StepParameterKeys.WheelSurfaceSpeedMPerSec,
                ParameterViolationKind.ExceedsMachineLimit,
                maximum);
        }

        if (capability.MinWheelSurfaceSpeedMPerSec is double minimum && trough < minimum)
        {
            yield return new ParameterViolation(
                StepParameterKeys.WheelSurfaceSpeedMPerSec,
                ParameterViolationKind.BelowMinimum,
                minimum);
        }
    }
}
