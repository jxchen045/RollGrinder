using System;
using System.Linq;
using System.Windows;

namespace RollGrinder.App.Controls;

/// <summary>
/// 配色（hmi.json 的 <c>theme</c>）：light = 方案 B 浅色高对比（默认，车间强光下看得清）；
/// dark = 方案 A 深色工业（夜班、暗处）。两份色板色位完全相同（<c>ThemePaletteTests</c> 守着），只换色值。
/// 在任何窗口建出来之前调用；改了重启生效。
/// </summary>
public static class ThemeProfile
{
    private const string LightPalette = "Themes/Palette.Light.xaml";
    private const string DarkPalette = "Themes/Palette.Dark.xaml";

    /// <summary>当前是不是深色。</summary>
    public static bool IsDark { get; private set; }

    public static void Apply(Application application, string? setting)
    {
        ArgumentNullException.ThrowIfNull(application);
        IsDark = string.Equals(setting, "dark", StringComparison.OrdinalIgnoreCase);
        if (!IsDark)
        {
            return;
        }

        // 换掉色板后整套重载：字号、控件样式里的 StaticResource 在载入时就取色，必须在新色板之后重新载入。
        Uri[] sources = application.Resources.MergedDictionaries
            .Select(dictionary => dictionary.Source)
            .Where(source => source is not null)
            .Select(source => source!.OriginalString.EndsWith(LightPalette, StringComparison.OrdinalIgnoreCase)
                ? new Uri("pack://application:,,,/" + DarkPalette, UriKind.Absolute)
                : source!.IsAbsoluteUri ? source : new Uri("pack://application:,,,/" + source.OriginalString.TrimStart('/'), UriKind.Absolute))
            .ToArray();
        application.Resources.MergedDictionaries.Clear();
        foreach (Uri source in sources)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
        }
    }
}
