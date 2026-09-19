using System;
using System.Linq;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;
using ScottPlot;

namespace RollGrinder.App.Views;

/// <summary>辊形编辑页。预览画两条线：合成辊形与仅主辊形的对照。</summary>
public partial class ProfileView : UserControl
{
    private ProfileViewModel? viewModel;

    public ProfileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        Detach();
        this.viewModel = DataContext as ProfileViewModel;
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

        if (this.viewModel is null || this.viewModel.ComposedPoints.Count < 2)
        {
            PreviewPlot.Refresh();
            return;
        }

        double[] mainX = this.viewModel.MainPoints.Select(point => point.BodyPositionMm).ToArray();
        double[] mainY = this.viewModel.MainPoints.Select(point => point.DiameterMm).ToArray();
        if (mainX.Length >= 2)
        {
            var main = PreviewPlot.Plot.Add.Scatter(mainX, mainY);
            main.LineWidth = 2f;
            main.MarkerSize = 0;
            main.LinePattern = LinePattern.Dashed;
            main.Color = Color.FromHex("#8D97A3");
        }

        double[] composedX = this.viewModel.ComposedPoints.Select(point => point.BodyPositionMm).ToArray();
        double[] composedY = this.viewModel.ComposedPoints.Select(point => point.DiameterMm).ToArray();
        var composed = PreviewPlot.Plot.Add.Scatter(composedX, composedY);
        composed.LineWidth = 3f;
        composed.MarkerSize = 0;
        composed.Color = Color.FromHex("#15507F");

        PreviewPlot.Plot.Axes.AutoScale();
        PreviewPlot.Refresh();
    }
}
