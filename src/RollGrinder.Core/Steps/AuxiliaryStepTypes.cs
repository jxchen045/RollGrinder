using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 标记类工序：开始与结束。不产生任何运动，只在工序序列里占一格，
/// 让 NC 知道程序的起点与终点，也让界面能画出完整的序列。
/// </summary>
public abstract class MarkerStepType : IGrindingStepType
{
    protected MarkerStepType(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public string Key { get; }

    public ParameterSchema Schema => ParameterSchema.Empty;

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        return new GrindingStepPlan(
            Key,
            PassCount: 0,
            InfeedPerPassRadiusMm: 0.0,
            FeedMmPerMin: 0.0,
            WorkpieceSpeedRpm: 0.0,
            WheelSpeedRpm: 0.0,
            SparkOutPassCount: 0,
            RequiresMeasurement: false);
    }
}

/// <summary>开始：程序起点。</summary>
public sealed class StartStepType : MarkerStepType
{
    public StartStepType()
        : base(StepTypeKeys.Start)
    {
    }
}

/// <summary>结束：程序终点。</summary>
public sealed class EndStepType : MarkerStepType
{
    public EndStepType()
        : base(StepTypeKeys.End)
    {
    }
}

/// <summary>
/// 砂轮修整：金刚笔沿砂轮走，把砂轮修圆修锐。
/// 切深是砂轮的半径量——修的是砂轮不是轧辊，所以这里不走直径量换算。
/// </summary>
public sealed class WheelDressStepType : IGrindingStepType
{
    public string Key => StepTypeKeys.WheelDress;

    /// <summary>没有修整装置就没法修砂轮。</summary>
    public string? RequiredOptionKey => MachineOptionKeys.WheelDresser;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(
            StepParameterKeys.WheelSurfaceSpeedMPerSec, ParameterUnit.MeterPerSecond, 30.0, 5.0, 80.0),
        ParameterDescriptor.Number(
            StepParameterKeys.DressInfeedRadiusMicrometer, ParameterUnit.Micrometer, 20.0, 1.0, 200.0),
        ParameterDescriptor.Number(
            StepParameterKeys.DressPassCount, ParameterUnit.Count, 2.0, 1.0, 20.0),
        ParameterDescriptor.Number(
            StepParameterKeys.DressFeedMmPerMin, ParameterUnit.MillimeterPerMinute, 200.0, 1.0, 3000.0),
    });

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        ParameterSet values = Schema.ApplyDefaults(parameters);

        return new GrindingStepPlan(
            Key,
            (int)values.GetNumber(StepParameterKeys.DressPassCount),
            InfeedPerPassRadiusMm: 0.0,
            values.GetNumber(StepParameterKeys.DressFeedMmPerMin),
            WorkpieceSpeedRpm: 0.0,
            WheelSpeedRpm: 0.0,
            SparkOutPassCount: 0,
            RequiresMeasurement: false)
        {
            // 修整不动轧辊，对辊件没有去除量。
            WheelSurfaceSpeedMPerSec = values.GetNumber(StepParameterKeys.WheelSurfaceSpeedMPerSec),
        };
    }
}

/// <summary>辊形测量：不磨削，沿辊身走一遍取测量数据。</summary>
public sealed class MeasureStepType : IGrindingStepType
{
    public string Key => StepTypeKeys.Measure;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(
            StepParameterKeys.MeasurePointCount, ParameterUnit.Count, 21.0, 3.0, 201.0),
        ParameterDescriptor.Number(
            StepParameterKeys.FeedMmPerMin, ParameterUnit.MillimeterPerMinute, 1500.0, 1.0, 20000.0),
        ParameterDescriptor.Number(
            StepParameterKeys.WorkpieceSpeedRpm, ParameterUnit.RevolutionsPerMinute, 10.0, 0.1, 500.0),
    });

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        ParameterSet values = Schema.ApplyDefaults(parameters);

        return new GrindingStepPlan(
            Key,
            PassCount: 1,
            InfeedPerPassRadiusMm: 0.0,
            values.GetNumber(StepParameterKeys.FeedMmPerMin),
            values.GetNumber(StepParameterKeys.WorkpieceSpeedRpm),
            WheelSpeedRpm: 0.0,
            SparkOutPassCount: 0,
            RequiresMeasurement: true)
        {
            // 测量时转速必须稳，不能变速。
            SpeedVariation = SpeedVariation.Off,
        };
    }
}

