namespace RollGrinder.Contracts;

/// <summary>
/// machine.json 中 axes[].role 的约定取值。轴名是现场的，角色是软件的：
/// 代码按角色找轴，绝不按轴名。
/// </summary>
public static class MachineAxisRoles
{
    /// <summary>切入轴（半径方向）。</summary>
    public const string InfeedRadius = "InfeedRadius";

    /// <summary>纵向拖板。实机是 Z1/Z2 龙门同步双驱，NC 侧作一根轴看，这里也是一根。</summary>
    public const string Carriage = "Carriage";

    /// <summary>测量架进给轴（实机的 X1）：测量臂整体的进退，与磨架的 X 是两根轴。</summary>
    public const string MeasuringCarriage = "MeasuringCarriage";

    /// <summary>工件主轴（头架）。</summary>
    public const string WorkpieceSpindle = "WorkpieceSpindle";

    /// <summary>砂轮主轴。</summary>
    public const string WheelSpindle = "WheelSpindle";

    /// <summary>砂轮摆角轴。</summary>
    public const string WheelSwivel = "WheelSwivel";

    /// <summary>
    /// 辊形执行轴。
    ///
    /// 实机（MK84160）上是 U 轴：**整条辊形曲线由它走出来**，不是在 X 轴之上做一点
    /// 中高微调——先前叫 CrownAdjust 是按通用磨床假设的，与实机不符。
    /// 行程因此比"微调"大得多，各台不同，由 machine.json 给。
    /// 行程间补偿也走这根轴：补偿本来就是对辊形的一次修正。
    /// </summary>
    public const string RollProfile = "RollProfile";
}
