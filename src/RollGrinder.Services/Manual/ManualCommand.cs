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

    /// <summary>
    /// 与本动作互斥的另一个保持型动作（头架正转 / 反转）。置上之后，打开本动作之前
    /// 先把对方清成 false，不把一对矛盾的命令同时摆给 PLC。
    ///
    /// 这是**防呆**：真正的互锁在 PLC。两位都是 false 就是停机，
    /// 所以清对方与置本方之间即使被打断，落到的也是安全的那一侧。
    /// </summary>
    public string? MutuallyExclusiveWith { get; init; }
}

/// <summary>
/// 手动页按钮矩阵的动作目录，对应 docs/design/B-Manual-手动与辅助操作.html 的三组按钮。
///
/// 2026-09 按 MK84160 电气原理图核对过一遍，四处与硬件不符的地方已改：
/// 尾架只有前进/后退（没有夹紧/放松那两个 DO）、A/B 是**外/内测量臂**、
/// 头架是**正转/反转**（不是启动/停止）、软着陆**分头架侧与尾架侧**四个动作
/// （Q69.0–Q69.3）。详见 docs/design/手动动作规范.md。
///
/// 组内顺序就是设计稿上的顺序——现场的手是有肌肉记忆的，别乱动。
/// </summary>
public static class ManualCommandCatalog
{
    /// <summary>头架正转的动作名。反转与它互斥，两处都要引到这个常量。</summary>
    private const string HeadstockForwardKey = "headstock.forward";

    /// <summary>头架反转的动作名。</summary>
    private const string HeadstockReverseKey = "headstock.reverse";

    /// <summary>
    /// 测量臂（8 个）。外测量臂上装的是测头 A，内测量臂上装的是测头 B——
    /// 原理图里的 DI 就是这么标的（I113.0–I113.7：外/内测量臂收起·放下、
    /// 外测头 A / 内测头 B 压缩过量、保护限位左/右），按钮上也照这么写，
    /// 免得现场对着"A/B"猜是哪一侧。
    /// </summary>
    public static IReadOnlyList<ManualCommandDescriptor> MeasuringArm { get; } = new[]
    {
        new ManualCommandDescriptor("outerArm.lower", ManualCommandKind.Pulse) { ResourceKey = "Action_OuterArmLower" },
        new ManualCommandDescriptor("outerArm.raise", ManualCommandKind.Pulse) { ResourceKey = "Action_OuterArmRaise" },
        new ManualCommandDescriptor("innerArm.lower", ManualCommandKind.Pulse) { ResourceKey = "Action_InnerArmLower" },
        new ManualCommandDescriptor("innerArm.raise", ManualCommandKind.Pulse) { ResourceKey = "Action_InnerArmRaise" },
        new ManualCommandDescriptor("arms.toRoll", ManualCommandKind.Pulse) { ResourceKey = "Action_ArmsToRoll" },
        new ManualCommandDescriptor("arms.home", ManualCommandKind.Pulse) { ResourceKey = "Action_ArmsHome" },
        new ManualCommandDescriptor("arms.calibrate", ManualCommandKind.Pulse) { ResourceKey = "Action_CalibrateArms" },

        // 采样不写机床：直接走测量服务，把当前测头读数存成一个测点。
        new ManualCommandDescriptor("measurement.sample", ManualCommandKind.Local, RequiresIdleChannel: false)
        {
            ResourceKey = "Action_SampleMeasurement",
        },
    };

