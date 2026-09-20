using System;
using System.Collections.Generic;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 一类纵磨工序的默认值与限值。短行程、粗磨、半精磨、精磨、抛光只是这张表不同，
/// 参数项与展开逻辑完全共用。
/// </summary>
public sealed record TraverseStepDefaults
{
    /// <summary>砂轮线速度（m/s）。</summary>
    public double WheelSurfaceSpeedMPerSec { get; init; } = 30.0;

    /// <summary>头架转速（r/min）。</summary>
    public double WorkpieceSpeedRpm { get; init; } = 25.0;

    /// <summary>拖板速度（mm/min）。</summary>
    public double FeedMmPerMin { get; init; } = 1200.0;

    /// <summary>默认进给方式。</summary>
    public string FeedMode { get; init; } = FeedModeChoices.PerReversal;

    /// <summary>连续进给（直径量 µm/min）。</summary>
    public double ContinuousInfeedDiameterMicrometerPerMin { get; init; } = 5.0;

    /// <summary>连续进给上限（直径量 µm/min）。</summary>
    public double MaxContinuousInfeedDiameterMicrometerPerMin { get; init; } = 200.0;

    /// <summary>周期进给（直径量 µm/道次）。</summary>
    public double InfeedPerPassDiameterMicrometer { get; init; } = 10.0;

    /// <summary>周期进给上限（直径量 µm/道次）。</summary>
    public double MaxInfeedPerPassDiameterMicrometer { get; init; } = 40.0;

    /// <summary>磨削道次（往复）。</summary>
    public double PassCount { get; init; } = 10.0;

    /// <summary>目标去除量（直径量 µm）。</summary>
    public double StockDiameterMicrometer { get; init; } = 100.0;

    /// <summary>折返时间（s）。</summary>
    public double ReversalDwellSeconds { get; init; } = 1.0;

    /// <summary>光磨道次。</summary>
    public double SparkOutPassCount { get; init; }

    /// <summary>在线测量默认开关。</summary>
    public bool InProcessMeasurement { get; init; }

    /// <summary>变速模式。</summary>
    public string SpeedVariationTarget { get; init; } = SpeedVariationChoices.Workpiece;

    /// <summary>变速幅度（±%）。</summary>
    public double SpeedVariationPercent { get; init; } = 8.0;

    /// <summary>变速幅度上限（±%）。砂轮变速会同步改变线速度，用它把幅度压住。</summary>
    public double MaxSpeedVariationPercent { get; init; } = 20.0;

    /// <summary>变速周期（s）。</summary>
    public double SpeedVariationPeriodSeconds { get; init; } = 5.0;

    /// <summary>本工序结束后是否需要测量。</summary>
    public bool RequiresMeasurement { get; init; }
}

/// <summary>
/// 纵磨工序的共同实现：砂轮沿辊身往复，X 轴按"连续"或"周期"其中一种方式切入。
///
/// 两种进给方式互斥（见 docs/design/工艺参数语义.md）：
/// 参数格里两个都在，但只有 <see cref="StepParameterKeys.FeedMode"/> 选中的那个参与展开，
/// 另一个在计划里置 0，NC 侧也就收不到它。
/// </summary>
public abstract class TraverseGrindingStepType : IGrindingStepType
{
    private readonly TraverseStepDefaults defaults;

