using System;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Services.Session;

/// <summary>
/// 当前登录状态。顶栏显示，界面按它开放或收起功能。
///
/// **没登录就什么都不给**：<see cref="HasAtLeast"/> 在签退状态下一律返回 false，
/// 所以"未登录时按钮全灰"是一条规则决定的，不是每个页面各自记得去判断。
/// </summary>
public interface IUserSession
{
    /// <summary>登录了没有。</summary>
    bool IsSignedIn { get; }

    /// <summary>当前账号；没登录时为 null。</summary>
    UserAccount? CurrentUser { get; }

    /// <summary>当前权限；没登录时是最低级。</summary>
    UserRole CurrentRole { get; }

    /// <summary>登录状态变化（登录、签退、权限被改）。</summary>
    event EventHandler? SessionChanged;

    /// <summary>登录。口令校验由 <see cref="IUserDirectory"/> 负责，这里只记状态。</summary>
    void SignIn(UserAccount account);

    /// <summary>签退。</summary>
    void SignOut();

    /// <summary>当前登录状态是否达到所需级别。没登录一律不够。</summary>
    bool HasAtLeast(UserRole required);
}

/// <inheritdoc cref="IUserSession"/>
public sealed class UserSession : IUserSession
{
    public event EventHandler? SessionChanged;

    public bool IsSignedIn => CurrentUser is not null;

    public UserAccount? CurrentUser { get; private set; }

    public UserRole CurrentRole => CurrentUser?.Role ?? UserRole.Operator;

    public void SignIn(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        CurrentUser = account;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        if (CurrentUser is null)
        {
            return;
        }

        CurrentUser = null;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HasAtLeast(UserRole required) => IsSignedIn && CurrentRole >= required;
}
