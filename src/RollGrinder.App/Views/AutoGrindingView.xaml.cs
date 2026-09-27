using System;
using System.Linq;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;
using ScottPlot;

namespace RollGrinder.App.Views;

/// <summary>
/// 自动磨削页。图表不适合纯绑定，这里只负责把视图模型给的点画出来，
/// 顺带画公差带与零线。
/// </summary>
public partial class AutoGrindingView : UserControl
{
    private AutoGrindingViewModel? viewModel;

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
        line.Color = Color.FromHex("#1F5FA8");
        StrokePlot.Plot.Axes.Left.Label.Text = this.viewModel.Localizer["Comp_StrokeAxis"];
        StrokePlot.Plot.Axes.Bottom.Label.Text = this.viewModel.Localizer["Comp_ColumnVersion"];
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
        if (this.viewModel.CurveShowsTolerance)
        {
            double tolerance = this.viewModel.ToleranceMicrometer;
            foreach (double edge in new[] { tolerance, -tolerance })
            {
                CurvePlot.Plot.Add.HorizontalLine(edge, 1f, Color.FromHex("#9AA0A6"), LinePattern.Dashed);
            }
        }

        var line = CurvePlot.Plot.Add.Scatter(positions, values);
        line.LineWidth = 2.5f;
        line.MarkerSize = 0;
        line.Color = Color.FromHex("#B3241C");

        CurvePlot.Plot.Axes.Left.Label.Text = this.viewModel.CurveYAxisLabel;
        CurvePlot.Plot.Axes.AutoScale();
        CurvePlot.Refresh();
    }
}
