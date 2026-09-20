namespace RollGrinder.Core.Steps;

/// <summary>
/// 选装装置的逻辑名。工序类型用它声明"这道工序需要机床装了什么"，
/// 具体这台机床装没装由 machine.json 的 options 说了算——
/// 代码里只出现逻辑名，不出现任何机床型号或物理地址（架构约束 ③）。
/// </summary>
public static class MachineOptionKeys
{
    /// <summary>砂轮修整装置（金刚笔 / 修整滚轮）。</summary>
    public const string WheelDresser = "hasWheelDresser";

    /// <summary>涡流探伤装置。</summary>
    public const string EddyCurrentTester = "hasEddyCurrentTester";
}

/// <summary>测量量的逻辑名，对应 machine.json 的 measurementChannels[].quantity。</summary>
public static class MeasurementQuantities
{
    /// <summary>直径。</summary>
    public const string Diameter = "Diameter";

    /// <summary>圆度。</summary>
    public const string Roundness = "Roundness";
}
