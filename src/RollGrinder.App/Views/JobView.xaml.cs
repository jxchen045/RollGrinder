using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;
using ScottPlot;

namespace RollGrinder.App.Views;

/// <summary>作业向导：五步拼一份作业，第 5 步画出"磨成什么样"，核对后下发。</summary>
public partial class JobView : UserControl
{
    private JobViewModel? viewModel;

    public JobView()
    {
        InitializeComponent();
        PlotTheme.Apply(ReviewPlot);
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        this.viewModel = DataContext as JobViewModel;
        if (this.viewModel is not null)
        {
            this.viewModel.ReviewCurveChanged += OnReviewCurveChanged;
            Redraw();
        }
    }

    private void Detach()
    {
        if (this.viewModel is not null)
        {
            this.viewModel.ReviewCurveChanged -= OnReviewCurveChanged;
        }
    }

    private void OnReviewCurveChanged(object? sender, EventArgs e) => Redraw();

    /// <summary>目标辊形（直径量 µm）沿辊身画出来，零线是公称直径。</summary>
    private void Redraw()
    {
        ReviewPlot.Plot.Clear();
        if (this.viewModel is { ReviewCurve.Count: > 1 } vm)
        {
            ReviewPlot.Plot.Add.HorizontalLine(0.0, 1f, PlotTheme.Curve(ReviewPlot, "Color.CurveTolerance"), LinePattern.Dotted);
            var target = ReviewPlot.Plot.Add.Scatter(
                vm.ReviewCurve.Select(point => point.BodyPositionMm).ToArray(),
                vm.ReviewCurve.Select(point => point.DiameterMicrometer).ToArray());
            target.LineWidth = 3f;
            target.MarkerSize = 0;
            target.Color = PlotTheme.Curve(ReviewPlot, "Color.CurveTarget");
            ReviewPlot.Plot.Axes.Left.Label.Text = vm.Localizer["Job_ReviewCurveAxis"];
            PlotTheme.ShowProfile(ReviewPlot, vm.ReviewCurve.Max(point => point.BodyPositionMm), vm.ReviewCurve.Select(point => point.DiameterMicrometer));
        }

        ReviewPlot.Refresh();
    }
}
