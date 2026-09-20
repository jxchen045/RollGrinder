namespace RollGrinder.Core.Steps;

/// <summary>
/// 短行程：小范围往复，把辊面走顺、让砂轮与辊面全面接触，为粗磨铺路。
/// 切除量小、进给慢，不做在线测量。
/// </summary>
public sealed class ShortStrokeStepType : TraverseGrindingStepType
{
    public ShortStrokeStepType()
        : base(StepTypeKeys.ShortStroke, new TraverseStepDefaults
        {
            WheelSurfaceSpeedMPerSec = 30.0,
            WorkpieceSpeedRpm = 30.0,
            FeedMmPerMin = 800.0,
            FeedMode = FeedModeChoices.Continuous,
            ContinuousInfeedDiameterMicrometerPerMin = 20.0,
            MaxContinuousInfeedDiameterMicrometerPerMin = 100.0,
            InfeedPerPassDiameterMicrometer = 10.0,
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
/// 粗磨：去除约八成余量。连续进给求切除率，辊面留下的螺旋由后面的工序磨掉。
/// </summary>
public sealed class RoughGrindingStepType : TraverseGrindingStepType
{
    public RoughGrindingStepType()
        : base(StepTypeKeys.Rough, new TraverseStepDefaults
        {
            WheelSurfaceSpeedMPerSec = 32.0,
            WorkpieceSpeedRpm = 30.0,
            FeedMmPerMin = 2500.0,
            FeedMode = FeedModeChoices.Continuous,
            ContinuousInfeedDiameterMicrometerPerMin = 40.0,
            MaxContinuousInfeedDiameterMicrometerPerMin = 200.0,
            InfeedPerPassDiameterMicrometer = 60.0,
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
/// 半精磨：改用周期进给，每道次等深，开始把辊形收住。默认值取自设计稿
/// docs/design/B-Steps-工序编程.html 的那一屏。
/// </summary>
public sealed class SemiFinishGrindingStepType : TraverseGrindingStepType
{
    public SemiFinishGrindingStepType()
        : base(StepTypeKeys.SemiFinish, new TraverseStepDefaults
        {
            WheelSurfaceSpeedMPerSec = 30.0,
            WorkpieceSpeedRpm = 25.6,
            FeedMmPerMin = 1200.0,
            FeedMode = FeedModeChoices.PerReversal,
            ContinuousInfeedDiameterMicrometerPerMin = 5.0,
            MaxContinuousInfeedDiameterMicrometerPerMin = 60.0,
            InfeedPerPassDiameterMicrometer = 10.0,
            MaxInfeedPerPassDiameterMicrometer = 40.0,
            PassCount = 10.0,
            StockDiameterMicrometer = 100.0,
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
/// 精磨：切深最浅、带光磨道次，结束后测量。变速幅度收小、周期放长——
/// 这一段既要压颤振，又不能让转速摆动本身留下痕迹。
/// </summary>
public sealed class FinishGrindingStepType : TraverseGrindingStepType
{
    public FinishGrindingStepType()
        : base(StepTypeKeys.Finish, new TraverseStepDefaults
        {
            WheelSurfaceSpeedMPerSec = 28.0,
            WorkpieceSpeedRpm = 20.0,
            FeedMmPerMin = 800.0,
            FeedMode = FeedModeChoices.PerReversal,
            ContinuousInfeedDiameterMicrometerPerMin = 3.0,
            MaxContinuousInfeedDiameterMicrometerPerMin = 30.0,
            InfeedPerPassDiameterMicrometer = 10.0,
            MaxInfeedPerPassDiameterMicrometer = 40.0,
            PassCount = 6.0,
            StockDiameterMicrometer = 60.0,
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
            WheelSurfaceSpeedMPerSec = 25.0,
            WorkpieceSpeedRpm = 15.0,
            FeedMmPerMin = 600.0,
            FeedMode = FeedModeChoices.PerReversal,
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
