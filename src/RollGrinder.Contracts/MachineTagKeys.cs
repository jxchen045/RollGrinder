using System;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Contracts;

/// <summary>
/// 逻辑变量名的构成约定。物理地址一律由 tagmap.json 决定，这里只定义逻辑名怎么拼，
/// 轴名来自 machine.json，不在代码里写死。
/// </summary>
public static class MachineTagKeys
{
    /// <summary>NC 通道状态。</summary>
    public const string ChannelState = "machine.channelState";

    /// <summary>当前 NC 程序名。</summary>
    public const string ProgramName = "machine.programName";

    /// <summary>已下发参数是否有效（上位机写、NC 读）。</summary>
    public const string JobParametersValid = "job.parametersValid";

    /// <summary>辊件公称半径（mm）。</summary>
    public const string JobRollRadiusMm = "job.rollRadiusMm";

    /// <summary>辊身长度（mm）。</summary>
    public const string JobBodyLengthMm = "job.bodyLengthMm";

    /// <summary>轴向进给（mm/min）。</summary>
    public const string JobFeedMmPerMin = "job.feedMmPerMin";

    /// <summary>测量得到的直径（mm）。</summary>
    public const string MeasuredDiameterMm = "measure.diameterMm";

    /// <summary>某根轴的实际位置（mm）。</summary>
    public static string AxisActualPositionMm(string axisName) =>
        Compose("axis", axisName, "actualPositionMm");

    /// <summary>某根轴的实际转速（r/min）。</summary>
    public static string AxisActualSpeedRpm(string axisName) =>
        Compose("axis", axisName, "actualSpeedRpm");

    private static string Compose(string prefix, string axisName, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axisName);
        return string.Concat(prefix, ".", axisName, ".", suffix);
    }

    /// <summary>按机床描述列出监控需要读取的逻辑名（缺哪一项由 tagmap 决定，读不到的会被跳过）。</summary>
    public static System.Collections.Generic.IReadOnlyList<string> MonitoringKeys(MachineDescription machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var keys = new System.Collections.Generic.List<string>
        {
            ChannelState,
            ProgramName,
            JobParametersValid,
            MeasuredDiameterMm,
        };

        foreach (AxisDescription axis in machine.Axes)
        {
            if (!axis.IsPresent)
            {
                continue;
            }

            keys.Add(AxisActualPositionMm(axis.Name));
            keys.Add(AxisActualSpeedRpm(axis.Name));
        }

        return keys;
    }
}
