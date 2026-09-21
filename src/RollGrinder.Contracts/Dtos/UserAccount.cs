using System;

namespace RollGrinder.Contracts.Dtos;

/// <summary>
/// 一个操作账号。**不带口令**——口令只以哈希的形式留在仓储里，
/// 任何一层都拿不到明文，也就没法不小心写进日志或记录。
/// </summary>
/// <param name="UserName">用户名，登录时按它找人，大小写不敏感。</param>
/// <param name="Role">权限级别。</param>
/// <param name="MustSetPassword">
/// 这个账号还没设过口令。首次启动时种下的管理账号就是这个状态：
/// 登录时先让人设一个口令再放行，免得出厂默认口令一直留在现场。
/// </param>
/// <param name="CreatedAtUtc">建立时刻。</param>
public sealed record UserAccount(
    string UserName,
    UserRole Role,
    bool MustSetPassword,
    DateTimeOffset CreatedAtUtc);
