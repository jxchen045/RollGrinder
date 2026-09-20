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

    /// <summary>连续进给分量（直径量 µm/min）。0 表示这一路默认不用。</summary>
    public double ContinuousInfeedDiameterMicrometerPerMin { get; init; } = 5.0;

    /// <summary>连续进给上限（直径量 µm/min）。</summary>
    public double MaxContinuousInfeedDiameterMicrometerPerMin { get; init; } = 200.0;

    /// <summary>周期进给分量（直径量 µm/道次）。0 表示这一路默认不用。</summary>
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
/// 纵磨工序的共同实现：砂轮沿辊身往复，X 轴同时可以有两路切入分量——
/// 连续分量（走行程时持续切入）与周期分量（换向点一次性切入）。
///
/// **两者可以同时非零，不是二选一**（见 docs/design/工艺参数语义.md）：
/// 依据是 MGK84160 操作说明书的磨削实例表——粗磨 连续 0.05 mm/min 与周期 0.005 mm
/// 同时给值。哪一路不用就把它设成 0，展开时照原值下发，NC 侧把两路相加。
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

        double targetStockRadiusMm = UnitConversion.DiameterMicrometerToRadiusMm(
            values.GetNumber(StepParameterKeys.StockDiameterMicrometer));

        // 两路分量都照原值展开——设成 0 的那一路自然就不起作用，不需要再压一次。
        double infeedPerPassRadiusMm = UnitConversion.DiameterMicrometerToRadiusMm(
            values.GetNumber(StepParameterKeys.InfeedPerPassDiameterMicrometer));
        double continuousInfeedRadiusMmPerMin = UnitConversion.DiameterMicrometerPerMinToRadiusMmPerMin(
            values.GetNumber(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin));

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
            ContinuousInfeedRadiusMmPerMin = continuousInfeedRadiusMmPerMin,
            TargetStockRadiusMm = targetStockRadiusMm,
            WheelSurfaceSpeedMPerSec = values.GetNumber(StepParameterKeys.WheelSurfaceSpeedMPerSec),
            ReversalDwellSeconds = values.GetNumber(StepParameterKeys.ReversalDwellSeconds),
            InProcessMeasurement = values.GetBoolean(StepParameterKeys.InProcessMeasurement),
            SpeedVariation = ReadSpeedVariation(values),
        };
    }

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
