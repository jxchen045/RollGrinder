using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Services.Session;

/// <summary>
/// 上位机里"谁能改什么"的一项（修改稿问题 Q9）。页面和键只问"有没有这项权限"，
/// 不各自去比角色——哪项归哪一级只在 <see cref="PermissionPolicy"/> 一处定。
/// </summary>
public enum Permission
{
    /// <summary>运行：选作业、下发、启动 / 暂停、手动页的机构动作。</summary>
    RunMachine = 0,

    /// <summary>作业与轧辊台账：建作业、登记和修改轧辊。</summary>
    EditJobs = 1,

    /// <summary>辊形库。</summary>
    EditProfiles = 2,

    /// <summary>工艺程序库。</summary>
    EditPrograms = 3,

    /// <summary>标定值、砂轮数据与修整参数、换砂轮。</summary>
    EditCalibration = 4,

    /// <summary>账号管理。</summary>
    ManageUsers = 5,

    /// <summary>补偿增益、限幅等补偿设置。</summary>
    EditCompensation = 6,

    /// <summary>机床配置（machine.json）。</summary>
    EditMachineConfig = 7,

    /// <summary>标签映射（tagmap.json）。</summary>
    EditTagMap = 8,

    /// <summary>台账改计划（逐支、多选）、导入、作废（关系设计第 8 节）。作业里"变更这支辊的工艺"另按 hmi.json 定。</summary>
    EditRollPlans = 9,
}

/// <summary>
/// 权限表（按 Q9 的建议）：操作者选作业、下发、运行；管理员另加辊形、程序、标定与账号；
/// 制造商另加补偿、机床配置、标签映射。角色是逐级包含的，高一级有低一级的全部权限。
/// </summary>
public static class PermissionPolicy
{
    private static readonly IReadOnlyDictionary<Permission, UserRole> MinimumRoles = new Dictionary<Permission, UserRole>
    {
        [Permission.RunMachine] = UserRole.Operator,
        [Permission.EditJobs] = UserRole.Operator,
        [Permission.EditProfiles] = UserRole.Administrator,
        [Permission.EditPrograms] = UserRole.Administrator,
        [Permission.EditCalibration] = UserRole.Administrator,
        [Permission.ManageUsers] = UserRole.Administrator,
        [Permission.EditCompensation] = UserRole.Manufacturer,
        [Permission.EditMachineConfig] = UserRole.Manufacturer,
        [Permission.EditTagMap] = UserRole.Manufacturer,
        [Permission.EditRollPlans] = UserRole.Administrator,
    };

    /// <summary>这项权限最低要哪一级。</summary>
    public static UserRole MinimumRole(Permission permission) =>
        MinimumRoles.TryGetValue(permission, out UserRole role)
            ? role
            : throw new ArgumentOutOfRangeException(nameof(permission), permission, "Permission is not in the policy table.");

    /// <summary>某一级有哪些权限（界面上"我的权限"一览、测试用）。</summary>
    public static IReadOnlyList<Permission> GrantedTo(UserRole role) =>
        Enum.GetValues<Permission>().Where(permission => role >= MinimumRole(permission)).ToList();
}

/// <summary>会话上的权限判断。没登录一律没有。</summary>
public static class UserSessionPermissionExtensions
{
    public static bool Can(this IUserSession session, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.HasAtLeast(PermissionPolicy.MinimumRole(permission));
    }
}
