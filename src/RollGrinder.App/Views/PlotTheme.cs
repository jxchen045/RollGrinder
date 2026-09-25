using System;
using System.Windows;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;
using ScottPlot;
using ScottPlot.WPF;

namespace RollGrinder.App.Views;

/// <summary>
/// 三张曲线图（自动磨削、辊形预览、磨削记录）共用的外观：刻度字、坐标轴标题、图例的字号与颜色，
/// 网格与底色。以前用的是 ScottPlot 默认值——刻度字在工控机上只有两三毫米高，隔一臂远看不清。
///
/// 颜色运行时从 Palette.Light.xaml 的色位读，不在这里另写一份；字体用系统里一定有的雅黑（ScottPlot 用自己的渲染器，
/// 读不到编译进程序集的 Noto Sans SC），数字照样清楚，中文轴标题不会变成方块。
/// </summary>
internal static class PlotTheme
{
    /// <summary>刻度数字。</summary>
    public const float TickFontSize = 15f;

    /// <summary>坐标轴标题（单位）。</summary>
    public const float AxisLabelFontSize = 16f;

    /// <summary>图例。</summary>
    public const float LegendFontSize = 15f;

    private const string FontName = "Microsoft YaHei";

    public static void Apply(WpfPlot control)
    {
        Color text = PaletteColor(control, "Color.TextSecondary");
        Plot plot = control.Plot;
        plot.Font.Set(FontName);

        foreach (IAxis axis in new IAxis[] { plot.Axes.Bottom, plot.Axes.Left })
        {
            axis.TickLabelStyle.FontSize = TickFontSize;
            axis.TickLabelStyle.ForeColor = text;
            axis.Label.FontSize = AxisLabelFontSize;
            axis.Label.ForeColor = text;
            axis.Label.Bold = false;
        }

        plot.Legend.FontSize = LegendFontSize;
        plot.Grid.MajorLineColor = PaletteColor(control, "Color.PlotGridMinor");
        plot.DataBackground.Color = PaletteColor(control, "Color.PlotBackground");
        plot.FigureBackground.Color = PaletteColor(control, "Color.Surface");

        // 控件本身没有 DpiChanged 事件（只有窗口有）；拖到另一块缩放不同的屏上时由窗口的事件带过来。
        // 页面每次切换都会新建视图，离开时退订，免得窗口一直拽着旧图不放。
        Window? host = null;
        control.Loaded += (_, _) =>
        {
            MatchDisplayScale(control);
            host = Window.GetWindow(control);
            if (host is not null)
            {
                host.DpiChanged -= OnWindowDpiChanged;
                host.DpiChanged += OnWindowDpiChanged;
            }
        };
        control.Unloaded += (_, _) =>
        {
            if (host is not null)
            {
                host.DpiChanged -= OnWindowDpiChanged;
                host = null;
            }
        };

        void OnWindowDpiChanged(object sender, DpiChangedEventArgs e) => MatchDisplayScale(control);
    }

    /// <summary>
    /// 让图里的字和线跟着屏幕缩放走。
    ///
    /// ScottPlot 5.0.56 的 WPF 控件按物理像素作画，却不乘屏幕缩放：工控机 4K 屏开 200% 时，
    /// 15 号的刻度字实际只有 7.5 DIP——第四轮现场截图里坐标数字只有两三毫米高就是这个原因，
    /// 字号设多大都白设。这里把 <see cref="Plot.ScaleFactor"/> 设成当前缩放倍数，字号才是真的 DIP。
    /// </summary>
    public static void MatchDisplayScale(WpfPlot control)
    {
        double scale = VisualTreeHelper.GetDpi(control).DpiScaleX;
        if (scale <= 0 || Math.Abs(control.Plot.ScaleFactor - scale) < 0.01)
        {
            return;
        }

        control.Plot.ScaleFactor = scale;
        control.Refresh();
    }

    private static Color PaletteColor(FrameworkElement owner, string key)
    {
        // 设计器里或资源没加载上时找不到色位，退回黑色也比抛异常强：图照样画得出来。
        System.Windows.Media.Color c = owner.TryFindResource(key) is System.Windows.Media.Color found
            ? found
            : System.Windows.Media.Colors.Black;
        return new Color(c.R, c.G, c.B, c.A);
    }
}
