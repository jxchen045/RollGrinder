using RollGrinder.Core.Units;

namespace RollGrinder.Core.Geometry;

/// <summary>
/// 辊件几何。内部一律半径量（mm）；辊身坐标 0 在操作侧端面，向传动侧增大。
/// </summary>
/// <param name="BodyLengthMm">辊身长度（mm）。</param>
/// <param name="NominalRadiusMm">公称半径（mm）。</param>
public sealed record RollGeometry(double BodyLengthMm, double NominalRadiusMm)
{
    /// <summary>从界面量（辊身长度 mm + 直径 mm）构造。</summary>
    public static RollGeometry FromDiameter(double bodyLengthMm, double nominalDiameterMm) =>
        Create(bodyLengthMm, UnitConversion.DiameterMmToRadiusMm(nominalDiameterMm));

    /// <summary>构造并校验。</summary>
    public static RollGeometry Create(double bodyLengthMm, double nominalRadiusMm)
    {
        if (bodyLengthMm <= 0.0)
        {
            throw new DomainException("Roll body length must be positive.");
        }

        if (nominalRadiusMm <= 0.0)
        {
            throw new DomainException("Roll radius must be positive.");
        }

        return new RollGeometry(bodyLengthMm, nominalRadiusMm);
    }

    /// <summary>公称直径（mm），界面量。</summary>
    public double NominalDiameterMm => UnitConversion.RadiusMmToDiameterMm(NominalRadiusMm);

    /// <summary>把 [0,1] 的归一化辊身位置换成 mm。</summary>
    public double BodyPositionMmAt(double normalizedPosition) => normalizedPosition * BodyLengthMm;
}
