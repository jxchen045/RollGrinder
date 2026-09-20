using System;
using System.Collections.Generic;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 领域层看到的机床能力，全部是半径量与 mm/min、r/min。
/// 取值来自 machine.json，由上层映射进来——领域层不认识配置文件，也不内置任何阈值。
/// </summary>
/// <param name="MaxInfeedPerPassRadiusMm">单刀最大切深（半径量 mm）。</param>
/// <param name="MaxFeedMmPerMin">最大轴向进给（mm/min）。</param>
/// <param name="MaxWorkpieceSpeedRpm">工件最大转速（r/min）。</param>
/// <param name="MaxWheelSpeedRpm">砂轮最大转速（r/min）。</param>
/// <param name="MinBodyLengthMm">最小辊身长度（mm）。</param>
/// <param name="MaxBodyLengthMm">最大辊身长度（mm）。</param>
/// <param name="MinRadiusMm">最小半径（mm）。</param>
/// <param name="MaxRadiusMm">最大半径（mm）。</param>
public sealed record MachineCapability(
    double MaxInfeedPerPassRadiusMm,
    double MaxFeedMmPerMin,
    double MaxWorkpieceSpeedRpm,
    double MaxWheelSpeedRpm,
    double MinBodyLengthMm,
    double MaxBodyLengthMm,
    double MinRadiusMm,
    double MaxRadiusMm)
{
    /// <summary>
    /// 砂轮线速度下限（m/s）。为 null 表示 machine.json 没给这项，
    /// 校验就跳过它——不猜一个机床数字出来。
    /// </summary>
    public double? MinWheelSurfaceSpeedMPerSec { get; init; }

    /// <summary>砂轮线速度上限（m/s）。为 null 表示未配置。</summary>
    public double? MaxWheelSurfaceSpeedMPerSec { get; init; }

    /// <summary>
    /// 本台机床装有的选装装置（machine.json 的 options 里取值为 true 的那些键）。
    /// 工序类型按 <see cref="IGrindingStepType.RequiredOptionKey"/> 对着它查。
    /// </summary>
    public IReadOnlySet<string> InstalledOptions { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 本台机床装有的测量量（machine.json 的 measurementChannels 里 isPresent 的那些 quantity）。
    /// 要测量的工序对着它查——没有测头就别编"磨后测量"。
    /// </summary>
    public IReadOnlySet<string> AvailableMeasurements { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>这台机床有没有直径测量通道。</summary>
    public bool CanMeasureDiameter => AvailableMeasurements.Contains(MeasurementQuantities.Diameter);

    /// <summary>这台机床能不能做某道工序（按选装装置判断）。</summary>
    public bool Supports(IGrindingStepType stepType)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        return stepType.RequiredOptionKey is not string option || InstalledOptions.Contains(option);
    }

    /// <summary>单刀最大切深的直径量微米表示，供界面提示用。</summary>
    public double MaxInfeedPerPassDiameterMicrometer =>
        UnitConversion.RadiusMmToDiameterMicrometer(MaxInfeedPerPassRadiusMm);
}
