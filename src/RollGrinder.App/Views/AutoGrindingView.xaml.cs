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
            Redraw();
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (this.viewModel is not null)
        {
            this.viewModel.CurveChanged -= OnCurveChanged;
        }
    }

    private void OnCurveChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        if (this.viewModel is null || this.viewModel.CurvePoints.Count < 2)
        {
            CurvePlot.Plot.Clear();
            CurvePlot.Refresh();
            return;
        }

        double[] positions = this.viewModel.CurvePoints.Select(point => point.BodyPositionMm).ToArray();
        double[] micrometres = this.viewModel.CurvePoints.Select(point => point.DiameterMicrometer).ToArray();

        CurvePlot.Plot.Clear();
        CurvePlot.Plot.Add.HorizontalLine(0.0, 1f, Colors.Gray, LinePattern.Dotted);

        var line = CurvePlot.Plot.Add.Scatter(positions, micrometres);
        line.LineWidth = 2.5f;
        line.MarkerSize = 0;
        line.Color = Color.FromHex("#B3241C");

        CurvePlot.Plot.Axes.AutoScale();
        CurvePlot.Refresh();
    }
}
