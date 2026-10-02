using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;
using ScottPlot;

namespace RollGrinder.App.Views;

/// <summary>选辊形 / 选程序子视图：选中的辊形实线，现计划虚线，按辊形图规范（4 : 1、两轴名）。</summary>
public partial class PlanPickerView : UserControl
{
    private PlanPickerViewModel? viewModel;

    public PlanPickerView()
    {
        InitializeComponent();
        PlotTheme.Apply(Plot);
        DataContextChanged += (_, _) => Attach(DataContext as PlanPickerViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(PlanPickerViewModel? next)
    {
        if (this.viewModel is not null)
        {
            this.viewModel.PreviewChanged -= OnPreviewChanged;
        }

        this.viewModel = next;
        if (next is not null)
        {
            next.PreviewChanged += OnPreviewChanged;
            Redraw();
        }
    }

    private void OnPreviewChanged(object? sender, EventArgs e) => Dispatcher.Invoke(Redraw);

    private void Redraw()
    {
        Plot.Plot.Clear();
        if (this.viewModel is { } vm && vm.IsProfile)
        {
            if (vm.ReferenceCurve.Count > 1)
            {
                var reference = Plot.Plot.Add.Scatter(
                    vm.ReferenceCurve.Select(p => p.BodyPositionMm).ToArray(), vm.ReferenceCurve.Select(p => p.DiameterMicrometer).ToArray());
                reference.LineWidth = 2f;
                reference.MarkerSize = 0;
                reference.LinePattern = LinePattern.Dashed;
                reference.Color = PlotTheme.Curve(Plot, "Color.CurveReference");
            }

            if (vm.SelectedCurve.Count > 1)
            {
                var selected = Plot.Plot.Add.Scatter(
                    vm.SelectedCurve.Select(p => p.BodyPositionMm).ToArray(), vm.SelectedCurve.Select(p => p.DiameterMicrometer).ToArray());
                selected.LineWidth = 3f;
                selected.MarkerSize = 0;
                selected.Color = PlotTheme.Curve(Plot, "Color.CurveTarget");
                PlotTheme.AxisTitles(Plot, vm.Localizer["Chart_AxisZ"], vm.Localizer["Chart_AxisDiameterUm"]);
                PlotTheme.ShowProfile(Plot, vm.BodyLengthMm, vm.SelectedCurve.Select(p => p.DiameterMicrometer).Concat(vm.ReferenceCurve.Select(p => p.DiameterMicrometer)));
            }
        }

        Plot.Refresh();
    }
}