/// <summary>
/// 倒角：磨辊身两端的过渡角。宽度沿辊身方向，角度相对辊身轴线。
/// </summary>
public sealed class ChamferStepType : IGrindingStepType
{
    public string Key => StepTypeKeys.Chamfer;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(
            StepParameterKeys.ChamferWidthMm, ParameterUnit.Millimeter, 5.0, 0.5, 100.0),
        ParameterDescriptor.Number(
            StepParameterKeys.ChamferAngleDegree, ParameterUnit.Degree, 30.0, 1.0, 89.0),
        ParameterDescriptor.Number(
            StepParameterKeys.WheelSurfaceSpeedMPerSec, ParameterUnit.MeterPerSecond, 28.0, 5.0, 80.0),
        ParameterDescriptor.Number(
            StepParameterKeys.WorkpieceSpeedRpm, ParameterUnit.RevolutionsPerMinute, 15.0, 0.1, 500.0),
        ParameterDescriptor.Number(
            StepParameterKeys.FeedMmPerMin, ParameterUnit.MillimeterPerMinute, 200.0, 1.0, 3000.0),
        ParameterDescriptor.Number(
            StepParameterKeys.PassCount, ParameterUnit.Count, 2.0, 1.0, 20.0),
    });

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        ParameterSet values = Schema.ApplyDefaults(parameters);

        return new GrindingStepPlan(
            Key,
            (int)values.GetNumber(StepParameterKeys.PassCount),
            InfeedPerPassRadiusMm: 0.0,
            values.GetNumber(StepParameterKeys.FeedMmPerMin),
            values.GetNumber(StepParameterKeys.WorkpieceSpeedRpm),
            WheelSpeedRpm: 0.0,
            SparkOutPassCount: 0,
            RequiresMeasurement: false)
        {
            // 倒角走的是端部轮廓，不是辊身的径向去除量。
            WheelSurfaceSpeedMPerSec = values.GetNumber(StepParameterKeys.WheelSurfaceSpeedMPerSec),
        };
    }
}

/// <summary>
/// 涡流探伤：探头沿辊身螺旋扫查，查表面与近表面裂纹。不磨削。
/// 扫查螺距（mm/转）与头架转速一起决定拖板速度：拖板 = 螺距 × 转速。
/// </summary>
public sealed class EddyCurrentStepType : IGrindingStepType
{
    public string Key => StepTypeKeys.EddyCurrent;

    /// <summary>没有探伤器就别把这道工序编进程序。</summary>
    public string? RequiredOptionKey => MachineOptionKeys.EddyCurrentTester;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(
            StepParameterKeys.ScanPitchMm, ParameterUnit.Millimeter, 3.0, 0.1, 50.0),
        ParameterDescriptor.Number(
            StepParameterKeys.WorkpieceSpeedRpm, ParameterUnit.RevolutionsPerMinute, 30.0, 0.1, 500.0),
    });

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        ParameterSet values = Schema.ApplyDefaults(parameters);

        double workpieceSpeedRpm = values.GetNumber(StepParameterKeys.WorkpieceSpeedRpm);
        double scanPitchMm = values.GetNumber(StepParameterKeys.ScanPitchMm);

        return new GrindingStepPlan(
            Key,
            PassCount: 1,
            InfeedPerPassRadiusMm: 0.0,

            // 拖板速度由螺距与转速导出，不让操作员分别设两个再互相打架。
            FeedMmPerMin: scanPitchMm * workpieceSpeedRpm,
            workpieceSpeedRpm,
            WheelSpeedRpm: 0.0,
            SparkOutPassCount: 0,
            RequiresMeasurement: false)
        {
            SpeedVariation = SpeedVariation.Off,
        };
    }
}

