namespace RollGrinder.Core.Geometry;

/// <summary>
/// 辊形上的一点：辊身坐标 + 相对公称半径的偏差（半径量 mm，正为大）。
/// </summary>
/// <param name="BodyPositionMm">辊身坐标（mm）。</param>
/// <param name="RadiusOffsetMm">半径偏差（mm）。</param>
public sealed record ProfilePoint(double BodyPositionMm, double RadiusOffsetMm);
