using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>手动动作页。测量对中那一页画沿 Z 的直径小曲线。</summary>
public partial class ManualView : UserControl
{
    private ManualViewModel? viewModel;

    public ManualView()
    {
        InitializeComponent();
        PlotTheme.Apply(PointsPlot);
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        this.viewModel = DataContext as ManualViewModel;
        if (this.viewModel is not null)
        {
            this.viewModel.PointsChanged += OnPointsChanged;
            Redraw();
        }
    }

    private void Detach()
    {
        if (this.viewModel is not null)
        {
            this.viewModel.PointsChanged -= OnPointsChanged;
        }
    }

    private void OnPointsChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        PointsPlot.Plot.Clear();
        if (this.viewModel is { Points.Count: > 0 } vm)
        {
            var line = PointsPlot.Plot.Add.Scatter(
                vm.Points.Select(row => row.Point.BodyPositionMm).ToArray(),
                vm.Points.Select(row => RollGrinder.Core.Units.UnitConversion.RadiusMmToDiameterMm(row.Point.MeasuredRadiusMm)).ToArray());
            line.LineWidth = 2.5f;
            line.MarkerSize = 8;
            line.Color = PlotTheme.Curve(PointsPlot, "Color.CurveMeasured");
            PlotTheme.AxisTitles(PointsPlot, vm.Localizer["Chart_AxisZ"], vm.Localizer["Manual_PointsAxis"]);
            PointsPlot.Plot.Axes.AutoScale();
        }

        PointsPlot.Refresh();
    }
}
