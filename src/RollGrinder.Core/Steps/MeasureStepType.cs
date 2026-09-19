using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Steps;

/// <summary>测量工序：不磨削，只沿辊身走一遍取测量数据。</summary>
public sealed class MeasureStepType : IGrindingStepType
{
    /// <summary>沿辊身的测点数。</summary>
    public const string MeasurePointCountKey = "measurePointCount";

    public string Key => StepTypeKeys.Measure;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(MeasurePointCountKey, ParameterUnit.Count, 21.0, 3.0, 201.0),
        ParameterDescriptor.Number(StepParameterKeys.FeedMmPerMin, ParameterUnit.MillimeterPerMinute, 1500.0, 1.0, 20000.0),
        ParameterDescriptor.Number(StepParameterKeys.WorkpieceSpeedRpm, ParameterUnit.RevolutionsPerMinute, 10.0, 0.1, 500.0),
    });

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        return new GrindingStepPlan(
            Key,
            PassCount: 1,
            InfeedPerPassRadiusMm: 0.0,
            parameters.GetNumber(StepParameterKeys.FeedMmPerMin),
            parameters.GetNumber(StepParameterKeys.WorkpieceSpeedRpm),
            WheelSpeedRpm: 0.0,
            SparkOutPassCount: 0,
            RequiresMeasurement: true);
    }
}
