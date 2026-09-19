namespace RollGrinder.Contracts;

/// <summary>
/// machine.json 中 axes[].role 的约定取值。轴名是现场的，角色是软件的：
/// 代码按角色找轴，绝不按轴名。
/// </summary>
public static class MachineAxisRoles
{
    /// <summary>切入轴（半径方向）。</summary>
    public const string InfeedRadius = "InfeedRadius";

    /// <summary>纵向拖板。</summary>
    public const string Carriage = "Carriage";

    /// <summary>工件主轴（头架）。</summary>
    public const string WorkpieceSpindle = "WorkpieceSpindle";

    /// <summary>砂轮主轴。</summary>
    public const string WheelSpindle = "WheelSpindle";

    /// <summary>砂轮摆角轴。</summary>
    public const string WheelSwivel = "WheelSwivel";
}
