using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 以"总去除量 + 每刀切深"描述的工序基类：粗磨与精磨只是默认值与限值不同。
/// </summary>
public abstract class StockRemovalStepType : IGrindingStepType
{
    protected StockRemovalStepType(
        string key,
        double defaultStockDiameterMicrometer,
        double defaultInfeedPerPassDiameterMicrometer,
        double maxInfeedPerPassDiameterMicrometer,
        double defaultFeedMmPerMin,
        int defaultSparkOutPassCount,
        bool requiresMeasurement)
    {
        Key = key;
        Schema = new ParameterSchema(new[]
        {
            ParameterDescriptor.Number(
                StepParameterKeys.StockDiameterMicrometer,
                ParameterUnit.Micrometer,
                defaultStockDiameterMicrometer,
                0.0,
                5000.0),
            ParameterDescriptor.Number(
                StepParameterKeys.InfeedPerPassDiameterMicrometer,
                ParameterUnit.Micrometer,
                defaultInfeedPerPassDiameterMicrometer,
                1.0,
                maxInfeedPerPassDiameterMicrometer),
            ParameterDescriptor.Number(
                StepParameterKeys.FeedMmPerMin,
                ParameterUnit.MillimeterPerMinute,
                defaultFeedMmPerMin,
                1.0,
                20000.0),
            ParameterDescriptor.Number(
                StepParameterKeys.WorkpieceSpeedRpm,
                ParameterUnit.RevolutionsPerMinute,
                20.0,
                0.1,
                500.0),
            ParameterDescriptor.Number(
                StepParameterKeys.WheelSpeedRpm,
                ParameterUnit.RevolutionsPerMinute,
                900.0,
                0.0,
                3000.0),
            ParameterDescriptor.Number(
                StepParameterKeys.SparkOutPassCount,
                ParameterUnit.Count,
                defaultSparkOutPassCount,
                0.0,
                20.0),
        });
        RequiresMeasurement = requiresMeasurement;
    }

    public string Key { get; }

    public ParameterSchema Schema { get; }

    protected bool RequiresMeasurement { get; }

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        double stockRadiusMm = UnitConversion.DiameterMicrometerToRadiusMm(
            parameters.GetNumber(StepParameterKeys.StockDiameterMicrometer));
        double infeedPerPassRadiusMm = UnitConversion.DiameterMicrometerToRadiusMm(
            parameters.GetNumber(StepParameterKeys.InfeedPerPassDiameterMicrometer));

        if (infeedPerPassRadiusMm <= 0.0)
        {
            throw new DomainException($"Step '{Key}' needs a positive infeed per pass.");
        }

        int passCount = (int)Math.Ceiling(stockRadiusMm / infeedPerPassRadiusMm);
        if (passCount < 1)
        {
            // 去除量为零时仍走一刀，以便工序序列保持完整。
            passCount = 1;
            infeedPerPassRadiusMm = 0.0;
        }
        else
        {
            // 最后一刀不超量：把总量平均到各刀上。
            infeedPerPassRadiusMm = stockRadiusMm / passCount;
        }

        return new GrindingStepPlan(
            Key,
            passCount,
            infeedPerPassRadiusMm,
            parameters.GetNumber(StepParameterKeys.FeedMmPerMin),
            parameters.GetNumber(StepParameterKeys.WorkpieceSpeedRpm),
            parameters.GetNumber(StepParameterKeys.WheelSpeedRpm),
            (int)parameters.GetNumber(StepParameterKeys.SparkOutPassCount),
            RequiresMeasurement);
    }
}

/// <summary>粗磨：去除量大、每刀深、进给快。</summary>
public sealed class RoughGrindingStepType : StockRemovalStepType
{
    public RoughGrindingStepType()
        : base(
            StepTypeKeys.Rough,
            defaultStockDiameterMicrometer: 600.0,
            defaultInfeedPerPassDiameterMicrometer: 60.0,
            maxInfeedPerPassDiameterMicrometer: 200.0,
            defaultFeedMmPerMin: 2500.0,
            defaultSparkOutPassCount: 0,
            requiresMeasurement: false)
    {
    }
}

/// <summary>精磨：去除量小、每刀浅、进给慢，结束后测量。</summary>
public sealed class FinishGrindingStepType : StockRemovalStepType
{
    public FinishGrindingStepType()
        : base(
            StepTypeKeys.Finish,
            defaultStockDiameterMicrometer: 60.0,
            defaultInfeedPerPassDiameterMicrometer: 10.0,
            maxInfeedPerPassDiameterMicrometer: 40.0,
            defaultFeedMmPerMin: 800.0,
            defaultSparkOutPassCount: 2,
            requiresMeasurement: true)
    {
    }
}
