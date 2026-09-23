using System.Linq;
using System.Threading.Tasks;
using RollGrinder.App.ViewModels;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Session;

namespace RollGrinder.App.SelfTest;

/// <summary>自检里给 admin 设的口令。只存在于自检专用数据目录。</summary>
internal static class SelfTestAccounts
{
    public const string AdminPassword = "SelfTest-Admin-1";
    public const string OperatorName = "selftest-op";
    public const string OperatorPassword = "SelfTest-Op-1";
}

/// <summary>登录：首次设口令、口令不一致、登录成功；用户管理：建、重置、删、删不掉最后一个制造商账号。</summary>
internal sealed class SessionSuite : ISelfTestSuite
{
    private readonly bool signInOnly;

    public SessionSuite(bool signInOnly) => this.signInOnly = signInOnly;

    public string Name => "Session";

    public async Task RunAsync(SelfTestHarness h)
    {
        ShellViewModel shell = h.Shell;

        await h.StepAsync("SignIn", "LoginOverlayShownAtStartup", ctx =>
        {
            ctx.Check(shell.IsSignInOpen, "sign-in overlay should be open at startup");
            ctx.Check(shell.KnownUserNames.Contains(UserDirectory.SeedUserName), "seed account 'admin' should be listed");
            ctx.Check(shell.IsOverlayOpen, "function keys must be blocked while signed out");
            return Task.CompletedTask;
        }, StepOptions.Shot);

        await h.StepAsync("SignIn", "SeedAccountAsksForPassword", async ctx =>
        {
            shell.SignInUserName = UserDirectory.SeedUserName;
            shell.SignInPassword = string.Empty;
            await h.RunAsync(shell.SignInCommand);
            ctx.Check(shell.NeedsNewPassword, "seed account without password should switch to 'set password first'");
            ctx.Check(!shell.IsSignedIn, "must not be signed in before a password is set");
        }, StepOptions.Shot);

        if (!this.signInOnly)
        {
            await h.StepAsync("SignIn", "EmptyPasswordRefused", async ctx =>
            {
                shell.NewPassword = string.Empty;
                shell.ConfirmPassword = string.Empty;
                await h.RunAsync(shell.SignInCommand);
                ctx.Check(!shell.IsSignedIn, "empty new password must be refused");
                ctx.Check(shell.SignInMessage.Length > 0, "a reason should be shown");
            });

            await h.StepAsync("SignIn", "MismatchRefused", async ctx =>
            {
                shell.NewPassword = SelfTestAccounts.AdminPassword;
                shell.ConfirmPassword = SelfTestAccounts.AdminPassword + "x";
                await h.RunAsync(shell.SignInCommand);
                ctx.Check(!shell.IsSignedIn, "mismatching confirmation must be refused");
                ctx.Check(shell.SignInMessage.Length > 0, "a reason should be shown");
            });
        }

        await h.StepAsync("SignIn", "SetPasswordAndSignIn", async ctx =>
        {
            shell.NewPassword = SelfTestAccounts.AdminPassword;
            shell.ConfirmPassword = SelfTestAccounts.AdminPassword;
            await h.RunAsync(shell.SignInCommand);
            ctx.Check(shell.IsSignedIn, "should be signed in after setting the first password");
            ctx.Check(!shell.IsSignInOpen, "sign-in overlay should close");
            ctx.Check(shell.CanManageUsers, "manufacturer account should be able to manage users");
            ctx.Note("home page " + shell.CurrentPage.Key);
        }, StepOptions.Shot);

        if (this.signInOnly)
        {
            return;
        }

        await h.StepAsync("Users", "OpenAdmin", async ctx =>
        {
            await h.RunAsync(shell.OpenUserAdminCommand);
            ctx.Check(shell.IsUserAdminOpen, "user admin overlay should open");
        }, StepOptions.Shot);

        await h.StepAsync("Users", "CreateOperator", async ctx =>
        {
            shell.NewUserName = SelfTestAccounts.OperatorName;
            shell.NewUserPassword = SelfTestAccounts.OperatorPassword;
            shell.NewUserRole = UserRole.Operator;
            await h.RunAsync(shell.CreateUserCommand);
            ctx.Check(shell.UserAccounts.Any(a => a.UserName == SelfTestAccounts.OperatorName), "new operator should be listed");
        });

        await h.StepAsync("Users", "DuplicateNameRefused", async ctx =>
        {
            shell.NewUserName = SelfTestAccounts.OperatorName;
            shell.NewUserPassword = SelfTestAccounts.OperatorPassword;
            await h.RunAsync(shell.CreateUserCommand);
            ctx.Check(shell.UserAccounts.Count(a => a.UserName == SelfTestAccounts.OperatorName) == 1, "duplicate must not be created");
        }, StepOptions.Expect(AlarmLog.DomainFailureResourceKey, AlarmLog.UnexpectedFailureResourceKey, "*"));

        await h.StepAsync("Users", "ResetOperatorPassword", async ctx =>
        {
            shell.SelectedUserAccount = shell.UserAccounts.First(a => a.UserName == SelfTestAccounts.OperatorName);
            await h.RunAsync(shell.ResetUserPasswordCommand);
            UserAccount? reset = shell.UserAccounts.FirstOrDefault(a => a.UserName == SelfTestAccounts.OperatorName);
            ctx.Check(reset is not null, "operator should still exist after a password reset");
            ctx.Check(reset!.MustSetPassword, "after a reset the operator must set a new password at next sign-in");
        });

        await h.StepAsync("Users", "LastManufacturerCannotBeDeleted", async ctx =>
        {
            shell.SelectedUserAccount = shell.UserAccounts.First(a => a.UserName == UserDirectory.SeedUserName);
            await h.RunAsync(shell.DeleteUserCommand);
            ctx.Check(shell.UserAccounts.Any(a => a.UserName == UserDirectory.SeedUserName), "the last manufacturer account must survive");
        }, StepOptions.Expect(AlarmLog.DomainFailureResourceKey));

        await h.StepAsync("Users", "DeleteOperator", async ctx =>
        {
            shell.SelectedUserAccount = shell.UserAccounts.First(a => a.UserName == SelfTestAccounts.OperatorName);
            await h.RunAsync(shell.DeleteUserCommand);
            ctx.Check(shell.UserAccounts.All(a => a.UserName != SelfTestAccounts.OperatorName), "operator should be gone");
        });

        await h.StepAsync("Users", "CloseAdmin", async ctx =>
        {
            await h.RunAsync(shell.CloseUserAdminCommand);
            ctx.Check(!shell.IsUserAdminOpen, "user admin overlay should close");
        });

        await h.StepAsync("SignIn", "SignOutAndWrongPasswordRejected", async ctx =>
        {
            await h.RunAsync(shell.SignOutCommand);
            ctx.Check(shell.IsSignInOpen && !shell.IsSignedIn, "sign-out should reopen the sign-in overlay");
            shell.SignInUserName = UserDirectory.SeedUserName;
            shell.SignInPassword = "wrong";
            await h.RunAsync(shell.SignInCommand);
            ctx.Check(!shell.IsSignedIn, "wrong password must be rejected");
            ctx.Check(shell.SignInMessage.Length > 0, "a rejection message should be shown");
        });

        await h.StepAsync("SignIn", "SignInAgain", async ctx =>
        {
            shell.SignInPassword = SelfTestAccounts.AdminPassword;
            await h.RunAsync(shell.SignInCommand);
            ctx.Check(shell.IsSignedIn, "should sign in with the password set earlier");
        });
    }
}

/// <summary>收尾：签退后功能键必须全部不透传。</summary>
internal sealed class SessionTailSuite : ISelfTestSuite
{
    public string Name => "SessionTail";

    public async Task RunAsync(SelfTestHarness h)
    {
        await h.StepAsync("SignOut", "KeysBlockedWhenSignedOut", async ctx =>
        {
            await h.RunAsync(h.Shell.SignOutCommand);
            string before = h.Shell.CurrentPage.Key.ToString();
            h.Shell.PressFunctionKey(7);
            await h.SettleAsync();
            ctx.Check(h.Shell.CurrentPage.Key.ToString() == before, "the navigation key must do nothing while signed out");
            ctx.Check(h.Shell.IsSignInOpen, "sign-in overlay should be open");
        }, StepOptions.Shot);
    }
}
