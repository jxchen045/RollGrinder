namespace RollGrinder.Contracts.Dtos;

/// <summary>
/// 操作权限。工序编程页的补偿设置、诊断页的部分内容按权限开放。
/// </summary>
public enum UserRole
{
    /// <summary>操作者：日常磨削、查记录。</summary>
    Operator = 0,

    /// <summary>管理员：编工艺、改辊形。</summary>
    Administrator = 1,

    /// <summary>制造商：补偿参数、诊断、机床配置。</summary>
    Manufacturer = 2,
}
