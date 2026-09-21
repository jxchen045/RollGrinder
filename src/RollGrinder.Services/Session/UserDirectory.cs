using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Data;

namespace RollGrinder.Services.Session;

/// <summary>登录结果。</summary>
public enum SignInOutcome
{
    /// <summary>口令对上了，可以放行。</summary>
    Succeeded = 0,

    /// <summary>用户名不存在，或者口令不对。两种情形不分开报——分开报等于告诉人哪个用户名存在。</summary>
    Rejected = 1,

    /// <summary>这个账号还没设过口令，先设一个再登录。</summary>
    PasswordNotSet = 2,
}

/// <summary>登录的结果与（成功时）账号本身。</summary>
/// <param name="Outcome">结果。</param>
/// <param name="Account">成功或"还没设口令"时是对应的账号，被拒时为 null。</param>
public sealed record SignInResult(SignInOutcome Outcome, UserAccount? Account);

/// <summary>
/// 账号目录：登录校验与账号增删改。口令的哈希只在这里做，别处拿不到明文。
/// </summary>
public interface IUserDirectory
{
    /// <summary>列出全部账号。</summary>
    Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken);

    /// <summary>校验用户名与口令。</summary>
    Task<SignInResult> SignInAsync(string userName, string password, CancellationToken cancellationToken);

    /// <summary>新建一个账号。口令留空表示"首次登录时再设"。</summary>
    Task CreateAsync(string userName, string? password, UserRole role, CancellationToken cancellationToken);

    /// <summary>改口令。</summary>
    Task SetPasswordAsync(string userName, string password, CancellationToken cancellationToken);

    /// <summary>改权限。</summary>
    Task SetRoleAsync(string userName, UserRole role, CancellationToken cancellationToken);

    /// <summary>删账号。最后一个制造商级账号不许删，否则这台机器就再也没人能管了。</summary>
    Task DeleteAsync(string userName, CancellationToken cancellationToken);

    /// <summary>
    /// 库是空的就种一个制造商级账号，**不带口令**：
    /// 首次登录时由现场自己设一个，出厂默认口令不会一直留在机器上。
    /// </summary>
    Task EnsureSeedAccountAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IUserDirectory"/>
public sealed class UserDirectory : IUserDirectory
{
    /// <summary>首次启动种下的管理账号名。</summary>
    public const string SeedUserName = "admin";

    /// <summary>PBKDF2 迭代次数。新设的口令用这个数；老口令按它自己存的那个数验。</summary>
    public const int Iterations = 210_000;

    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    private readonly IUserRepository users;
    private readonly TimeProvider timeProvider;

    public UserDirectory(IUserRepository users, TimeProvider timeProvider)
    {
        this.users = users ?? throw new ArgumentNullException(nameof(users));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) =>
        this.users.ListAsync(cancellationToken);

    public async Task<SignInResult> SignInAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return new SignInResult(SignInOutcome.Rejected, null);
        }

        StoredUser? stored = await this.users.FindAsync(userName.Trim(), cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return new SignInResult(SignInOutcome.Rejected, null);
        }

        if (stored.PasswordHash is null || stored.PasswordSalt is null)
        {
            return new SignInResult(SignInOutcome.PasswordNotSet, stored.Account);
        }

        byte[] candidate = Derive(password, stored.PasswordSalt, stored.Iterations);

        // 定长时间比较：按字节早退会把"对了几位"透出去。
        return CryptographicOperations.FixedTimeEquals(candidate, stored.PasswordHash)
            ? new SignInResult(SignInOutcome.Succeeded, stored.Account)
            : new SignInResult(SignInOutcome.Rejected, null);
    }

    public async Task CreateAsync(
        string userName,
        string? password,
        UserRole role,
        CancellationToken cancellationToken)
    {
        string name = Normalize(userName);

        if (await this.users.FindAsync(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DomainException($"User '{name}' already exists.");
        }

        await this.users.UpsertAsync(
            Build(new UserAccount(name, role, password is null, this.timeProvider.GetUtcNow()), password),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPasswordAsync(string userName, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new DomainException("A password cannot be empty.");
        }

        StoredUser stored = await RequireAsync(userName, cancellationToken).ConfigureAwait(false);
        await this.users.UpsertAsync(
            Build(stored.Account with { MustSetPassword = false }, password), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRoleAsync(string userName, UserRole role, CancellationToken cancellationToken)
    {
        StoredUser stored = await RequireAsync(userName, cancellationToken).ConfigureAwait(false);

        if (stored.Account.Role == UserRole.Manufacturer && role != UserRole.Manufacturer)
        {
            await RequireAnotherManufacturerAsync(stored.Account.UserName, cancellationToken).ConfigureAwait(false);
        }

        await this.users.UpsertAsync(
            stored with { Account = stored.Account with { Role = role } }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string userName, CancellationToken cancellationToken)
    {
        StoredUser stored = await RequireAsync(userName, cancellationToken).ConfigureAwait(false);

        if (stored.Account.Role == UserRole.Manufacturer)
        {
            await RequireAnotherManufacturerAsync(stored.Account.UserName, cancellationToken).ConfigureAwait(false);
        }

        await this.users.DeleteAsync(stored.Account.UserName, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureSeedAccountAsync(CancellationToken cancellationToken)
    {
        if (await this.users.CountAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return;
        }

        await this.users.UpsertAsync(
            new StoredUser(
                new UserAccount(SeedUserName, UserRole.Manufacturer, MustSetPassword: true, this.timeProvider.GetUtcNow()),
                PasswordHash: null,
                PasswordSalt: null,
                Iterations),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>降级或删掉最后一个制造商级账号会把人自己锁在外面，拦住。</summary>
    private async Task RequireAnotherManufacturerAsync(string exceptUserName, CancellationToken cancellationToken)
    {
        IReadOnlyList<UserAccount> all = await this.users.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (UserAccount account in all)
        {
            if (account.Role == UserRole.Manufacturer
                && !string.Equals(account.UserName, exceptUserName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new DomainException("The last manufacturer account cannot be removed or demoted.");
    }

    private async Task<StoredUser> RequireAsync(string userName, CancellationToken cancellationToken) =>
        await this.users.FindAsync(Normalize(userName), cancellationToken).ConfigureAwait(false)
        ?? throw new DomainException($"User '{userName}' does not exist.");

    private StoredUser Build(UserAccount account, string? password)
    {
        if (password is null)
        {
            return new StoredUser(account with { MustSetPassword = true }, null, null, Iterations);
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new StoredUser(account with { MustSetPassword = false }, Derive(password, salt, Iterations), salt, Iterations);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? string.Empty), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    private static string Normalize(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        return userName.Trim();
    }
}
