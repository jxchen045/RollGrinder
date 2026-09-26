using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// CVC 辊形：以归一化辊身坐标 u ∈ [-1, 1] 的三次多项式描述，
/// 系数按直径量微米给出，a0 为常数项（整体偏移）。
/// </summary>
public sealed class CvcProfileType : IRollProfileType
{
    public const string A0DiameterMicrometerKey = "cvcA0DiameterMicrometer";
    public const string A1DiameterMicrometerKey = "cvcA1DiameterMicrometer";
    public const string A2DiameterMicrometerKey = "cvcA2DiameterMicrometer";
    public const string A3DiameterMicrometerKey = "cvcA3DiameterMicrometer";

    public string Key => ProfileTypeKeys.Cvc;

    public bool SupportsSymmetricEditing => false;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(A0DiameterMicrometerKey, ParameterUnit.Micrometer, 0.0, -2000.0, 2000.0),
        ParameterDescriptor.Number(A1DiameterMicrometerKey, ParameterUnit.Micrometer, 0.0, -2000.0, 2000.0),
        ParameterDescriptor.Number(A2DiameterMicrometerKey, ParameterUnit.Micrometer, 0.0, -2000.0, 2000.0),
        ParameterDescriptor.Number(A3DiameterMicrometerKey, ParameterUnit.Micrometer, 0.0, -2000.0, 2000.0),
    });

    public RollProfile CreateProfile(RollGeometry geometry, ParameterSet parameters, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        double a0 = UnitConversion.DiameterMicrometerToRadiusMm(parameters.GetNumber(A0DiameterMicrometerKey));
        double a1 = UnitConversion.DiameterMicrometerToRadiusMm(parameters.GetNumber(A1DiameterMicrometerKey));
        double a2 = UnitConversion.DiameterMicrometerToRadiusMm(parameters.GetNumber(A2DiameterMicrometerKey));
        double a3 = UnitConversion.DiameterMicrometerToRadiusMm(parameters.GetNumber(A3DiameterMicrometerKey));

        return RollProfile.Sample(geometry.BodyLengthMm, sampleCount, bodyPositionMm =>
        {
            double u = (2.0 * bodyPositionMm / geometry.BodyLengthMm) - 1.0;
            return a0 + (a1 * u) + (a2 * u * u) + (a3 * u * u * u);
        });
    }
}
