using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Services.Manual;

/// <summary>一个手动动作往机床写什么形式的命令。</summary>
public enum ManualCommandKind
{
    /// <summary>
    /// 脉冲：写 true，过一个脉宽再写 false。
    /// PLC 侧必须按**上升沿**触发并自行复位——上位机被强制结束时那一句 false 就发不出去了。
    /// </summary>
    Pulse = 0,

    /// <summary>保持：按开关写 true / false，状态可回读（冷却水、砂轮、头架）。</summary>
    Toggle = 1,

    /// <summary>不写机床：由上位机自己完成（例如测量采样走测量服务）。</summary>
    Local = 2,
}

/// <summary>
/// 一个手动动作的定义。
///
/// 这里**全部是离散命令**，没有"按住才动"的点动。
/// 点动属于实时控制回路，要有硬件使能与安全回路托底，归机床操作面板与 PLC；
/// 上位机被强制结束时按住的键就松不开了——这正是最高原则要避开的事。
/// </summary>
/// <param name="Key">逻辑动作名，tagmap 里按 <c>manual.&lt;Key&gt;</c> 映射。</param>
/// <param name="Kind">命令形式。</param>
/// <param name="RequiresIdleChannel">
/// 自动循环还挂着程序时是否禁用。这是**防呆**，不是安全功能——
/// 真正的联锁在 PLC，上位机只是不去按那个按钮而已。
/// </param>
/// <param name="RequiresConfirmation">是否需要二次确认（会动大件、会松开辊子的动作）。</param>
public sealed record ManualCommandDescriptor(
    string Key,
    ManualCommandKind Kind,
    bool RequiresIdleChannel = true,
    bool RequiresConfirmation = false)
{
    /// <summary>界面文案的资源键，约定为 "Action_" + Key 的驼峰形式，由目录给出。</summary>
    public string ResourceKey { get; init; } = "Action_" + Key;
}

/// <summary>
/// 手动页按钮矩阵的动作目录，对应 docs/design/B-Manual-手动与辅助操作.html 的三组按钮。
/// 顺序就是设计稿上的顺序——现场的手是有肌肉记忆的，别乱动。
/// </summary>
public static class ManualCommandCatalog
{
    /// <summary>测量臂（8 个）。</summary>
    public static IReadOnlyList<ManualCommandDescriptor> MeasuringArm { get; } = new[]
    {
        new ManualCommandDescriptor("probeA.lower", ManualCommandKind.Pulse) { ResourceKey = "Action_ProbeALower" },
        new ManualCommandDescriptor("probeA.raise", ManualCommandKind.Pulse) { ResourceKey = "Action_ProbeARaise" },
        new ManualCommandDescriptor("probeB.lower", ManualCommandKind.Pulse) { ResourceKey = "Action_ProbeBLower" },
        new ManualCommandDescriptor("probeB.raise", ManualCommandKind.Pulse) { ResourceKey = "Action_ProbeBRaise" },
        new ManualCommandDescriptor("probes.toRoll", ManualCommandKind.Pulse) { ResourceKey = "Action_ProbesToRoll" },
        new ManualCommandDescriptor("probes.home", ManualCommandKind.Pulse) { ResourceKey = "Action_ProbesHome" },
        new ManualCommandDescriptor("probes.calibrate", ManualCommandKind.Pulse) { ResourceKey = "Action_CalibrateProbes" },

        // 采样不写机床：直接走测量服务，把当前测头读数存成一个测点。
        new ManualCommandDescriptor("measurement.sample", ManualCommandKind.Local, RequiresIdleChannel: false)
        {
            ResourceKey = "Action_SampleMeasurement",
        },
    };

    /// <summary>尾架与套筒（6 个）。</summary>
    public static IReadOnlyList<ManualCommandDescriptor> Tailstock { get; } = new[]
    {
        new ManualCommandDescriptor("quill.extend", ManualCommandKind.Pulse) { ResourceKey = "Action_QuillExtend" },
        new ManualCommandDescriptor("quill.retract", ManualCommandKind.Pulse) { ResourceKey = "Action_QuillRetract" },
        new ManualCommandDescriptor("tailstock.forward", ManualCommandKind.Pulse) { ResourceKey = "Action_TailstockForward" },
        new ManualCommandDescriptor("tailstock.backward", ManualCommandKind.Pulse) { ResourceKey = "Action_TailstockBackward" },
        new ManualCommandDescriptor("tailstock.clamp", ManualCommandKind.Pulse) { ResourceKey = "Action_TailstockClamp" },

        // 松开尾架 = 让几十吨的辊子失去一端支承，按两次。
        new ManualCommandDescriptor("tailstock.release", ManualCommandKind.Pulse, RequiresConfirmation: true)
        {
            ResourceKey = "Action_TailstockRelease",
        },
    };

    /// <summary>头架、中心架与其他（12 个）。</summary>
    public static IReadOnlyList<ManualCommandDescriptor> Other { get; } = new[]
    {
        new ManualCommandDescriptor("headstock.run", ManualCommandKind.Toggle) { ResourceKey = "Action_HeadstockStart" },
        new ManualCommandDescriptor("headstock.speedUp", ManualCommandKind.Pulse) { ResourceKey = "Action_HeadstockUp" },
        new ManualCommandDescriptor("headstock.speedDown", ManualCommandKind.Pulse) { ResourceKey = "Action_HeadstockDown" },
        new ManualCommandDescriptor("driver.extend", ManualCommandKind.Pulse) { ResourceKey = "Action_DriverExtend" },

        // 拨盘收回前轧辊必须已经停稳，按两次。
        new ManualCommandDescriptor("driver.retract", ManualCommandKind.Pulse, RequiresConfirmation: true)
        {
            ResourceKey = "Action_DriverRetract",
        },
        new ManualCommandDescriptor("u1Axis.zero", ManualCommandKind.Pulse) { ResourceKey = "Action_U1AxisZero" },
        new ManualCommandDescriptor("softLanding.up", ManualCommandKind.Pulse) { ResourceKey = "Action_SoftLandingUp" },

        // 把辊子落到托瓦上，按两次。
        new ManualCommandDescriptor("softLanding.down", ManualCommandKind.Pulse, RequiresConfirmation: true)
        {
            ResourceKey = "Action_SoftLandingDown",
        },

        // 冷却水是唯一一个磨削当中也要能开关的动作。
        new ManualCommandDescriptor("coolant", ManualCommandKind.Toggle, RequiresIdleChannel: false)
        {
            ResourceKey = "Action_Coolant",
        },
        new ManualCommandDescriptor("wheel.run", ManualCommandKind.Toggle) { ResourceKey = "Action_WheelStart" },
        new ManualCommandDescriptor("wheel.measureDiameter", ManualCommandKind.Pulse) { ResourceKey = "Action_MeasureWheelDiameter" },

        // 各轴归位会让整台机床动起来，按两次。
        new ManualCommandDescriptor("axes.home", ManualCommandKind.Pulse, RequiresConfirmation: true)
        {
            ResourceKey = "Action_AllAxesHome",
        },
    };

    /// <summary>全部 26 个动作。</summary>
    public static IReadOnlyList<ManualCommandDescriptor> All { get; } =
        MeasuringArm.Concat(Tailstock).Concat(Other).ToArray();

    /// <summary>需要回读状态的保持型动作。</summary>
    public static IReadOnlyList<ManualCommandDescriptor> Toggles { get; } =
        All.Where(command => command.Kind == ManualCommandKind.Toggle).ToArray();
}