    protected TraverseGrindingStepType(string key, TraverseStepDefaults defaults)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        this.defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        Schema = BuildSchema(defaults);
    }

    public string Key { get; }

    public ParameterSchema Schema { get; }

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        ParameterSet values = Schema.ApplyDefaults(parameters);

        StepFeedMode feedMode = ReadFeedMode(values);
        double targetStockRadiusMm = UnitConversion.DiameterMicrometerToRadiusMm(
            values.GetNumber(StepParameterKeys.StockDiameterMicrometer));

        double infeedPerPassRadiusMm = 0.0;
        double continuousInfeedRadiusMmPerMin = 0.0;
        if (feedMode == StepFeedMode.PerReversal)
        {
            infeedPerPassRadiusMm = UnitConversion.DiameterMicrometerToRadiusMm(
                values.GetNumber(StepParameterKeys.InfeedPerPassDiameterMicrometer));
        }
        else if (feedMode == StepFeedMode.Continuous)
        {
            continuousInfeedRadiusMmPerMin = UnitConversion.DiameterMicrometerPerMinToRadiusMmPerMin(
                values.GetNumber(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin));
        }

        return new GrindingStepPlan(
            Key,
            (int)values.GetNumber(StepParameterKeys.PassCount),
            infeedPerPassRadiusMm,
            values.GetNumber(StepParameterKeys.FeedMmPerMin),
            values.GetNumber(StepParameterKeys.WorkpieceSpeedRpm),

            // 砂轮按恒线速控制，rpm 由 NC 按当前砂轮直径算——上位机不掺和实时回路。
            WheelSpeedRpm: 0.0,
            (int)values.GetNumber(StepParameterKeys.SparkOutPassCount),
            this.defaults.RequiresMeasurement)
        {
            FeedMode = feedMode,
            ContinuousInfeedRadiusMmPerMin = continuousInfeedRadiusMmPerMin,
            TargetStockRadiusMm = targetStockRadiusMm,
            WheelSurfaceSpeedMPerSec = values.GetNumber(StepParameterKeys.WheelSurfaceSpeedMPerSec),
            ReversalDwellSeconds = values.GetNumber(StepParameterKeys.ReversalDwellSeconds),
            InProcessMeasurement = values.GetBoolean(StepParameterKeys.InProcessMeasurement),
            SpeedVariation = ReadSpeedVariation(values),
        };
    }

    private static StepFeedMode ReadFeedMode(ParameterSet values) =>
        values.GetChoice(StepParameterKeys.FeedMode) switch
        {
            FeedModeChoices.Continuous => StepFeedMode.Continuous,
            FeedModeChoices.PerReversal => StepFeedMode.PerReversal,
            var other => throw new DomainException($"Unknown feed mode '{other}'."),
        };

    private static SpeedVariation ReadSpeedVariation(ParameterSet values)
    {
        SpeedVariationTarget target = values.GetChoice(StepParameterKeys.SpeedVariationTarget) switch
        {
            SpeedVariationChoices.Off => SpeedVariationTarget.Off,
            SpeedVariationChoices.Workpiece => SpeedVariationTarget.Workpiece,
            SpeedVariationChoices.Wheel => SpeedVariationTarget.Wheel,
            SpeedVariationChoices.Both => SpeedVariationTarget.Both,
            var other => throw new DomainException($"Unknown speed variation target '{other}'."),
        };

        return target == SpeedVariationTarget.Off
            ? SpeedVariation.Off
            : new SpeedVariation(
                target,
                values.GetNumber(StepParameterKeys.SpeedVariationPercent),
                values.GetNumber(StepParameterKeys.SpeedVariationPeriodSeconds));
    }

    private static ParameterSchema BuildSchema(TraverseStepDefaults defaults)
    {
        var descriptors = new List<ParameterDescriptor>
        {
            ParameterDescriptor.Number(
                StepParameterKeys.WheelSurfaceSpeedMPerSec,
                ParameterUnit.MeterPerSecond,
                defaults.WheelSurfaceSpeedMPerSec,
                5.0,
                80.0),
            ParameterDescriptor.Number(
                StepParameterKeys.WorkpieceSpeedRpm,
                ParameterUnit.RevolutionsPerMinute,
                defaults.WorkpieceSpeedRpm,
                0.1,
                500.0),
            ParameterDescriptor.Number(
                StepParameterKeys.FeedMmPerMin,
                ParameterUnit.MillimeterPerMinute,
                defaults.FeedMmPerMin,
                1.0,
                20000.0),
            ParameterDescriptor.Choice(
                StepParameterKeys.FeedMode,
                new[] { FeedModeChoices.Continuous, FeedModeChoices.PerReversal },
                defaults.FeedMode),
            ParameterDescriptor.Number(
                StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin,
                ParameterUnit.MicrometerPerMinute,
                defaults.ContinuousInfeedDiameterMicrometerPerMin,
                0.0,
                defaults.MaxContinuousInfeedDiameterMicrometerPerMin),
            ParameterDescriptor.Number(
                StepParameterKeys.InfeedPerPassDiameterMicrometer,
                ParameterUnit.Micrometer,
                defaults.InfeedPerPassDiameterMicrometer,
                0.0,
                defaults.MaxInfeedPerPassDiameterMicrometer),
            ParameterDescriptor.Number(
                StepParameterKeys.PassCount,
                ParameterUnit.Count,
                defaults.PassCount,
                1.0,
                200.0),
            ParameterDescriptor.Number(
                StepParameterKeys.StockDiameterMicrometer,
                ParameterUnit.Micrometer,
                defaults.StockDiameterMicrometer,
                0.0,
                5000.0),
            ParameterDescriptor.Number(
                StepParameterKeys.ReversalDwellSeconds,
                ParameterUnit.Second,
                defaults.ReversalDwellSeconds,
                0.0,
                60.0),
            ParameterDescriptor.Number(
                StepParameterKeys.SparkOutPassCount,
                ParameterUnit.Count,
                defaults.SparkOutPassCount,
                0.0,
                20.0),
            ParameterDescriptor.Boolean(
                StepParameterKeys.InProcessMeasurement,
                defaults.InProcessMeasurement),
            ParameterDescriptor.Choice(
                StepParameterKeys.SpeedVariationTarget,
                new[]
                {
                    SpeedVariationChoices.Off,
                    SpeedVariationChoices.Workpiece,
                    SpeedVariationChoices.Wheel,
                    SpeedVariationChoices.Both,
                },
                defaults.SpeedVariationTarget),
            ParameterDescriptor.Number(
                StepParameterKeys.SpeedVariationPercent,
                ParameterUnit.Percent,
                defaults.SpeedVariationPercent,
                0.0,
                defaults.MaxSpeedVariationPercent),
            ParameterDescriptor.Number(
                StepParameterKeys.SpeedVariationPeriodSeconds,
                ParameterUnit.Second,
                defaults.SpeedVariationPeriodSeconds,
                1.0,
                30.0),
        };

        return new ParameterSchema(descriptors);
    }
}
