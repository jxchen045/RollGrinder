using System;
using System.Windows;
using System.Windows.Markup;

namespace RollGrinder.App.Controls;

/// <summary>
/// 两档固定画布：标准档 1920×1080、紧凑档 1366×768。启动时按 hmi.json 的 layout 与主屏大小选一档，
/// 整个外壳画在该档的画布上；实际屏幕不同就整体等比缩放（两块目标屏上正好是 1:1）。
///
/// 字号与外壳尺寸在 Themes/Layout.Standard.xaml，紧凑档用 Layout.Compact.xaml 覆盖；
/// 页面里剩下的固定尺寸写成 {ctl:Px 330}，紧凑档乘 <see cref="Scale"/>。
/// </summary>
public static class LayoutProfile
{
    /// <summary>紧凑档工作区与标准档之比（1146 / 1640 ≈ 580 / 846 ≈ 0.7）。</summary>
    public const double CompactScale = 0.7;

    public static LayoutKind Kind { get; private set; } = LayoutKind.Standard;

    public static bool FullScreen { get; private set; } = true;

    public static double Scale => Kind == LayoutKind.Compact ? CompactScale : 1.0;

    public static double CanvasWidth => Kind == LayoutKind.Compact ? 1366 : 1920;

    public static double CanvasHeight => Kind == LayoutKind.Compact ? 768 : 1080;

    /// <summary>在任何窗口建出来之前调用：定档，紧凑档把尺寸字典叠上去。</summary>
    public static void Apply(Application application, string? setting, bool fullScreen)
    {
        ArgumentNullException.ThrowIfNull(application);
        Kind = LayoutChooser.Choose(setting, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        FullScreen = fullScreen;
        if (Kind == LayoutKind.Compact)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Layout.Compact.xaml", UriKind.Absolute),
            });
        }
    }
}

/// <summary>
/// XAML：Width="{ctl:Px 330}"。标准档原样，紧凑档乘 <see cref="LayoutProfile.Scale"/>。
/// 用在页面里还没改成比例的固定栏宽、行高上，免得 1366 下几栏加起来超出工作区。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class PxExtension : MarkupExtension
{
    public PxExtension()
    {
    }

    public PxExtension(double value)
    {
        Value = value;
    }

    [ConstructorArgument("value")]
    public double Value { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        double scaled = Math.Round(Value * LayoutProfile.Scale);
        Type? type = (serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget)?.TargetProperty switch
        {
            DependencyProperty property => property.PropertyType,
            System.Reflection.PropertyInfo info => info.PropertyType,
            _ => null,
        };
        return type == typeof(GridLength) ? new GridLength(scaled) : scaled;
    }
}
