using System;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// 锥度辊形。
/// <list type="bullet">
///   <item>顺接辊形（现行）：端部减薄。taperDiameterMicrometer 是端面处比相邻段低多少（直径量 µm，正为减薄），
///         线性回到交界处的 0；朝头架还是尾架端面由这段所在的半边定，见 <see cref="CreateEndRelief"/>。</item>
///   <item>叠加辊形（旧格式）：从段起点到终点线性变化，taperDiameterMicrometer 为终点相对起点的直径量差值
///         （正为终点大）。只为读旧作业快照与记录保留。</item>
/// </list>
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

    /// <inheritdoc />
    public bool IsEndRelief => true;

    /// <summary>
    /// 顺接辊形里的锥度 = 端部减薄：参数为正时端面比相邻段低这么多（直径量），线性回到交界处的 0。
    /// 段内坐标的起点就是端面；朝哪一端由合成辊形按位置定。
    /// </summary>
    public RollProfile CreateEndRelief(RollGeometry segmentGeometry, ParameterSet parameters, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(segmentGeometry);
        ArgumentNullException.ThrowIfNull(parameters);

        double reliefRadiusMm = UnitConversion.DiameterMicrometerToRadiusMm(
            parameters.GetNumber(TaperDiameterMicrometerKey));

        return RollProfile.Sample(
            segmentGeometry.BodyLengthMm,
            sampleCount,
            positionMm => -reliefRadiusMm * (1.0 - (positionMm / segmentGeometry.BodyLengthMm)));
    }
}
