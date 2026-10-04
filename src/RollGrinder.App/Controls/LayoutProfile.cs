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

    /// <summary>诊断 › 变量监视的"值"列宽：表头和行模板共用（模板里只能用 x:Static）。</summary>
    public static GridLength TagValueColumn => new(Math.Round(360 * Scale));

    /// <summary>诊断 › 变量监视的"状态"列宽。</summary>
    public static GridLength TagStateColumn => new(Math.Round(120 * Scale));

    /// <summary>库列表的版本列宽（表头与行模板共用；模板里只能用 x:Static）。</summary>
    /// <summary>上排位置 / 主轴块的轴名列。</summary>
    public static GridLength AxisNameColumn => new(Math.Round(48 * Scale));

    /// <summary>上排主轴块的倍率列。</summary>
    public static GridLength OverrideColumn => new(Math.Round(64 * Scale));

    /// <summary>上排辅助功能块的组名宽。</summary>
    public static double StatusGroupColumnWidth => Math.Round(56 * Scale);

    public static GridLength LibraryVersionColumn => new(Math.Round(90 * Scale));

    /// <summary>库列表的概况列宽。</summary>
    public static GridLength LibraryDetailColumn => new(Math.Round(280 * Scale));

    /// <summary>库列表的"在用"列宽。</summary>
    public static GridLength LibraryUsersColumn => new(Math.Round(80 * Scale));

    /// <summary>库列表的修改时刻列宽。</summary>
    public static GridLength LibraryModifiedColumn => new(Math.Round(180 * Scale));

    /// <summary>辊形段表的起点 / 长度 / 终点列宽（表头与行模板共用，紧凑档按比例缩）。</summary>
    public static GridLength SegmentNumberColumn => new(Math.Round(64 * Scale));

    /// <summary>轴读数行左边"轴名 + 角色"的最小宽（角色名更长时照样往右撑开）。</summary>
    public static double AxisReadoutLabelWidth => Math.Round(230 * Scale);

    /// <summary>"标签 + 值"两列表的标签宽（模板里用）。</summary>
    public static double LabelColumnWidth => Math.Round(160 * Scale);

    /// <summary>记录页 12 项指标的列数：标准档在右栏 2 列；紧凑档挪到曲线下面，4 列 × 3 行。模板里用 x:Static 取。</summary>
    public static int MetricColumns => Kind == LayoutKind.Compact ? 4 : 2;

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
        // 列宽、行宽各有各的类型：GridLength（Grid 行列）、DataGridLength（DataGrid 列）；给错类型 WPF 在建窗口时抛异常。
        if (type == typeof(GridLength))
        {
            return new GridLength(scaled);
        }

        if (type == typeof(System.Windows.Controls.DataGridLength))
        {
            return new System.Windows.Controls.DataGridLength(scaled);
        }

        return scaled;
    }
}

/// <summary>
/// XAML：Grid.Column="{ctl:ByLayout Standard=2, Compact=1}"。两档各取一个值，按目标属性的类型转换
/// （int、GridLength、double、Thickness……），用来在紧凑档把第三栏挪到第二栏下面这类重排。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class ByLayoutExtension : MarkupExtension
{
    public string Standard { get; set; } = string.Empty;

    public string Compact { get; set; } = string.Empty;

    public override object? ProvideValue(IServiceProvider serviceProvider)
    {
        string text = LayoutProfile.Kind == LayoutKind.Compact ? Compact : Standard;
        Type? type = (serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget)?.TargetProperty switch
        {
            DependencyProperty property => property.PropertyType,
            System.Reflection.PropertyInfo info => info.PropertyType,
            _ => null,
        };
        return type is null || type == typeof(string) || type == typeof(object)
            ? text
            : System.ComponentModel.TypeDescriptor.GetConverter(type).ConvertFromInvariantString(text);
    }
}
