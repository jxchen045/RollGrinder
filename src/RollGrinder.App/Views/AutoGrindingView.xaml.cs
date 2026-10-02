using System;
using System.Linq;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;
using ScottPlot;

namespace RollGrinder.App.Views;

/// <summary>
/// 自动磨削页。图表不适合纯绑定，这里只负责把视图模型给的点画出来。
/// 误差曲线上另画三样（最终稿 D4）：公差带填色、超差段加粗（5 px）、砂轮当前 Z 位置的橙色竖线。
/// 颜色取自 Palette.Light.xaml 的曲线色位（最终稿 4.3），各页一致。
/// </summary>
public partial class AutoGrindingView : UserControl
{
    private AutoGrindingViewModel? viewModel;
    private ScottPlot.Plottables.VerticalLine? wheelLine;

    public AutoGrindingView()
    {
        InitializeComponent();
        PlotTheme.Apply(CurvePlot);
        PlotTheme.Apply(StrokePlot);
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        Detach();
        this.viewModel = DataContext as AutoGrindingViewModel;
        if (this.viewModel is not null)
        {
            this.viewModel.CurveChanged += OnCurveChanged;
            this.viewModel.StrokeCurveChanged += OnStrokeCurveChanged;
            this.viewModel.WheelPositionChanged += OnWheelPositionChanged;
            Redraw();
            RedrawStrokes();
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (this.viewModel is not null)
        {
            this.viewModel.CurveChanged -= OnCurveChanged;
            this.viewModel.StrokeCurveChanged -= OnStrokeCurveChanged;
            this.viewModel.WheelPositionChanged -= OnWheelPositionChanged;
        }
    }

    /// <summary>砂轮走到哪了：只挪竖线，不重画整条曲线。</summary>
    private void OnWheelPositionChanged(object? sender, EventArgs e)
    {
        if (this.viewModel?.WheelPositionMm is double z && this.wheelLine is not null)
        {
            this.wheelLine.X = z;
            this.wheelLine.IsVisible = true;
            CurvePlot.Refresh();
        }
        else if (this.wheelLine is not null)
        {
            this.wheelLine.IsVisible = false;
            CurvePlot.Refresh();
        }
    }

    private void OnCurveChanged(object? sender, EventArgs e) => Redraw();

    private void OnStrokeCurveChanged(object? sender, EventArgs e) => RedrawStrokes();

    /// <summary>补偿子视图的收敛曲线：横轴行程版本，纵轴 NC 报的修正量（直径量 µm）。</summary>
    private void RedrawStrokes()
    {
        StrokePlot.Plot.Clear();
        if (this.viewModel is null || this.viewModel.StrokeCurve.Count == 0)
        {
            StrokePlot.Refresh();
            return;
        }

        double[] versions = this.viewModel.StrokeCurve.Select(point => point.Version).ToArray();
        double[] offsets = this.viewModel.StrokeCurve.Select(point => point.OffsetMicrometer).ToArray();
        StrokePlot.Plot.Add.HorizontalLine(0.0, 1f, Colors.Gray, LinePattern.Dotted);
        var line = StrokePlot.Plot.Add.Scatter(versions, offsets);
        line.LineWidth = 2.5f;
        line.MarkerSize = 6;
        line.Color = PlotTheme.Curve(StrokePlot, "Color.CurveCompensation");
        PlotTheme.AxisTitles(StrokePlot, this.viewModel.Localizer["Comp_ColumnVersion"], this.viewModel.Localizer["Comp_StrokeAxis"]);
        StrokePlot.Plot.Axes.AutoScale();
        StrokePlot.Refresh();
    }

    private void Redraw()
    {
        if (this.viewModel is null || this.viewModel.CurvePoints.Count < 2)
        {
            CurvePlot.Plot.Clear();
            CurvePlot.Refresh();
            return;
        }

        double[] positions = this.viewModel.CurvePoints.Select(point => point.BodyPositionMm).ToArray();
        double[] values = this.viewModel.CurvePoints.Select(point => point.Value).ToArray();

        CurvePlot.Plot.Clear();

        CurvePlot.Plot.Add.HorizontalLine(0.0, 1f, Colors.Gray, LinePattern.Dotted);

        // 公差带只有误差曲线才有意义：圆度、偏心、电流各有各的判据，
        // 画一条同样的带子会让人按错的尺子读数。
        double tolerance = this.viewModel.ToleranceMicrometer;
        bool showTolerance = this.viewModel.CurveShowsTolerance && tolerance > 0;
        if (showTolerance)
        {
            var band = CurvePlot.Plot.Add.HorizontalSpan(-tolerance, tolerance);
            band.FillStyle.Color = PlotTheme.Curve(CurvePlot, "Color.CurveToleranceBand");
            band.LineStyle.Width = 0;
            foreach (double edge in new[] { tolerance, -tolerance })
            {
                CurvePlot.Plot.Add.HorizontalLine(edge, 1f, PlotTheme.Curve(CurvePlot, "Color.CurveTolerance"), LinePattern.Dashed);
            }
        }

        Color measured = PlotTheme.Curve(CurvePlot, "Color.CurveMeasured");
        var line = CurvePlot.Plot.Add.Scatter(positions, values);
        line.LineWidth = 2.5f;
        line.MarkerSize = 0;
        line.Color = measured;

        // 超差段加粗到 5 px：一眼看出哪一段没磨到位。
        if (showTolerance)
        {
            foreach ((int first, int last) in Controls.CurveMath.ExcessRuns(positions, values, tolerance))
            {
                var excess = CurvePlot.Plot.Add.Scatter(positions[first..(last + 1)], values[first..(last + 1)]);
                excess.LineWidth = 5f;
                excess.MarkerSize = 0;
                excess.Color = measured;
            }
        }

        // 砂轮当前 Z 位置：橙色竖线，跟着机床走。
        this.wheelLine = CurvePlot.Plot.Add.VerticalLine(this.viewModel.WheelPositionMm ?? 0.0, 2f, PlotTheme.Curve(CurvePlot, "Color.CurveWheel"));
        this.wheelLine.IsVisible = this.viewModel.WheelPositionMm is not null;

        PlotTheme.AxisTitles(CurvePlot, this.viewModel.Localizer["Chart_AxisZ"], this.viewModel.CurveYAxisLabel);
        // 边磨边刷新：人手缩放 / 拖过就保留人看的那一段，按图角"复位视图"再回到规范范围。
        // 五条曲线共用 4 : 1 的框，切换时框不跳；误差按偏差图（公差带居中），其余按数据取整。
        double length = positions.Max();
        if (showTolerance)
        {
            PlotTheme.ShowDeviation(CurvePlot, length, tolerance, values);
        }
        else
        {
            PlotTheme.ShowAlongBody(CurvePlot, length, values);
        }
        CurvePlot.Refresh();
    }
}
