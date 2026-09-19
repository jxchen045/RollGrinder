using System;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Services.Session;

/// <summary>当前操作权限。顶栏显示，界面按它开放或收起功能。</summary>
public interface IUserSession
{
    /// <summary>当前权限。</summary>
    UserRole CurrentRole { get; }

    /// <summary>权限变化。</summary>
    event EventHandler? RoleChanged;

    /// <summary>切换权限。口令校验由调用方负责。</summary>
    void SetRole(UserRole role);

    /// <summary>当前权限是否达到所需级别。</summary>
    bool HasAtLeast(UserRole required);
}

/// <inheritdoc cref="IUserSession"/>
public sealed class UserSession : IUserSession
{
    public UserSession(UserRole initialRole)
    {
        CurrentRole = initialRole;
    }

    public event EventHandler? RoleChanged;

    public UserRole CurrentRole { get; private set; }

    public void SetRole(UserRole role)
    {
        if (CurrentRole == role)
        {
            return;
        }

        CurrentRole = role;
        RoleChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HasAtLeast(UserRole required) => CurrentRole >= required;
}
