using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Services.Monitoring;

/// <summary>一个机构状态读出来是什么样。</summary>
public enum IndicatorState
{
    /// <summary>读不到（没映射、质量不好、断线）。界面画虚框，不当成"关"。</summary>
    Unknown = 0,

    /// <summary>关 / 不在位。灰。</summary>
    Off = 1,

    /// <summary>开 / 到位。绿。</summary>
    On = 2,

    /// <summary>动作中（发了命令还没到位）。橙。读 <c>&lt;变量&gt;.moving</c>，没映射就没有这一态。</summary>
    Moving = 3,

    /// <summary>故障。红。读 <c>&lt;变量&gt;.fault</c>，没映射就没有这一态。</summary>
    Fault = 4,
}

/// <summary>
/// 一个机构状态指示（修改稿 5.5 状态带、5.6 手动页到位显示）。
/// </summary>
/// <param name="Key">指示的名字，也是界面文案资源键的后缀（Status_&lt;Key&gt;、Status_&lt;Key&gt;_On / _Off）。</param>
/// <param name="TagKey">要读的逻辑变量。</param>
public sealed record StatusIndicator(string Key, string TagKey)
{
    /// <summary>名字的资源键，例如"套筒"。</summary>
    public string LabelResourceKey => "Status_" + Key;

    /// <summary>开 / 到位时写的字，例如"伸出"。</summary>
    public string OnResourceKey => "Status_" + Key + "_On";

    /// <summary>关时写的字，例如"缩回"。</summary>
    public string OffResourceKey => "Status_" + Key + "_Off";

    /// <summary>从快照里读这一项。数值、布尔都认（PLC 位有的映成 Boolean，有的映成 Int）。</summary>
    public IndicatorState Read(MachineStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ConnectionState != GatewayConnectionState.Connected)
        {
            return IndicatorState.Unknown;
        }

        // 故障、动作中两位可选：映射了且为真时盖过开 / 关（四态灯，界面修订稿 v3 1.1）。
        if (snapshot.GetNumberOrNull(FaultTagKey) is double fault && fault != 0.0)
        {
            return IndicatorState.Fault;
        }

        if (snapshot.GetNumberOrNull(MovingTagKey) is double moving && moving != 0.0)
        {
            return IndicatorState.Moving;
        }

        double? value = snapshot.GetNumberOrNull(TagKey);
        return value is null ? IndicatorState.Unknown : value.Value != 0.0 ? IndicatorState.On : IndicatorState.Off;
    }

    /// <summary>故障位（可选）。</summary>
    public string FaultTagKey => TagKey + ".fault";

    /// <summary>动作中位（可选）。</summary>
    public string MovingTagKey => TagKey + ".moving";

    /// <summary>几项合成一个灯（例如内外两个测量臂合成"测量臂"）：有一项开就是开；都读不到才是读不到。</summary>
    public static IndicatorState Combine(IEnumerable<IndicatorState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        IndicatorState[] all = states.ToArray();
        return all.Contains(IndicatorState.Fault) ? IndicatorState.Fault
            : all.Contains(IndicatorState.Moving) ? IndicatorState.Moving
            : all.Contains(IndicatorState.On) ? IndicatorState.On
            : all.Contains(IndicatorState.Off) ? IndicatorState.Off
            : IndicatorState.Unknown;
    }
}

/// <summary>
/// 机构状态目录。保持型手动动作的状态回读（冷却水、砂轮、头架）直接用手动目录那一位；
/// 其余是 Q7 要电气补的到位信号，按 <c>status.&lt;名字&gt;</c> 在 tagmap 里映射。
/// </summary>
public static class MachineStatusCatalog
{
    public static StatusIndicator Coolant { get; } = new("coolant", MachineTagKeys.ManualCommandState("coolant"));

    public static StatusIndicator WheelRunning { get; } = new("wheelRunning", MachineTagKeys.ManualCommandState("wheel.run"));

    public static StatusIndicator HeadstockForward { get; } = new("headstockForward", MachineTagKeys.ManualCommandState("headstock.forward"));

    public static StatusIndicator HeadstockReverse { get; } = new("headstockReverse", MachineTagKeys.ManualCommandState("headstock.reverse"));

    public static StatusIndicator OuterArm { get; } = new("outerArm", MachineTagKeys.Status("outerArm.lowered"));

    public static StatusIndicator InnerArm { get; } = new("innerArm", MachineTagKeys.Status("innerArm.lowered"));

    public static StatusIndicator Quill { get; } = new("quill", MachineTagKeys.Status("quill.extended"));

    public static StatusIndicator Tailstock { get; } = new("tailstock", MachineTagKeys.Status("tailstock.forward"));

    public static StatusIndicator Driver { get; } = new("driver", MachineTagKeys.Status("driver.extended"));

    public static StatusIndicator SoftLandingHeadstock { get; } = new("softLandingHeadstock", MachineTagKeys.Status("softLanding.headstock.raised"));

    public static StatusIndicator SoftLandingTailstock { get; } = new("softLandingTailstock", MachineTagKeys.Status("softLanding.tailstock.raised"));

    public static StatusIndicator SteadyRest { get; } = new("steadyRest", MachineTagKeys.Status("steadyRest.engaged"));

    /// <summary>防护门关好。</summary>
    public static StatusIndicator Door { get; } = new("door", MachineTagKeys.Status("door.closed"));

    /// <summary>液压正常。</summary>
    public static StatusIndicator Hydraulics { get; } = new("hydraulics", MachineTagKeys.Status("hydraulics.ok"));

    /// <summary>润滑正常。</summary>
    public static StatusIndicator Lubrication { get; } = new("lubrication", MachineTagKeys.Status("lubrication.ok"));

    /// <summary>全部指示。</summary>
    public static IReadOnlyList<StatusIndicator> All { get; } = new[]
    {
        Coolant, WheelRunning, HeadstockForward, HeadstockReverse,
        OuterArm, InnerArm, Quill, Tailstock, Driver,
        SoftLandingHeadstock, SoftLandingTailstock, SteadyRest,
        Door, Hydraulics, Lubrication,
    };

    /// <summary>
    /// 辅助功能块的五组（界面修订稿 v3 1.1）：冷却 · 装夹 · 支承 · 测量 · 机床，每组一行。
    /// 组名资源键 "StatusGroup_" + 键。自动页与 JOG 页同一套。
    /// </summary>
    public static IReadOnlyList<(string Key, IReadOnlyList<StatusIndicator> Indicators)> Groups { get; } = new (string, IReadOnlyList<StatusIndicator>)[]
    {
        ("cooling", new[] { Coolant, WheelRunning }),
        ("clamping", new[] { Tailstock, Quill, Driver }),
        ("support", new[] { SteadyRest, SoftLandingHeadstock, SoftLandingTailstock }),
        ("measuring", new[] { OuterArm, InnerArm }),
        ("machine", new[] { Door, Hydraulics, Lubrication }),
    };
}
