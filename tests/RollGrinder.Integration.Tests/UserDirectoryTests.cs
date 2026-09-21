using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Data;
using RollGrinder.Services.Session;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 登录与账号管理。口令只以 PBKDF2 的导出密钥存，库里没有明文。
/// </summary>
public sealed class UserDirectoryTests : IDisposable
{
    private readonly TempWorkspace workspace = new();
    private readonly SqliteDatabase database;

    public UserDirectoryTests()
    {
        this.database = new SqliteDatabase(Path.Combine(this.workspace.Root, "data", SqliteDatabase.FileName));
    }

    public void Dispose() => this.workspace.Dispose();

    private async Task<UserDirectory> DirectoryAsync()
    {
        await this.database.MigrateAsync(CancellationToken.None);
        return new UserDirectory(new SqliteUserRepository(this.database), TimeProvider.System);
    }

    [Fact]
    public async Task A_password_that_matches_signs_in()
    {
        UserDirectory directory = await DirectoryAsync();
        await directory.CreateAsync("wang", "correct horse", UserRole.Operator, CancellationToken.None);

        SignInResult result = await directory.SignInAsync("wang", "correct horse", CancellationToken.None);

        result.Outcome.Should().Be(SignInOutcome.Succeeded);
        result.Account!.UserName.Should().Be("wang");
        result.Account.Role.Should().Be(UserRole.Operator);
    }

    [Theory]
    [InlineData("wang", "wrong")]
    [InlineData("nobody", "correct horse")]
    public async Task A_wrong_password_and_an_unknown_user_are_rejected_the_same_way(string userName, string password)
    {
        // 两种情形不分开报——分开报等于告诉人哪个用户名存在。
        UserDirectory directory = await DirectoryAsync();
        await directory.CreateAsync("wang", "correct horse", UserRole.Operator, CancellationToken.None);

        SignInResult result = await directory.SignInAsync(userName, password, CancellationToken.None);

        result.Outcome.Should().Be(SignInOutcome.Rejected);
        result.Account.Should().BeNull();
    }

    [Fact]
    public async Task User_names_are_case_insensitive()
    {
        UserDirectory directory = await DirectoryAsync();
        await directory.CreateAsync("Wang", "pw", UserRole.Operator, CancellationToken.None);

        (await directory.SignInAsync("wang", "pw", CancellationToken.None)).Outcome
            .Should().Be(SignInOutcome.Succeeded);
        await directory.Invoking(d => d.CreateAsync("WANG", "pw2", UserRole.Operator, CancellationToken.None))
            .Should().ThrowAsync<DomainException>("同一个人不该能建两个账号");
    }

