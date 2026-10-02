using System;
using System.Collections.Generic;
using RollGrinder.App.Controls;
using System.Windows;
using System.Windows.Controls;
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
        // 图表通则（界面修订稿 v3 1.2）：只画主刻度网格、浅色、在数据下层；不画次网格。
        plot.Grid.MajorLineColor = PaletteColor(control, "Color.PlotGridMinor");
        plot.Grid.MinorLineWidth = 0;
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

        EnableTouch(control);
        control.Loaded += (_, _) => AddResetButton(control);
        control.SizeChanged += (_, _) =>
        {
            if (Views.GetOrCreateValue(control).FixedAspect)
            {
                ApplyAspect(control);
                control.Refresh();
            }
        };
    }

    /// <summary>
    /// 画面刷新数据后调它代替 <c>Axes.AutoScale()</c>：人手缩放 / 拖过图，就保留人看的那一段，
    /// 直到按图角的"复位视图"（界面最终稿 4.6）。
    /// </summary>
    public static void AutoScale(WpfPlot control)
    {
        if (!Views.GetOrCreateValue(control).UserAdjusted)
        {
            control.Plot.Axes.AutoScale();
        }
    }

    /// <summary>复位视图：回到规范范围（辊形图、偏差图），其余回到自动缩放。</summary>
    public static void ResetView(WpfPlot control)
    {
        PlotState state = Views.GetOrCreateValue(control);
        state.UserAdjusted = false;
        if (state.Home is { } home)
        {
            control.Plot.Axes.SetLimits(home.Left, home.Right, home.Bottom, home.Top);
        }
        else
        {
            control.Plot.Axes.AutoScale();
        }

        control.Refresh();
    }

    /// <summary>
    /// 辊形图（分辨率适配方案第 4 节）：X 整根辊身 0…L，Y 由目标辊形定（直径量 µm），绘图区固定 4 : 1。
    /// 同一条辊形在辊形页、库、作业、记录里形状完全一样。
    /// </summary>
    public static void ShowProfile(WpfPlot control, double bodyLengthMm, IEnumerable<double> targetMicrometer)
    {
        (double low, double high) = ChartRanges.Profile(targetMicrometer);
        Views.GetOrCreateValue(control).MaxAspect = ChartRanges.PlotAspect;
        SetHome(control, bodyLengthMm, low, high);
    }

    /// <summary>
    /// 偏差图（误差等）：X 整根辊身，Y 以 0 为中心对称，半幅 = max(2 × 公差, 数据)。
    /// 绘图区铺满宽度，宽高比 4 : 1 – 8 : 1（纵轴按公差定，不比形状，不必死守 4 : 1）。
    /// </summary>
    public static void ShowDeviation(WpfPlot control, double bodyLengthMm, double toleranceMicrometer, IEnumerable<double> values)
    {
        (double low, double high) = ChartRanges.Deviation(toleranceMicrometer, values);
        Views.GetOrCreateValue(control).MaxAspect = ChartRanges.DeviationMaxAspect;
        SetHome(control, bodyLengthMm, low, high);
    }

    /// <summary>
    /// 两根轴的名称与单位（图表通则）：X 写在刻度下方正中，Y 写在纵轴旁。每张图都要写，
    /// 不写就分不清是直径量还是半径量、mm 还是 µm。
    /// </summary>
    public static void AxisTitles(WpfPlot control, string bottom, string left)
    {
        control.Plot.Axes.Bottom.Label.Text = bottom;
        control.Plot.Axes.Left.Label.Text = left;
    }

    /// <summary>其他沿辊身的量（圆度、偏心、电流）：X 整根辊身，Y 按数据取整，绘图区 4 : 1。</summary>
    public static void ShowAlongBody(WpfPlot control, double bodyLengthMm, IEnumerable<double> values)
    {
        (double low, double high) = ChartRanges.Data(values);
        Views.GetOrCreateValue(control).MaxAspect = ChartRanges.PlotAspect;
        SetHome(control, bodyLengthMm, low, high);
    }

    private static void SetHome(WpfPlot control, double bodyLengthMm, double low, double high)
    {
        PlotState state = Views.GetOrCreateValue(control);
        state.Home = new AxisLimits(0.0, Math.Max(bodyLengthMm, 1.0), low, high);
        state.FixedAspect = true;
        if (!state.UserAdjusted)
        {
            control.Plot.Axes.SetLimits(0.0, Math.Max(bodyLengthMm, 1.0), low, high);
        }

        ApplyAspect(control);
    }

    /// <summary>绘图区按 4 : 1 居中，容器比例不同就四周留白，不拉伸。</summary>
    private static void ApplyAspect(WpfPlot control)
    {
        if (!Views.GetOrCreateValue(control).FixedAspect || control.ActualWidth <= 0 || control.ActualHeight <= 0)
        {
            return;
        }

        bool labelled = !string.IsNullOrEmpty(control.Plot.Axes.Left.Label.Text);
        float left = (TickFontSize * 4.2f) + (labelled ? AxisLabelFontSize * 1.8f : 0f);
        float bottom = (TickFontSize * 2.4f) + (string.IsNullOrEmpty(control.Plot.Axes.Bottom.Label.Text) ? 0f : AxisLabelFontSize * 1.8f);
        const float top = 14f;
        const float right = 20f;
        double availableWidth = Math.Max(control.ActualWidth - left - right, 10.0);
        double availableHeight = Math.Max(control.ActualHeight - top - bottom, 10.0);
        double maxAspect = Math.Max(Views.GetOrCreateValue(control).MaxAspect, ChartRanges.PlotAspect);
        double width = Math.Min(availableWidth, availableHeight * maxAspect);
        double height = Math.Min(availableHeight, width / ChartRanges.PlotAspect);
        float padX = (float)((availableWidth - width) / 2.0);
        float padY = (float)((availableHeight - height) / 2.0);
        control.Plot.Layout.Fixed(new PixelPadding(left + padX, right + padX, bottom + padY, top + padY));
    }

    /// <summary>
    /// 双指缩放、单指拖动（界面最终稿 4.6）。触摸交给 WPF 的操作事件自己算：ScottPlot 只认鼠标，
    /// 触摸提升成鼠标只有单指拖，没有双指缩放。鼠标拖、滚轮缩放照旧走 ScottPlot 自己的。
    /// </summary>
    private static void EnableTouch(WpfPlot control)
    {
        control.IsManipulationEnabled = true;
        control.ManipulationStarting += (_, e) =>
        {
            e.ManipulationContainer = control;
            e.Mode = System.Windows.Input.ManipulationModes.Translate | System.Windows.Input.ManipulationModes.Scale;
            e.Handled = true;
        };
        control.ManipulationDelta += (_, e) =>
        {
            PixelRect data = control.Plot.LastRender.DataRect;
            AxisLimits limits = control.Plot.Axes.GetLimits();
            if (data.Width <= 0 || data.Height <= 0 || limits.HorizontalSpan <= 0 || limits.VerticalSpan <= 0)
            {
                return;
            }

            // 作图用的像素已按屏幕缩放折回 DIP（见 MatchDisplayScale），和操作事件的坐标是同一种单位。
            double perX = limits.HorizontalSpan / data.Width;
            double perY = limits.VerticalSpan / data.Height;
            double originX = limits.Left + ((e.ManipulationOrigin.X - data.Left) * perX);
            double originY = limits.Top - ((e.ManipulationOrigin.Y - data.Top) * perY);
            double scale = e.DeltaManipulation.Scale.X > 0 ? e.DeltaManipulation.Scale.X : 1.0;
            double shiftX = -e.DeltaManipulation.Translation.X * perX;
            double shiftY = e.DeltaManipulation.Translation.Y * perY;

            control.Plot.Axes.SetLimits(
                originX - ((originX - limits.Left) / scale) + shiftX,
                originX + ((limits.Right - originX) / scale) + shiftX,
                originY - ((originY - limits.Bottom) / scale) + shiftY,
                originY + ((limits.Top - originY) / scale) + shiftY);
            Views.GetOrCreateValue(control).UserAdjusted = true;
            control.Refresh();
            e.Handled = true;
        };

        // 鼠标拖、滚轮缩放也算人手调过视图。
        control.PreviewMouseWheel += (_, _) => Views.GetOrCreateValue(control).UserAdjusted = true;
        control.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed || e.RightButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                Views.GetOrCreateValue(control).UserAdjusted = true;
            }
        };
    }

    /// <summary>图右上角放一个"⟲ 复位视图"。图的外层是面板就叠在同一格里，是边框就包一层网格。</summary>
    private static void AddResetButton(WpfPlot control)
    {
        PlotState state = Views.GetOrCreateValue(control);
        if (state.HasResetButton)
        {
            return;
        }

        var button = new Button
        {
            Content = Localization.LocalizationScope.Current["Plot_ResetView"],
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Padding = new Thickness(10, 2, 10, 2),
            MinHeight = 36,
            Style = control.TryFindResource("SecondaryButton") as Style,
        };
        button.Click += (_, _) => ResetView(control);

        // 图藏起来（例如记录页没数据）时按钮跟着藏。
        button.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = control });

        switch (control.Parent)
        {
            case System.Windows.Controls.Grid grid:
                System.Windows.Controls.Grid.SetRow(button, System.Windows.Controls.Grid.GetRow(control));
                System.Windows.Controls.Grid.SetColumn(button, System.Windows.Controls.Grid.GetColumn(control));
                System.Windows.Controls.Grid.SetRowSpan(button, System.Windows.Controls.Grid.GetRowSpan(control));
                System.Windows.Controls.Grid.SetColumnSpan(button, System.Windows.Controls.Grid.GetColumnSpan(control));
                grid.Children.Add(button);
                state.HasResetButton = true;
                break;
            case Decorator decorator:
                decorator.Child = null;
                decorator.Child = Wrap(control, button);
                state.HasResetButton = true;
                break;
            case Panel panel:
                // 例如 DockPanel 的最后一格（填满）：在原位置换成"图 + 按钮"的一格，停靠方向跟着搬过去。
                int index = panel.Children.IndexOf(control);
                Dock dock = DockPanel.GetDock(control);
                panel.Children.RemoveAt(index);
                System.Windows.Controls.Grid host = Wrap(control, button);
                DockPanel.SetDock(host, dock);
                panel.Children.Insert(index, host);
                state.HasResetButton = true;
                break;
        }
    }

    private static System.Windows.Controls.Grid Wrap(WpfPlot control, Button button)
    {
        var host = new System.Windows.Controls.Grid();
        host.Children.Add(control);
        host.Children.Add(button);
        return host;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WpfPlot, PlotState> Views = new();

    private sealed class PlotState
    {
        public bool UserAdjusted { get; set; }

        public AxisLimits? Home { get; set; }

        public bool FixedAspect { get; set; }

        public bool HasResetButton { get; set; }

        /// <summary>绘图区最宽的宽高比：辊形图 4（固定），偏差图 8（铺满宽度）。</summary>
        public double MaxAspect { get; set; } = ChartRanges.PlotAspect;
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

    /// <summary>曲线色位（最终稿 4.3），例如 Color.CurveMeasured。各页的同一种曲线用同一种颜色。</summary>
    public static Color Curve(FrameworkElement owner, string key) => PaletteColor(owner, key);

    private static Color PaletteColor(FrameworkElement owner, string key)
    {
        // 设计器里或资源没加载上时找不到色位，退回黑色也比抛异常强：图照样画得出来。
        System.Windows.Media.Color c = owner.TryFindResource(key) is System.Windows.Media.Color found
            ? found
            : System.Windows.Media.Colors.Black;
        return new Color(c.R, c.G, c.B, c.A);
    }
}
