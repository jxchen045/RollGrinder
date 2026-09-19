using System;

namespace RollGrinder.App.Localization;

/// <summary>
/// 供 XAML 标记扩展取用的当前本地化器。
/// XAML 无法走构造函数注入，这里是唯一的静态入口，由组合根在启动时赋值。
/// </summary>
public static class LocalizationScope
{
    private static IStringLocalizer? current;

    public static IStringLocalizer Current =>
        current ?? throw new InvalidOperationException("LocalizationScope.Current has not been initialised yet.");

    public static void SetCurrent(IStringLocalizer localizer)
    {
        current = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }
}
