using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;
using ScottPlot;

namespace RollGrinder.App.Views;

/// <summary>对比：两条辊形叠图，以辊身中心为 0（与选方案子视图同一画法）。</summary>
public partial class CompareView : UserControl
{
    private CompareViewModel? viewModel;

    public CompareView()
    {
        InitializeComponent();
        PlotTheme.Apply(Plot);
        DataContextChanged += (_, _) => Attach(DataContext as CompareViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(CompareViewModel? next)
    {
        if (this.viewModel is not null)
        {
            this.viewModel.CurvesChanged -= OnCurvesChanged;
        }

        this.viewModel = next;
        if (next is not null)
        {
            next.CurvesChanged += OnCurvesChanged;
            Redraw();
        }
    }

    private void OnCurvesChanged(object? sender, EventArgs e) => Dispatcher.Invoke(Redraw);

    private void Redraw()
    {
        Plot.Plot.Clear();
        if (this.viewModel is { HasCurve: true } vm)
        {
            Plot.Plot.Add.VerticalLine(0.0, 1f, PlotTheme.Curve(Plot, "Color.CurveTolerance"), LinePattern.Dashed);
            Add(vm.CurveB, "Color.CurveReference", 2f, LinePattern.Dashed);
            Add(vm.CurveA, "Color.CurveTarget", 3f, LinePattern.Solid);
            Plot.Plot.Axes.AutoScale();
        }

        Plot.Refresh();
    }

    private void Add(IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> curve, string colorKey, float width, LinePattern pattern)
    {
        if (curve.Count < 2)
        {
            return;
        }

        double half = curve.Max(point => point.BodyPositionMm) / 2.0;
        var line = Plot.Plot.Add.Scatter(
            curve.Select(point => point.BodyPositionMm - half).ToArray(), curve.Select(point => point.DiameterMicrometer).ToArray());
        line.LineWidth = width;
        line.MarkerSize = 0;
        line.LinePattern = pattern;
        line.Color = PlotTheme.Curve(Plot, colorKey);
    }
}