/// <summary>无火花光磨：只走行程不进给，消除弹性变形。</summary>
public sealed class SparkOutStepType : IGrindingStepType
{
    public string Key => StepTypeKeys.SparkOut;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(
            StepParameterKeys.PassCount, ParameterUnit.Count, 3.0, 1.0, 20.0),
        ParameterDescriptor.Number(
            StepParameterKeys.FeedMmPerMin, ParameterUnit.MillimeterPerMinute, 600.0, 1.0, 20000.0),
        ParameterDescriptor.Number(
            StepParameterKeys.WorkpieceSpeedRpm, ParameterUnit.RevolutionsPerMinute, 20.0, 0.1, 500.0),
        ParameterDescriptor.Number(
            StepParameterKeys.WheelSurfaceSpeedMPerSec, ParameterUnit.MeterPerSecond, 28.0, 5.0, 80.0),
        ParameterDescriptor.Number(
            StepParameterKeys.ReversalDwellSeconds, ParameterUnit.Second, 2.0, 0.0, 60.0),
    });

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        ParameterSet values = Schema.ApplyDefaults(parameters);

        return new GrindingStepPlan(
            Key,
            (int)values.GetNumber(StepParameterKeys.PassCount),
            InfeedPerPassRadiusMm: 0.0,
            values.GetNumber(StepParameterKeys.FeedMmPerMin),
            values.GetNumber(StepParameterKeys.WorkpieceSpeedRpm),
            WheelSpeedRpm: 0.0,
            SparkOutPassCount: 0,
            RequiresMeasurement: false)
        {
            // 名副其实：不进给。
            WheelSurfaceSpeedMPerSec = values.GetNumber(StepParameterKeys.WheelSurfaceSpeedMPerSec),
            ReversalDwellSeconds = values.GetNumber(StepParameterKeys.ReversalDwellSeconds),
        };
    }
}

/// <summary>
/// 圆度测量：在若干个辊身截面上各绕一圈取点，算圆度与偏心度。
///
/// 与辊形测量分开是有道理的——辊形沿轴线扫，圆度绕圆周扫，两件事。
/// 每转的取点数默认 36（每 10°一点），相位以头架圆周分度脉冲为基准
/// （MK84160 原理图 I37.1），磨前磨后两次测量才叠得到同一个相位上比。
/// </summary>
public sealed class RoundnessStepType : IGrindingStepType
{
    public string Key => StepTypeKeys.Roundness;

    public string? RequiredOptionKey => null;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(
            StepParameterKeys.RoundnessSectionCount, ParameterUnit.Count, 5.0, 1.0, 51.0),
        ParameterDescriptor.Number(
            StepParameterKeys.RoundnessPointsPerRevolution, ParameterUnit.Count, 36.0, 8.0, 360.0),
        ParameterDescriptor.Number(
            StepParameterKeys.WorkpieceSpeedRpm, ParameterUnit.RevolutionsPerMinute, 8.0, 0.1, 500.0),
    });

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        ParameterSet values = Schema.ApplyDefaults(parameters);

        return new GrindingStepPlan(
            Key,
            PassCount: 1,
            InfeedPerPassRadiusMm: 0.0,

            // 圆度是停在一个截面上绕圈测，拖板不走。
            FeedMmPerMin: 0.0,
            values.GetNumber(StepParameterKeys.WorkpieceSpeedRpm),
            WheelSpeedRpm: 0.0,
            SparkOutPassCount: 0,
            RequiresMeasurement: true)
        {
            // 测量时转速必须稳，不能变速——转速一摆，圆度就测不准了。
            SpeedVariation = SpeedVariation.Off,
        };
    }
}

/// <summary>
/// 暂停：磨到这一步停下来等人。换砂轮、量个尺寸、请人来看一眼都用它。
///
/// 不是报警，也不是结束——按继续就往下走。只有一个"为什么停"，
/// 让自动页把原因写在提示上；没有"停多久"，加了反而让人以为到点会自己走。
/// </summary>
public sealed class PauseStepType : IGrindingStepType
{
    public string Key => StepTypeKeys.Pause;

    public string? RequiredOptionKey => null;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Choice(
            StepParameterKeys.PauseReason,
            new[]
            {
                PauseReasonChoices.WheelChange,
                PauseReasonChoices.ManualMeasure,
                PauseReasonChoices.Inspect,
                PauseReasonChoices.Other,
            },
            PauseReasonChoices.Other),
    });

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        return new GrindingStepPlan(
            Key,
            PassCount: 0,
            InfeedPerPassRadiusMm: 0.0,
            FeedMmPerMin: 0.0,
            WorkpieceSpeedRpm: 0.0,
            WheelSpeedRpm: 0.0,
            SparkOutPassCount: 0,
            RequiresMeasurement: false);
    }
}
