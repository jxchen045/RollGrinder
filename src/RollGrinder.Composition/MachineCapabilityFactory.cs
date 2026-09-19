using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;

namespace RollGrinder.Composition;

/// <summary>
/// 把 machine.json 描述的机床能力映射成领域层认识的 <see cref="MachineCapability"/>。
/// 阈值键名只在这里出现一次；缺键时取该项的保守值，并不内置任何机床数字。
/// </summary>
public static class MachineCapabilityFactory
{
    /// <summary>单刀最大切深阈值在 machine.json 中的键。</summary>
    public const string MaxInfeedPerPassRadiusMmKey = "maxInfeedPerPassRadiusMm";

    /// <summary>进给轴的用途标识（machine.json 的 axes[].role）。</summary>
    public const string CarriageRole = "Carriage";

    /// <summary>工件主轴的用途标识。</summary>
    public const string WorkpieceSpindleRole = "WorkpieceSpindle";

    /// <summary>砂轮主轴的用途标识。</summary>
    public const string WheelSpindleRole = "WheelSpindle";

    public static MachineCapability Create(MachineDescription machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        IReadOnlyList<AxisDescription> axes = machine.Axes.Where(axis => axis.IsPresent).ToArray();

        return new MachineCapability(
            MaxInfeedPerPassRadiusMm: Threshold(machine, MaxInfeedPerPassRadiusMmKey),
            MaxFeedMmPerMin: AxisLimit(axes, CarriageRole, axis => axis.MaxFeedMmPerMin),
            MaxWorkpieceSpeedRpm: AxisLimit(axes, WorkpieceSpindleRole, axis => axis.MaxSpeedRpm),
            MaxWheelSpeedRpm: AxisLimit(axes, WheelSpindleRole, axis => axis.MaxSpeedRpm),
            MinBodyLengthMm: machine.Workpiece.MinBodyLengthMm,
            MaxBodyLengthMm: machine.Workpiece.MaxBodyLengthMm,
            MinRadiusMm: UnitConversion.DiameterMmToRadiusMm(machine.Workpiece.MinDiameterMm),
            MaxRadiusMm: UnitConversion.DiameterMmToRadiusMm(machine.Workpiece.MaxDiameterMm));
    }

    private static double Threshold(MachineDescription machine, string key) =>
        machine.Thresholds.TryGetValue(key, out double value)
            ? value
            : throw new Contracts.GatewayException(
                $"machine.json is missing threshold '{key}'; the HMI will not guess a machine limit.");

    private static double AxisLimit(
        IReadOnlyList<AxisDescription> axes,
        string role,
        Func<AxisDescription, double?> selector)
    {
        AxisDescription? axis = axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Role, role, StringComparison.Ordinal));

        if (axis is null)
        {
            // 机床没有这根轴：该方向上没有可用能力，任何非零设定都会被判超限。
            return 0.0;
        }

        return selector(axis)
            ?? throw new Contracts.GatewayException(
                $"machine.json axis '{axis.Name}' (role {role}) is missing its speed/feed limit.");
    }
}
