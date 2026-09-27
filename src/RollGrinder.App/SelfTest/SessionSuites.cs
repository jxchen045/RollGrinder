using System.Linq;
using System.Threading.Tasks;
using RollGrinder.App.Navigation;
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
            ctx.Check(h.IsShownOnScreen("SignInOverlay"), "the sign-in dialog must actually be visible on screen, not just flagged open");
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
            ctx.Check(!h.IsShownOnScreen("SignInOverlay"), "the sign-in dialog should be gone from the screen");
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
            ctx.Check(h.IsShownOnScreen("UserAdminOverlay"), "the user admin dialog must actually be visible on screen");
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
        }, StepOptions.Expect(UserDirectory.AlreadyExistsResourceKey));

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
        }, StepOptions.Expect(UserDirectory.LastManufacturerResourceKey));

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

/// <summary>
/// 权限细分（修改稿 Q9）：操作者能选作业、下发、运行，辊形、程序、标定、补偿、配置只能看；
/// 管理员能改辊形、程序、标定；制造商全都能改。临时建一个操作者账号，查完删掉。
/// </summary>
internal sealed class PermissionSuite : ISelfTestSuite
{
    private const string OperatorName = "selftest-perm-op";
    private const string OperatorPassword = "SelfTest-Perm-1";

    public string Name => "Permissions";

    public async Task RunAsync(SelfTestHarness h)
    {
        ShellViewModel shell = h.Shell;
        await h.RecoverAsync();

        await h.StepAsync("Operator", "CreateAndSignIn", async ctx =>
        {
            await h.RunAsync(shell.OpenUserAdminCommand);
            shell.NewUserName = OperatorName;
            shell.NewUserPassword = OperatorPassword;
            shell.NewUserRole = UserRole.Operator;
            await h.RunAsync(shell.CreateUserCommand);
            await h.RunAsync(shell.CloseUserAdminCommand);

            await h.RunAsync(shell.SignOutCommand);
            shell.SignInUserName = OperatorName;
            shell.SignInPassword = OperatorPassword;
            await h.RunAsync(shell.SignInCommand);
            ctx.Check(shell.IsSignedIn && !shell.IsSignInOpen, "the operator should be signed in");
        });

        await h.StepAsync("Operator", "LibrariesAreReadOnly", async ctx =>
        {
            ProfileViewModel profile = h.Page<ProfileViewModel>();
            StepsViewModel steps = h.Page<StepsViewModel>();
            ctx.Check(profile.IsRoleLocked && profile.IsReadOnly, "an operator must not edit profiles");
            ctx.Check(steps.IsRoleLocked && !steps.CanSave, "an operator must not edit programs");
            ctx.Check(!h.Page<SettingsViewModel>().CanEdit, "an operator must not edit calibration");
            ctx.Check(!h.Page<AutoGrindingViewModel>().CanEditCompensation, "an operator must not edit the compensation tuning");
            ctx.Check(!h.Page<DiagnosticsViewModel>().CanEditMachineConfig && !h.Page<DiagnosticsViewModel>().CanEditTagMap,
                "an operator must not edit the machine config or tag map");
            ctx.Check(!shell.CanManageUsers, "an operator must not manage accounts");
            ctx.Check(!h.Page<JobViewModel>().IsRoleLocked, "an operator builds and downloads jobs");

            await h.GoToAsync(PageKey.Steps, ctx);
            ctx.Check(!h.IsKeyUsable(h.IndexOfKey("Fn_SaveProgram")), "the save key should be greyed out for an operator");
            ctx.Check(h.IsShownOnScreen("RoleLockBadge"), "the top bar should say which role is needed");
            ctx.Note(steps.RoleLockText);
            h.TryScreenshot("permission-operator-steps");
        });

        await h.StepAsync("Manufacturer", "SignBackInAndClean", async ctx =>
        {
            await h.RunAsync(shell.SignOutCommand);
            shell.SignInUserName = UserDirectory.SeedUserName;
            shell.SignInPassword = SelfTestAccounts.AdminPassword;
            await h.RunAsync(shell.SignInCommand);
            ctx.Check(shell.IsSignedIn, "the manufacturer should sign in again");
            ctx.Check(!h.Page<ProfileViewModel>().IsRoleLocked && h.Page<DiagnosticsViewModel>().CanEditMachineConfig,
                "the manufacturer may edit everything");

            await h.RunAsync(shell.OpenUserAdminCommand);
            shell.SelectedUserAccount = shell.UserAccounts.FirstOrDefault(a => a.UserName == OperatorName);
            if (shell.SelectedUserAccount is not null)
            {
                await h.RunAsync(shell.DeleteUserCommand);
            }

            await h.RunAsync(shell.CloseUserAdminCommand);
            ctx.Check(shell.UserAccounts.All(a => a.UserName != OperatorName), "the temporary operator should be removed");
        });
    }
}
