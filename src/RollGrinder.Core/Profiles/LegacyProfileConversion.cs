using System;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// 旧的叠加辊形转成顺接辊形（阶段 1 的决定）：按原来的合成曲线采样，变成铺满设计长度的一段点表，
/// 用折线插值——采样点就是原来下发给 NC 的那些点，点与点之间原来也是按直线走，所以形状完全不变。
/// 代价是原来凸度、锥度这些参数不能再分开改。
///
/// 只在辊形编辑器打开旧库条目时用；作业快照和磨削记录里的旧辊形照旧按叠加求值，不转。
/// </summary>
public static class LegacyProfileConversion
{
    public static CompositeRollProfile ToPointTable(
        CompositeRollProfile legacy, RollGeometry geometry, RollProfileTypeRegistry registry, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(registry);

        if (legacy.Layout != ProfileLayout.Superimposed)
        {
            return legacy;
        }

        RollProfile composed = legacy.Compose(geometry, registry, sampleCount);
        TablePoint[] points = composed.Points
            .Select(point => new TablePoint(point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
            .ToArray();

        ParameterSet parameters = PointTableProfileType.DefaultsFor(geometry.BodyLengthMm)
            .With(PointTableProfileType.PointsKey, ParameterValue.FromPoints(points))
            .With(PointTableProfileType.InterpolationKey, ParameterValue.FromChoice(nameof(InterpolationMethod.Linear)));

        return CompositeRollProfile.Sequential(
            0.0, new[] { new SequentialSegment(ProfileTypeKeys.PointTable, geometry.BodyLengthMm, parameters) });
    }
}
