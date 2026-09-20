namespace RollGrinder.Core.Steps;

/// <summary>
/// 短行程（自适应磨削）：先在辊面两端各 1/3 处小范围往复，把辊面走顺、
/// 让砂轮与辊面全面接触，两端磨到设定道次后转入全面磨削。
/// 切除量小、进给慢，不做在线测量；只用连续分量，周期分量默认 0。
/// </summary>
public sealed class ShortStrokeStepType : TraverseGrindingStepType
{
    public ShortStrokeStepType()
        : base(StepTypeKeys.ShortStroke, new TraverseStepDefaults
        {
            WheelSurfaceSpeedMPerSec = 30.0,
            WorkpieceSpeedRpm = 30.0,
            FeedMmPerMin = 800.0,
            ContinuousInfeedDiameterMicrometerPerMin = 20.0,
            MaxContinuousInfeedDiameterMicrometerPerMin = 100.0,
            InfeedPerPassDiameterMicrometer = 0.0,
            MaxInfeedPerPassDiameterMicrometer = 40.0,
            PassCount = 4.0,
            StockDiameterMicrometer = 60.0,
            ReversalDwellSeconds = 1.0,
            SparkOutPassCount = 0.0,
            InProcessMeasurement = false,
            SpeedVariationTarget = SpeedVariationChoices.Workpiece,
            SpeedVariationPercent = 8.0,
            SpeedVariationPeriodSeconds = 5.0,
            RequiresMeasurement = false,
        })
    {
    }
}

/// <summary>
/// 粗磨：去除约八成余量。默认值取自 MGK84160 操作说明书的磨削实例（粗磨一列）——
/// 连续 0.05 mm/min 求切除率，另叠一路 0.005 mm/道次 的周期分量做分层。
/// 辊面留下的螺旋由后面的工序磨掉。
/// </summary>
public sealed class RoughGrindingStepType : TraverseGrindingStepType
{
    public RoughGrindingStepType()
        : base(StepTypeKeys.Rough, new TraverseStepDefaults
        {
            WheelSurfaceSpeedMPerSec = 40.0,
            WorkpieceSpeedRpm = 35.0,
            FeedMmPerMin = 2300.0,
            ContinuousInfeedDiameterMicrometerPerMin = 50.0,
            MaxContinuousInfeedDiameterMicrometerPerMin = 200.0,
            InfeedPerPassDiameterMicrometer = 5.0,
            MaxInfeedPerPassDiameterMicrometer = 200.0,
            PassCount = 10.0,
            StockDiameterMicrometer = 600.0,
            ReversalDwellSeconds = 1.0,
            SparkOutPassCount = 0.0,
            InProcessMeasurement = false,
            SpeedVariationTarget = SpeedVariationChoices.Workpiece,
            SpeedVariationPercent = 8.0,
            SpeedVariationPeriodSeconds = 5.0,
            RequiresMeasurement = false,
        })
    {
    }
}

/// <summary>
/// 半精磨：两路分量都收小，开始把辊形收住。默认值取自 MGK84160 操作说明书的
/// 磨削实例（半粗磨一列）——连续与周期都是 0.002 mm 量级。
/// </summary>
public sealed class SemiFinishGrindingStepType : TraverseGrindingStepType
{
    public SemiFinishGrindingStepType()
        : base(StepTypeKeys.SemiFinish, new TraverseStepDefaults
        {
            WheelSurfaceSpeedMPerSec = 40.0,
            WorkpieceSpeedRpm = 38.0,
            FeedMmPerMin = 1200.0,
            ContinuousInfeedDiameterMicrometerPerMin = 2.0,
            MaxContinuousInfeedDiameterMicrometerPerMin = 60.0,
            InfeedPerPassDiameterMicrometer = 2.0,
            MaxInfeedPerPassDiameterMicrometer = 40.0,
            PassCount = 4.0,
            StockDiameterMicrometer = 40.0,
            ReversalDwellSeconds = 1.0,
            SparkOutPassCount = 1.0,
            InProcessMeasurement = true,
            SpeedVariationTarget = SpeedVariationChoices.Workpiece,
            SpeedVariationPercent = 8.0,
            SpeedVariationPeriodSeconds = 5.0,
            RequiresMeasurement = false,
        })
    {
    }
}

/// <summary>
/// 精磨：只留周期分量（连续置 0），每道次等深，带光磨道次，结束后测量。
/// 默认值取自 MGK84160 操作说明书的磨削实例（中磨一列）。
/// 变速幅度收小、周期放长——这一段既要压颤振，又不能让转速摆动本身留下痕迹。
/// </summary>
public sealed class FinishGrindingStepType : TraverseGrindingStepType
{
    public FinishGrindingStepType()
        : base(StepTypeKeys.Finish, new TraverseStepDefaults
        {
            WheelSurfaceSpeedMPerSec = 35.0,
            WorkpieceSpeedRpm = 40.0,
            FeedMmPerMin = 800.0,
            ContinuousInfeedDiameterMicrometerPerMin = 0.0,
            MaxContinuousInfeedDiameterMicrometerPerMin = 30.0,
            InfeedPerPassDiameterMicrometer = 2.0,
            MaxInfeedPerPassDiameterMicrometer = 40.0,
            PassCount = 6.0,
            StockDiameterMicrometer = 12.0,
            ReversalDwellSeconds = 2.0,
            SparkOutPassCount = 2.0,
            InProcessMeasurement = true,
            SpeedVariationTarget = SpeedVariationChoices.Workpiece,
            SpeedVariationPercent = 5.0,
            MaxSpeedVariationPercent = 10.0,
            SpeedVariationPeriodSeconds = 8.0,
            RequiresMeasurement = true,
        })
    {
    }
}

/// <summary>
/// 抛光：不再去量，只把粗糙度降下来并磨匀。默认进给为 0，靠光磨道次走。
/// </summary>
public sealed class PolishStepType : TraverseGrindingStepType
{
    public PolishStepType()
        : base(StepTypeKeys.Polish, new TraverseStepDefaults
        {
            WheelSurfaceSpeedMPerSec = 35.0,
            WorkpieceSpeedRpm = 40.0,
            FeedMmPerMin = 400.0,
            ContinuousInfeedDiameterMicrometerPerMin = 0.0,
            MaxContinuousInfeedDiameterMicrometerPerMin = 10.0,
            InfeedPerPassDiameterMicrometer = 0.0,
            MaxInfeedPerPassDiameterMicrometer = 5.0,
            PassCount = 4.0,
            StockDiameterMicrometer = 0.0,
            ReversalDwellSeconds = 2.0,
            SparkOutPassCount = 3.0,
            InProcessMeasurement = false,
            SpeedVariationTarget = SpeedVariationChoices.Workpiece,
            SpeedVariationPercent = 5.0,
            MaxSpeedVariationPercent = 10.0,
            SpeedVariationPeriodSeconds = 8.0,
            RequiresMeasurement = false,
        })
    {
    }
}
