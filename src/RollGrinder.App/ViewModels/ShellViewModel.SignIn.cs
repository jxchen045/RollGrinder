using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Data;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>外壳的登录与用户管理（最终稿 5.14、F5、Q19）。</summary>
public sealed partial class ShellViewModel
{
    // ── 登录 ──────────────────────────────────────────────────────────────────
    //
    // 没登录就什么都不给：登录浮层是 IsOverlayOpen 的一部分，
    // 所以功能键与页面点击在签退状态下自动不透传，不用每个页面各自记得判断。

    /// <summary>登录浮层开着没有。启动时是开着的。</summary>
    [ObservableProperty]
    private bool isSignInOpen = true;

    /// <summary>登录了没有——顶栏按它显示用户名还是"未登录"。</summary>
    [ObservableProperty]
    private bool isSignedIn;

    /// <summary>当前用户名，顶栏显示。</summary>
    [ObservableProperty]
    private string userNameText = string.Empty;

    /// <summary>标题行用户名旁的小菜单（签退、用户管理）开着没有。</summary>
    [ObservableProperty]
    private bool isUserMenuOpen;

    /// <summary>
    /// 这个账号用数字 PIN 登录（操作者，最终稿 Q19）：登录框给数字键，不用打字。
    /// 管理员及以上用口令。
    /// </summary>
    [ObservableProperty]
    private bool isPinEntry;

    /// <summary>登录框里选/填的用户名。</summary>
    [ObservableProperty]
    private string signInUserName = string.Empty;

    /// <summary>登录框里的口令。由 PasswordBox 的事件推进来——WPF 不让绑 Password。</summary>
    [ObservableProperty]
    private string signInPassword = string.Empty;

    /// <summary>首次设口令时的新口令与确认。</summary>
    [ObservableProperty]
    private string newPassword = string.Empty;

    [ObservableProperty]
    private string confirmPassword = string.Empty;

    /// <summary>这个账号还没设过口令：登录框切到"先设一个口令"。</summary>
    [ObservableProperty]
    private bool needsNewPassword;

    /// <summary>常规登录（不是"先设口令"那一支）。给界面切换两块输入区用。</summary>
    public bool IsNormalSignIn => !NeedsNewPassword;

    partial void OnNeedsNewPasswordChanged(bool value) => OnPropertyChanged(nameof(IsNormalSignIn));

    /// <summary>登录框里的提示（口令不对、两次不一致……）；没有提示时为空。</summary>
    [ObservableProperty]
    private string signInMessage = string.Empty;

    /// <summary>库里有哪些用户名，登录框做成下拉，省得在触摸屏上打字。</summary>
    public ObservableCollection<string> KnownUserNames { get; } = new();

    /// <summary>账号列表，用户管理浮层用。</summary>
    public ObservableCollection<UserAccount> UserAccounts { get; } = new();

    [ObservableProperty]
    private bool isUserAdminOpen;

    [ObservableProperty]
    private UserAccount? selectedUserAccount;

    /// <summary>新建账号的用户名、口令与权限。</summary>
    [ObservableProperty]
    private string newUserName = string.Empty;

    [ObservableProperty]
    private string newUserPassword = string.Empty;

    [ObservableProperty]
    private UserRole newUserRole;

    /// <summary>管理员以上才看得到"用户管理"。</summary>
    public bool CanManageUsers => this.userSession.Can(Permission.ManageUsers);

    /// <summary>新用户可选的权限。</summary>
    public IReadOnlyList<UserRole> AssignableRoles { get; } =
        new[] { UserRole.Operator, UserRole.Administrator, UserRole.Manufacturer };

    /// <summary>
    /// 新用户的权限：三个分段键，一眼看全，按键和触摸都好按（修改稿原则 1：去掉下拉）。
    /// 以前是下拉，还直接显示英文枚举名。
    /// </summary>
    public ObservableCollection<ParameterChoiceViewModel> RoleChoices { get; } = new();

    /// <summary>登录框里已有的用户名：一人一个键，按一下填进用户名框；新名字照样手输。</summary>
    public ObservableCollection<ParameterChoiceViewModel> KnownUserChoices { get; } = new();

    partial void OnNewUserRoleChanged(UserRole value)
    {
        foreach (ParameterChoiceViewModel choice in RoleChoices)
        {
            choice.IsSelected = choice.Key == value.ToString();
        }
    }

    partial void OnSignInUserNameChanged(string value)
    {
        foreach (ParameterChoiceViewModel choice in KnownUserChoices)
        {
            choice.IsSelected = string.Equals(choice.Key, value, StringComparison.Ordinal);
        }

        IsPinEntry = UserAccounts.FirstOrDefault(a => string.Equals(a.UserName, value, StringComparison.Ordinal))?.Role
            == UserRole.Operator;
    }

    /// <summary>标题行上的用户：没登录就是登录框（本来就开着），登录了就开 / 关小菜单。</summary>
    [RelayCommand]
    private void PressUser()
    {
        if (!IsSignedIn)
        {
            IsSignInOpen = true;
            return;
        }

        IsUserMenuOpen = !IsUserMenuOpen;
    }

    /// <summary>PIN 只许数字，4–8 位：数字键盘能输完，也不至于短到一猜就中。</summary>
    private static bool IsValidPin(string pin) => pin.Length is >= 4 and <= 8 && pin.All(char.IsAsciiDigit);

    /// <summary>启动后把用户名列表拉进来，登录框的下拉才有东西可选。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        SignInMessage = string.Empty;

