using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;
using ScottPlot;

namespace RollGrinder.App.Views;

/// <summary>库。辊形预览画沿辊身的直径量曲线。</summary>
public partial class LibraryView : UserControl
{
    private LibraryViewModel? viewModel;

    public LibraryView()
    {
        InitializeComponent();
        PlotTheme.Apply(PreviewPlot);
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        this.viewModel = DataContext as LibraryViewModel;
        if (this.viewModel is not null)
        {
            this.viewModel.PreviewChanged += OnPreviewChanged;
            Redraw();
        }
    }

    private void Detach()
    {
        if (this.viewModel is not null)
        {
            this.viewModel.PreviewChanged -= OnPreviewChanged;
        }
    }

    private void OnPreviewChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        PreviewPlot.Plot.Clear();
        if (this.viewModel is { PreviewCurve.Count: > 1 } vm)
        {
            PreviewPlot.Plot.Add.HorizontalLine(0.0, 1f, PlotTheme.Curve(PreviewPlot, "Color.CurveTolerance"), LinePattern.Dotted);
            var line = PreviewPlot.Plot.Add.Scatter(
                vm.PreviewCurve.Select(point => point.BodyPositionMm).ToArray(),
                vm.PreviewCurve.Select(point => point.DiameterMicrometer).ToArray());
            line.LineWidth = 3f;
            line.MarkerSize = 0;
            line.Color = PlotTheme.Curve(PreviewPlot, "Color.CurveTarget");
            PreviewPlot.Plot.Axes.Left.Label.Text = vm.Localizer["Job_ReviewCurveAxis"];
            PlotTheme.ShowProfile(PreviewPlot, vm.PreviewCurve.Max(point => point.BodyPositionMm), vm.PreviewCurve.Select(point => point.DiameterMicrometer));
        }

        PreviewPlot.Refresh();
    }
}
