using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// 锥度辊形：从操作侧到传动侧线性变化。
/// taperDiameterMicrometer 为传动侧相对操作侧的直径量差值（µm，正为传动侧大）。
/// </summary>
public sealed class TaperProfileType : IRollProfileType
{
    public const string TaperDiameterMicrometerKey = "taperDiameterMicrometer";

    public string Key => ProfileTypeKeys.Taper;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(TaperDiameterMicrometerKey, ParameterUnit.Micrometer, 0.0, -2000.0, 2000.0),
    });

    public RollProfile CreateProfile(RollGeometry geometry, ParameterSet parameters, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        double taperRadiusMm = UnitConversion.DiameterMicrometerToRadiusMm(
            parameters.GetNumber(TaperDiameterMicrometerKey));

        return RollProfile.Sample(
            geometry.BodyLengthMm,
            sampleCount,
            bodyPositionMm => taperRadiusMm * (bodyPositionMm / geometry.BodyLengthMm));
    }
}