        try
        {
            if (NeedsNewPassword)
            {
                await CompleteFirstPasswordAsync(cancellationToken).ConfigureAwait(true);
                return;
            }

            SignInResult result = await this.userDirectory
                .SignInAsync(SignInUserName, SignInPassword, cancellationToken).ConfigureAwait(true);

            switch (result.Outcome)
            {
                case SignInOutcome.Succeeded:
                    Accept(result.Account!);
                    break;

                case SignInOutcome.PasswordNotSet:
                    // 首次启动种下的管理账号走这一支：先设口令再放行。
                    NeedsNewPassword = true;
                    SignInMessage = this.localizer["SignIn_SetPasswordFirst"];
                    break;

                default:
                    // 用户名不存在与口令不对不分开报——分开报等于告诉人哪个用户名存在。
                    SignInMessage = this.localizer["SignIn_Rejected"];
                    break;
            }
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    private async Task CompleteFirstPasswordAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(NewPassword))
        {
            SignInMessage = this.localizer["SignIn_PasswordEmpty"];
            return;
        }

        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            SignInMessage = this.localizer["SignIn_PasswordMismatch"];
            return;
        }

        if (IsPinEntry && !IsValidPin(NewPassword))
        {
            SignInMessage = this.localizer["SignIn_PinFormat"];
            return;
        }

        try
        {
            await this.userDirectory
                .SetPasswordAsync(SignInUserName, NewPassword, cancellationToken).ConfigureAwait(true);

            SignInResult result = await this.userDirectory
                .SignInAsync(SignInUserName, NewPassword, cancellationToken).ConfigureAwait(true);
            if (result.Account is not null)
            {
                Accept(result.Account);
            }
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    private void Accept(UserAccount account)
    {
        this.userSession.SignIn(account);
        IsSignedIn = true;
        UserNameText = account.UserName;
        IsSignInOpen = false;
        ClearSignInFields();
        OnPropertyChanged(nameof(CanManageUsers));
    }

    [RelayCommand]
    private void SignOut()
    {
        IsUserMenuOpen = false;
        this.interaction.Confirmations.Cancel();
        this.userSession.SignOut();
        IsSignedIn = false;
        UserNameText = string.Empty;
        IsUserAdminOpen = false;
        ClearSignInFields();
        IsSignInOpen = true;
        OnPropertyChanged(nameof(CanManageUsers));
    }

    private void ClearSignInFields()
    {
        SignInPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        NeedsNewPassword = false;
        SignInMessage = string.Empty;
    }

    private async Task RefreshUserNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<UserAccount> accounts =
                await this.userDirectory.ListAsync(cancellationToken).ConfigureAwait(true);

            KnownUserNames.Clear();
            KnownUserChoices.Clear();
            UserAccounts.Clear();
            foreach (UserAccount account in accounts)
            {
                KnownUserNames.Add(account.UserName);
                string userName = account.UserName;
                KnownUserChoices.Add(new ParameterChoiceViewModel(
                    userName, userName, new RelayCommand(() => SignInUserName = userName))
                {
                    IsSelected = string.Equals(userName, SignInUserName, StringComparison.Ordinal),
                });
                UserAccounts.Add(account);
            }

            if (string.IsNullOrEmpty(SignInUserName))
            {
                SignInUserName = KnownUserNames.FirstOrDefault() ?? string.Empty;
            }
            else
            {
                OnSignInUserNameChanged(SignInUserName);
            }
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    // ── 用户管理 ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenUserAdminAsync(CancellationToken cancellationToken)
    {
        if (!CanManageUsers)
        {
            return;
        }

        IsUserMenuOpen = false;
        await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
        SelectedUserAccount = UserAccounts.FirstOrDefault();
        IsUserAdminOpen = true;
    }

    [RelayCommand]
    private void CloseUserAdmin() => IsUserAdminOpen = false;

    [RelayCommand]
    private async Task CreateUserAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 口令留空表示"首次登录时再设"，和出厂那个管理账号一样。
            await this.userDirectory.CreateAsync(
                NewUserName,
                string.IsNullOrEmpty(NewUserPassword) ? null : NewUserPassword,
                NewUserRole,
                cancellationToken).ConfigureAwait(true);

            NewUserName = string.Empty;
            NewUserPassword = string.Empty;
            await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    [RelayCommand]
    private async Task DeleteUserAsync(CancellationToken cancellationToken)
    {
        if (SelectedUserAccount is null)
        {
            return;
        }

        try
        {
            await this.userDirectory
                .DeleteAsync(SelectedUserAccount.UserName, cancellationToken).ConfigureAwait(true);
            await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (DomainException ex)
        {
            // 最后一个制造商级账号不许删——删了这台机器就再也没人能管了。
            Alarms.RaiseException(ex);
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    /// <summary>把选中账号的口令清掉：下次登录时由本人重设，管理员看不到明文。</summary>
    [RelayCommand]
    private async Task ResetUserPasswordAsync(CancellationToken cancellationToken)
    {
        if (SelectedUserAccount is null)
        {
            return;
        }

        try
        {
            await this.userDirectory.DeleteAsync(SelectedUserAccount.UserName, cancellationToken).ConfigureAwait(true);
            await this.userDirectory.CreateAsync(
                SelectedUserAccount.UserName, null, SelectedUserAccount.Role, cancellationToken).ConfigureAwait(true);
            await RefreshUserNamesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }
}
