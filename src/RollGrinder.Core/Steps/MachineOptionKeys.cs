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

    /// <summary>A/B 双测头（测量安装误差要靠两端比对）。</summary>
    public const string DualProbeMeasurement = "hasDualProbeMeasurement";

    /// <summary>U1 轴托瓦自动调平。</summary>
    public const string U1Leveling = "hasU1Leveling";

    /// <summary>砂轮接触检测（声发射或功率突变），砂轮自动趋近要用。</summary>
    public const string ContactDetection = "hasContactDetection";
}

/// <summary>
/// 轴角色的逻辑名。领域层按角色找轴，绝不按轴名——轴名是现场的，角色是软件的。
/// 与 RollGrinder.Contracts 的 MachineAxisRoles 是同一套约定；
/// 领域层不引用 Contracts，所以这里只列领域用得到的那几个，有测试盯着两边一致。
/// </summary>
public static class MachineAxisRoleNames
{
    /// <summary>中高调整轴（补偿执行轴）。</summary>
    public const string CrownAdjust = "CrownAdjust";
}

/// <summary>测量量的逻辑名，对应 machine.json 的 measurementChannels[].quantity。</summary>
public static class MeasurementQuantities
{
    /// <summary>直径。</summary>
    public const string Diameter = "Diameter";

    /// <summary>圆度。</summary>
    public const string Roundness = "Roundness";
}
