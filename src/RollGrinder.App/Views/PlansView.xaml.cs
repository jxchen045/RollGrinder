using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;
using ScottPlot;

namespace RollGrinder.App.Views;

/// <summary>方案列表。辊形图横轴以辊身中心为 0。</summary>
public partial class PlansView : UserControl
{
    private PlansViewModel? viewModel;

    public PlansView()
    {
        InitializeComponent();
        PlotTheme.Apply(PreviewPlot);
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        this.viewModel = DataContext as PlansViewModel;
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
            PreviewPlot.Plot.Add.VerticalLine(0.0, 1f, PlotTheme.Curve(PreviewPlot, "Color.CurveTolerance"), LinePattern.Dashed);
            var line = PreviewPlot.Plot.Add.Scatter(
                vm.PreviewCurve.Select(point => point.CenteredZMm).ToArray(),
                vm.PreviewCurve.Select(point => point.DiameterMicrometer).ToArray());
            line.LineWidth = 3f;
            line.MarkerSize = 0;
            line.Color = PlotTheme.Curve(PreviewPlot, "Color.CurveTarget");
            PlotTheme.AxisTitles(PreviewPlot, vm.Localizer["Chart_AxisZCentered"], vm.Localizer["Chart_AxisDiameterUm"]);
            PlotTheme.ShowProfileCentered(PreviewPlot, vm.PreviewLengthMm, vm.PreviewCurve.Select(point => point.DiameterMicrometer));
        }

        PreviewPlot.Refresh();
    }
}