    [Fact]
    public async Task The_seed_account_has_no_password_until_someone_sets_one()
    {
        // 出厂默认口令不会一直留在机器上：种下的账号没有口令，首次登录时现场自己设。
        UserDirectory directory = await DirectoryAsync();
        await directory.EnsureSeedAccountAsync(CancellationToken.None);

        IReadOnlyList<UserAccount> accounts = await directory.ListAsync(CancellationToken.None);
        accounts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                UserName = UserDirectory.SeedUserName,
                Role = UserRole.Manufacturer,
                MustSetPassword = true,
            });

        SignInResult first = await directory.SignInAsync(UserDirectory.SeedUserName, string.Empty, CancellationToken.None);
        first.Outcome.Should().Be(SignInOutcome.PasswordNotSet, "空口令也不该放行");

        await directory.SetPasswordAsync(UserDirectory.SeedUserName, "shop floor", CancellationToken.None);

        (await directory.SignInAsync(UserDirectory.SeedUserName, "shop floor", CancellationToken.None)).Outcome
            .Should().Be(SignInOutcome.Succeeded);
        (await directory.SignInAsync(UserDirectory.SeedUserName, string.Empty, CancellationToken.None)).Outcome
            .Should().Be(SignInOutcome.Rejected);
    }

    [Fact]
    public async Task Seeding_twice_does_not_add_a_second_account()
    {
        UserDirectory directory = await DirectoryAsync();
        await directory.EnsureSeedAccountAsync(CancellationToken.None);
        await directory.SetPasswordAsync(UserDirectory.SeedUserName, "shop floor", CancellationToken.None);
        await directory.EnsureSeedAccountAsync(CancellationToken.None);

        (await directory.ListAsync(CancellationToken.None)).Should().ContainSingle();
        (await directory.SignInAsync(UserDirectory.SeedUserName, "shop floor", CancellationToken.None)).Outcome
            .Should().Be(SignInOutcome.Succeeded, "重新种一次不该把已经设好的口令抹掉");
    }

    [Fact]
    public async Task The_password_never_reaches_the_database_in_the_clear()
    {
        const string password = "a-very-distinctive-passphrase";
        UserDirectory directory = await DirectoryAsync();
        await directory.CreateAsync("wang", password, UserRole.Operator, CancellationToken.None);

        byte[] file = await File.ReadAllBytesAsync(this.database.DatabaseFilePath, CancellationToken.None);

        IndexOf(file, Encoding.UTF8.GetBytes(password)).Should().Be(-1, "库里不该有口令明文");
        IndexOf(file, Encoding.Unicode.GetBytes(password)).Should().Be(-1);
    }

    [Fact]
    public async Task Two_accounts_with_the_same_password_get_different_hashes()
    {
        // 每个账号各自的盐：否则一张彩虹表能同时打穿所有用同一个口令的账号。
        UserDirectory directory = await DirectoryAsync();
        await directory.CreateAsync("wang", "same", UserRole.Operator, CancellationToken.None);
        await directory.CreateAsync("li", "same", UserRole.Operator, CancellationToken.None);

        var repository = new SqliteUserRepository(this.database);
        StoredUser? first = await repository.FindAsync("wang", CancellationToken.None);
        StoredUser? second = await repository.FindAsync("li", CancellationToken.None);

        first!.PasswordSalt.Should().NotEqual(second!.PasswordSalt);
        first.PasswordHash.Should().NotEqual(second.PasswordHash);
    }

    [Fact]
    public async Task The_last_manufacturer_account_cannot_be_deleted_or_demoted()
    {
        // 删了或降了就再也没人能管这台机器。
        UserDirectory directory = await DirectoryAsync();
        await directory.EnsureSeedAccountAsync(CancellationToken.None);
        await directory.CreateAsync("wang", "pw", UserRole.Administrator, CancellationToken.None);

        await directory.Invoking(d => d.DeleteAsync(UserDirectory.SeedUserName, CancellationToken.None))
            .Should().ThrowAsync<DomainException>();
        await directory.Invoking(d => d.SetRoleAsync(UserDirectory.SeedUserName, UserRole.Operator, CancellationToken.None))
            .Should().ThrowAsync<DomainException>();

        // 再添一个制造商级账号之后就放行了。
        await directory.CreateAsync("chen", "pw", UserRole.Manufacturer, CancellationToken.None);
        await directory.DeleteAsync(UserDirectory.SeedUserName, CancellationToken.None);

        (await directory.ListAsync(CancellationToken.None)).Select(a => a.UserName)
            .Should().BeEquivalentTo("wang", "chen");
    }

    [Fact]
    public async Task Clearing_a_password_puts_the_account_back_to_set_it_at_first_sign_in()
    {
        UserDirectory directory = await DirectoryAsync();
        await directory.CreateAsync("wang", "pw", UserRole.Operator, CancellationToken.None);
        await directory.DeleteAsync("wang", CancellationToken.None);
        await directory.CreateAsync("wang", null, UserRole.Operator, CancellationToken.None);

        (await directory.SignInAsync("wang", "pw", CancellationToken.None)).Outcome
            .Should().Be(SignInOutcome.PasswordNotSet, "旧口令不该还能用");
    }

    [Fact]
    public async Task An_empty_password_is_refused_when_setting_one()
    {
        UserDirectory directory = await DirectoryAsync();
        await directory.EnsureSeedAccountAsync(CancellationToken.None);

        await directory.Invoking(d => d.SetPasswordAsync(UserDirectory.SeedUserName, string.Empty, CancellationToken.None))
            .Should().ThrowAsync<DomainException>();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>签退状态下什么都不给：这条是一处规则，不是每个页面各自记得判断。</summary>
public sealed class UserSessionTests
{
    private static readonly UserAccount Manufacturer =
        new("chen", UserRole.Manufacturer, MustSetPassword: false, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_signed_out_session_grants_nothing()
    {
        var session = new UserSession();

        session.IsSignedIn.Should().BeFalse();
        session.CurrentUser.Should().BeNull();
        session.HasAtLeast(UserRole.Operator).Should().BeFalse("连最低级也不给");
        session.HasAtLeast(UserRole.Manufacturer).Should().BeFalse();
    }

    [Fact]
    public void Signing_in_grants_up_to_the_accounts_role()
    {
        var session = new UserSession();
        session.SignIn(new UserAccount("wang", UserRole.Administrator, false, DateTimeOffset.UnixEpoch));

        session.HasAtLeast(UserRole.Operator).Should().BeTrue();
        session.HasAtLeast(UserRole.Administrator).Should().BeTrue();
        session.HasAtLeast(UserRole.Manufacturer).Should().BeFalse();
    }

    [Fact]
    public void Signing_out_takes_it_all_away_again()
    {
        var session = new UserSession();
        var changes = 0;
        session.SessionChanged += (_, _) => changes++;

        session.SignIn(Manufacturer);
        session.SignOut();

        session.IsSignedIn.Should().BeFalse();
        session.HasAtLeast(UserRole.Operator).Should().BeFalse();
        changes.Should().Be(2, "登录与签退各通知一次");
    }

    [Fact]
    public void Signing_out_twice_only_notifies_once()
    {
        var session = new UserSession();
        session.SignIn(Manufacturer);

        var changes = 0;
        session.SessionChanged += (_, _) => changes++;
        session.SignOut();
        session.SignOut();

        changes.Should().Be(1);
    }
}
