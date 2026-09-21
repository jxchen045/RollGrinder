using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Data;

/// <summary>
/// 账号连同它的口令材料。只在仓储与目录服务之间传递，不往上层走——
/// 上层拿到的是 <see cref="UserAccount"/>，里面没有任何口令痕迹。
/// </summary>
/// <param name="Account">账号本身。</param>
/// <param name="PasswordHash">PBKDF2 导出的密钥；账号还没设口令时为 null。</param>
/// <param name="PasswordSalt">每个账号各自的盐；还没设口令时为 null。</param>
/// <param name="Iterations">导出用的迭代次数，跟着口令一起存，将来调高了老口令照样验得动。</param>
public sealed record StoredUser(
    UserAccount Account,
    byte[]? PasswordHash,
    byte[]? PasswordSalt,
    int Iterations);

/// <summary>账号仓储。口令一律以哈希存，库里没有明文。</summary>
public interface IUserRepository
{
    /// <summary>按用户名列出全部账号（不含口令材料）。</summary>
    Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken);

    /// <summary>找一个账号（含口令材料）；不存在返回 null。用户名大小写不敏感。</summary>
    Task<StoredUser?> FindAsync(string userName, CancellationToken cancellationToken);

    /// <summary>新建或整条覆盖一个账号。</summary>
    Task UpsertAsync(StoredUser user, CancellationToken cancellationToken);

    /// <summary>删掉一个账号。</summary>
    Task DeleteAsync(string userName, CancellationToken cancellationToken);

    /// <summary>库里一共有几个账号。用来判断要不要种一个初始管理账号。</summary>
    Task<int> CountAsync(CancellationToken cancellationToken);
}
