using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Steps;

/// <summary>光磨：不进刀，只走刀消除弹性变形。</summary>
public sealed class SparkOutStepType : IGrindingStepType
{
    public string Key => StepTypeKeys.SparkOut;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(StepParameterKeys.PassCount, ParameterUnit.Count, 3.0, 1.0, 20.0),
        ParameterDescriptor.Number(StepParameterKeys.FeedMmPerMin, ParameterUnit.MillimeterPerMinute, 600.0, 1.0, 20000.0),
        ParameterDescriptor.Number(StepParameterKeys.WorkpieceSpeedRpm, ParameterUnit.RevolutionsPerMinute, 20.0, 0.1, 500.0),
        ParameterDescriptor.Number(StepParameterKeys.WheelSpeedRpm, ParameterUnit.RevolutionsPerMinute, 900.0, 0.0, 3000.0),
    });

    public GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        return new GrindingStepPlan(
            Key,
            (int)parameters.GetNumber(StepParameterKeys.PassCount),
            InfeedPerPassRadiusMm: 0.0,
            parameters.GetNumber(StepParameterKeys.FeedMmPerMin),
            parameters.GetNumber(StepParameterKeys.WorkpieceSpeedRpm),
            parameters.GetNumber(StepParameterKeys.WheelSpeedRpm),
            SparkOutPassCount: 0,
            RequiresMeasurement: false);
    }
}
