using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Profiles;

/// <summary>圆柱辊形：全长零偏差。</summary>
public sealed class CylindricalProfileType : IRollProfileType
{
    public string Key => ProfileTypeKeys.Cylindrical;

    public bool IsSelfSymmetric => true;

    public ParameterSchema Schema => ParameterSchema.Empty;

    public RollProfile CreateProfile(RollGeometry geometry, ParameterSet parameters, int sampleCount)
    {
        System.ArgumentNullException.ThrowIfNull(geometry);
        return RollProfile.Sample(geometry.BodyLengthMm, sampleCount, _ => 0.0);
    }
}