    /// <summary>
    /// 尾架与套筒（4 个）。原理图上这一组只有前进/后退两对 DO
    /// （尾架 Q67.2 / Q66.1，套筒 Q66.2 / Q65.3，另有 Q67.0）——
    /// **没有"夹紧/放松"**，此前那两个按钮是凭空加的，已删。
    /// 顶紧力由液压系统自己管，不是上位机的一个按钮。
    /// </summary>
    public static IReadOnlyList<ManualCommandDescriptor> Tailstock { get; } = new[]
    {
        new ManualCommandDescriptor("quill.extend", ManualCommandKind.Pulse) { ResourceKey = "Action_QuillExtend" },

        // 套筒缩回 = 顶尖离开辊子端面，按两次。
        new ManualCommandDescriptor("quill.retract", ManualCommandKind.Pulse, RequiresConfirmation: true)
        {
            ResourceKey = "Action_QuillRetract",
        },
        new ManualCommandDescriptor("tailstock.forward", ManualCommandKind.Pulse) { ResourceKey = "Action_TailstockForward" },

        // 尾架后退 = 让几十吨的辊子失去一端支承，按两次。
        new ManualCommandDescriptor("tailstock.backward", ManualCommandKind.Pulse, RequiresConfirmation: true)
        {
            ResourceKey = "Action_TailstockBackward",
        },
    };

    /// <summary>头架、软着陆与其他（15 个）。</summary>
    public static IReadOnlyList<ManualCommandDescriptor> Other { get; } = new[]
    {
        // 头架是正转/反转，不是启动/停止：两个都关掉才是停。两者互斥。
        new ManualCommandDescriptor(HeadstockForwardKey, ManualCommandKind.Toggle)
        {
            ResourceKey = "Action_HeadstockForward",
            MutuallyExclusiveWith = HeadstockReverseKey,
        },
        new ManualCommandDescriptor(HeadstockReverseKey, ManualCommandKind.Toggle)
        {
            ResourceKey = "Action_HeadstockReverse",
            MutuallyExclusiveWith = HeadstockForwardKey,
        },
        new ManualCommandDescriptor("headstock.speedUp", ManualCommandKind.Pulse) { ResourceKey = "Action_HeadstockUp" },
        new ManualCommandDescriptor("headstock.speedDown", ManualCommandKind.Pulse) { ResourceKey = "Action_HeadstockDown" },
        new ManualCommandDescriptor("driver.extend", ManualCommandKind.Pulse) { ResourceKey = "Action_DriverExtend" },

        // 拨盘收回前轧辊必须已经停稳，按两次。
        new ManualCommandDescriptor("driver.retract", ManualCommandKind.Pulse, RequiresConfirmation: true)
        {
            ResourceKey = "Action_DriverRetract",
        },
        new ManualCommandDescriptor("u1Axis.zero", ManualCommandKind.Pulse) { ResourceKey = "Action_U1AxisZero" },

        // 软着陆两侧各一对升降（Q69.0–Q69.3），不是一个开关：
        // 两端不同步就是在掰辊子，所以两侧分开给，让操作工看着托瓦一侧一侧来。
        new ManualCommandDescriptor("softLanding.headstock.up", ManualCommandKind.Pulse)
        {
            ResourceKey = "Action_SoftLandingHeadstockUp",
        },
        new ManualCommandDescriptor("softLanding.headstock.down", ManualCommandKind.Pulse, RequiresConfirmation: true)
        {
            ResourceKey = "Action_SoftLandingHeadstockDown",
        },
        new ManualCommandDescriptor("softLanding.tailstock.up", ManualCommandKind.Pulse)
        {
            ResourceKey = "Action_SoftLandingTailstockUp",
        },
        new ManualCommandDescriptor("softLanding.tailstock.down", ManualCommandKind.Pulse, RequiresConfirmation: true)
        {
            ResourceKey = "Action_SoftLandingTailstockDown",
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

    /// <summary>全部 27 个动作。</summary>
    public static IReadOnlyList<ManualCommandDescriptor> All { get; } =
        MeasuringArm.Concat(Tailstock).Concat(Other).ToArray();

    /// <summary>需要回读状态的保持型动作。</summary>
    public static IReadOnlyList<ManualCommandDescriptor> Toggles { get; } =
        All.Where(command => command.Kind == ManualCommandKind.Toggle).ToArray();
}
