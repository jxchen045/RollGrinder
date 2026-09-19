using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// 抛物线凸度辊形：两端为零，中点为 crownDiameterMicrometer（直径量 µm，正为凸、负为凹）。
/// </summary>
public sealed class CrownProfileType : IRollProfileType
{
    public const string CrownDiameterMicrometerKey = "crownDiameterMicrometer";

    public string Key => ProfileTypeKeys.Crown;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(CrownDiameterMicrometerKey, ParameterUnit.Micrometer, 0.0, -2000.0, 2000.0),
    });

    public RollProfile CreateProfile(RollGeometry geometry, ParameterSet parameters, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        double crownRadiusMm = UnitConversion.DiameterMicrometerToRadiusMm(
            parameters.GetNumber(CrownDiameterMicrometerKey));

        return RollProfile.Sample(geometry.BodyLengthMm, sampleCount, bodyPositionMm =>
        {
            // u ∈ [-1, 1]，抛物线在两端为 0、中点为 1。
            double u = (2.0 * bodyPositionMm / geometry.BodyLengthMm) - 1.0;
            return crownRadiusMm * (1.0 - (u * u));
        });
    }
}
