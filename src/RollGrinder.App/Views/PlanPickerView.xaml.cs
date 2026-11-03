using System;
using System.Collections.Generic;
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

    /// <summary>
    /// 选中的与现在用的叠图。横轴以辊身中心为 0（设计稿 v4：辊形图通则）——两条曲线名义长度不同时也按中心对齐着看，
    /// 正好是截取 / 延伸的样子。
    /// </summary>
    private void Redraw()
    {
        Plot.Plot.Clear();
        if (this.viewModel is { } vm && vm.ShowsCurve)
        {
            static double Half(IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> curve) =>
                curve.Count == 0 ? 0.0 : curve.Max(p => p.BodyPositionMm) / 2.0;

            double length = Math.Max(
                vm.SelectedCurve.Count > 1 ? Half(vm.SelectedCurve) * 2.0 : 0.0,
                vm.ReferenceCurve.Count > 1 ? Half(vm.ReferenceCurve) * 2.0 : 0.0);
            Plot.Plot.Add.VerticalLine(0.0, 1f, PlotTheme.Curve(Plot, "Color.CurveTolerance"), LinePattern.Dashed);
            if (vm.ReferenceCurve.Count > 1)
            {
                double half = Half(vm.ReferenceCurve);
                var reference = Plot.Plot.Add.Scatter(
                    vm.ReferenceCurve.Select(p => p.BodyPositionMm - half).ToArray(), vm.ReferenceCurve.Select(p => p.DiameterMicrometer).ToArray());
                reference.LineWidth = 2f;
                reference.MarkerSize = 0;
                reference.LinePattern = LinePattern.Dashed;
                reference.Color = PlotTheme.Curve(Plot, "Color.CurveReference");
            }

            if (vm.SelectedCurve.Count > 1)
            {
                double half = Half(vm.SelectedCurve);
                var selected = Plot.Plot.Add.Scatter(
                    vm.SelectedCurve.Select(p => p.BodyPositionMm - half).ToArray(), vm.SelectedCurve.Select(p => p.DiameterMicrometer).ToArray());
                selected.LineWidth = 3f;
                selected.MarkerSize = 0;
                selected.Color = PlotTheme.Curve(Plot, "Color.CurveTarget");
            }

            if (length > 0.0)
            {
                PlotTheme.AxisTitles(Plot, vm.Localizer["Chart_AxisZCentered"], vm.Localizer["Chart_AxisDiameterUm"]);
                PlotTheme.ShowProfileCentered(
                    Plot, length, vm.SelectedCurve.Select(p => p.DiameterMicrometer).Concat(vm.ReferenceCurve.Select(p => p.DiameterMicrometer)));
            }
        }

        Plot.Refresh();
    }
}
