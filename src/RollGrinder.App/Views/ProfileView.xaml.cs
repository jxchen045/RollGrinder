using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using RollGrinder.App.Interaction;
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
        PlotTheme.Apply(PreviewPlot);
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
            this.viewModel.ImportPointsRequested += OnImportPointsRequested;
            this.viewModel.ImportReferenceRequested += OnImportReferenceRequested;
            this.viewModel.GeneratePointsRequested += OnGeneratePointsRequested;
            Redraw();
        }
    }

    private void Detach()
    {
        if (this.viewModel is not null)
        {
            this.viewModel.PreviewChanged -= OnPreviewChanged;
            this.viewModel.ImportPointsRequested -= OnImportPointsRequested;
            this.viewModel.ImportReferenceRequested -= OnImportReferenceRequested;
            this.viewModel.GeneratePointsRequested -= OnGeneratePointsRequested;
        }
    }

    private void OnPreviewChanged(object? sender, EventArgs e) => Redraw();

    private async void OnImportPointsRequested(object? sender, EventArgs e)
    {
        if (this.viewModel is null)
        {
            return;
        }

        string? path = InteractionScope.FileDialogs.PickOpenPath(".csv", "CSV|*.csv|All files|*.*");
        if (path is null)
        {
            return;
        }

        await this.viewModel.ImportPointsAsync(path, CancellationToken.None);
    }

    private async void OnImportReferenceRequested(object? sender, EventArgs e)
    {
        if (this.viewModel is null)
        {
            return;
        }

        string? path = InteractionScope.FileDialogs.PickOpenPath(".csv", "CSV|*.csv|All files|*.*");
        if (path is null)
        {
            return;
        }

        await this.viewModel.ImportReferenceAsync(path, CancellationToken.None);
    }

    private async void OnGeneratePointsRequested(object? sender, EventArgs e)
    {
        if (this.viewModel is null)
        {
            return;
        }

        string? path = InteractionScope.FileDialogs.PickSavePath(
            string.Create(CultureInfo.InvariantCulture, $"profile-{DateTime.Now:yyyyMMdd-HHmm}.csv"),
            ".csv",
            "CSV|*.csv");
        if (path is null)
        {
            return;
        }

        await this.viewModel.GeneratePointsAsync(path, CancellationToken.None);
    }

    private void Redraw()
    {
        PreviewPlot.Plot.Clear();

        if (this.viewModel is null || this.viewModel.ComposedPoints.Count < 2)
        {
            PreviewPlot.Refresh();
            return;
        }

        double[] composedX = this.viewModel.ComposedPoints.Select(point => point.BodyPositionMm).ToArray();
        double[] composedY = this.viewModel.ComposedPoints.Select(point => point.DiameterMm).ToArray();
        var composed = PreviewPlot.Plot.Add.Scatter(composedX, composedY);
        composed.LineWidth = 3f;
        composed.MarkerSize = 0;
        composed.Color = Color.FromHex("#15507F");

        // 段界画虚线：顺接辊形一眼看出每段从哪到哪。
        foreach (double boundary in this.viewModel.BoundaryZs)
        {
            var line = PreviewPlot.Plot.Add.VerticalLine(boundary);
            line.LineWidth = 1f;
            line.LinePattern = LinePattern.Dashed;
            line.Color = Color.FromHex("#8D97A3");
        }

        // 当前段加粗、换色。
        if (this.viewModel.SelectedSegmentPoints.Count >= 2)
        {
            var selected = PreviewPlot.Plot.Add.Scatter(
                this.viewModel.SelectedSegmentPoints.Select(point => point.BodyPositionMm).ToArray(),
                this.viewModel.SelectedSegmentPoints.Select(point => point.DiameterMm).ToArray());
            selected.LineWidth = 6f;
            selected.MarkerSize = 0;
            selected.Color = Color.FromHex("#B3241C").WithAlpha(0.55);
        }

        // 点表段：原始点和插值曲线画在一起，看得出插值有没有在点之间鼓出来。
        if (this.viewModel.TablePoints.Count > 0)
        {
            var table = PreviewPlot.Plot.Add.Scatter(
                this.viewModel.TablePoints.Select(point => point.BodyPositionMm).ToArray(),
                this.viewModel.TablePoints.Select(point => point.DiameterMm).ToArray());
            table.LineWidth = 0f;
            table.MarkerSize = 8f;
            table.Color = Color.FromHex("#B3241C");
        }

        // 导进来的对照线：虚线、另一个颜色，一眼看出哪条是设计、哪条是拿来比的。
        double[] referenceX = this.viewModel.ReferencePoints.Select(point => point.BodyPositionMm).ToArray();
        double[] referenceY = this.viewModel.ReferencePoints.Select(point => point.DiameterMm).ToArray();
        if (referenceX.Length >= 2)
        {
            var reference = PreviewPlot.Plot.Add.Scatter(referenceX, referenceY);
            reference.LineWidth = 2f;
            reference.MarkerSize = 0;
            reference.LinePattern = LinePattern.Dotted;
            reference.Color = Color.FromHex("#8A5A00");
        }

        PreviewPlot.Plot.Axes.AutoScale();
        PreviewPlot.Refresh();
    }
}
