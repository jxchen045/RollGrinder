namespace RollGrinder.Core.Units;

/// <summary>
/// 内部计算一律用半径量（mm）与辊身坐标；界面按直径量与微米显示。
/// 这里是全工程唯一的换算点，其他地方不得再写 *2、/2、*1000。
/// </summary>
public static class UnitConversion
{
    private const double MicrometerPerMillimeter = 1000.0;
    private const double DiameterPerRadius = 2.0;

    /// <summary>半径量（mm）→ 直径量（mm）。</summary>
    public static double RadiusMmToDiameterMm(double radiusMm) => radiusMm * DiameterPerRadius;

    /// <summary>直径量（mm）→ 半径量（mm）。</summary>
    public static double DiameterMmToRadiusMm(double diameterMm) => diameterMm / DiameterPerRadius;

    /// <summary>毫米 → 微米。</summary>
    public static double MmToMicrometer(double lengthMm) => lengthMm * MicrometerPerMillimeter;

    /// <summary>微米 → 毫米。</summary>
    public static double MicrometerToMm(double lengthMicrometer) => lengthMicrometer / MicrometerPerMillimeter;

    /// <summary>半径量（mm）→ 直径量（µm）：界面显示辊形偏差用的组合换算。</summary>
    public static double RadiusMmToDiameterMicrometer(double radiusMm) =>
        MmToMicrometer(RadiusMmToDiameterMm(radiusMm));

    /// <summary>直径量（µm）→ 半径量（mm）：界面输入辊形参数用的组合换算。</summary>
    public static double DiameterMicrometerToRadiusMm(double diameterMicrometer) =>
        DiameterMmToRadiusMm(MicrometerToMm(diameterMicrometer));

    /// <summary>
    /// 直径量速率（µm/min）→ 半径量速率（mm/min）：连续进给用。
    /// 换算因子与长度一致，单独起个名字是为了让调用处读起来不含糊。
    /// </summary>
    public static double DiameterMicrometerPerMinToRadiusMmPerMin(double diameterMicrometerPerMin) =>
        DiameterMicrometerToRadiusMm(diameterMicrometerPerMin);
}
