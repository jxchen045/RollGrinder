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

        // 按方案编辑：横轴以辊身中心为 0（设计稿 v4 P1）；按模板编辑仍从 0 起。
        double offset = this.viewModel.ZOffsetMm;
        double[] composedX = this.viewModel.ComposedPoints.Select(point => point.BodyPositionMm - offset).ToArray();
        double[] composedY = this.viewModel.ComposedPoints.Select(point => point.DiameterMm * 1000.0).ToArray();
        PlotTheme.AxisTitles(
            PreviewPlot,
            this.viewModel.Localizer[offset > 0.0 ? "Chart_AxisZCentered" : "Chart_AxisZ"],
            this.viewModel.Localizer["Chart_AxisDiameterUm"]);
        var composed = PreviewPlot.Plot.Add.Scatter(composedX, composedY);
        composed.LineWidth = 3f;
        composed.MarkerSize = 0;
        composed.Color = PlotTheme.Curve(PreviewPlot, "Color.CurveTarget");

        // 段界画虚线：顺接辊形一眼看出每段从哪到哪。
        foreach (double boundary in this.viewModel.BoundaryZs)
        {
            var line = PreviewPlot.Plot.Add.VerticalLine(boundary - offset);
            line.LineWidth = 1f;
            line.LinePattern = LinePattern.Dashed;
            line.Color = PlotTheme.Curve(PreviewPlot, "Color.CurveSegmentBoundary");
        }

        // 按方案编辑：端部非工作区边界画点线（截取 / 延伸只许发生在线外，设计说明 4.5）。
        foreach (double mark in this.viewModel.EndZoneMarks)
        {
            var zone = PreviewPlot.Plot.Add.VerticalLine(mark - offset);
            zone.LineWidth = 1.5f;
            zone.LinePattern = LinePattern.Dotted;
            zone.Color = PlotTheme.Curve(PreviewPlot, "Color.CurveReference");
        }

        // 当前段加粗、换色。
        if (this.viewModel.SelectedSegmentPoints.Count >= 2)
        {
            var selected = PreviewPlot.Plot.Add.Scatter(
                this.viewModel.SelectedSegmentPoints.Select(point => point.BodyPositionMm - offset).ToArray(),
                this.viewModel.SelectedSegmentPoints.Select(point => point.DiameterMm * 1000.0).ToArray());
            selected.LineWidth = 6f;
            selected.MarkerSize = 0;
            selected.Color = PlotTheme.Curve(PreviewPlot, "Color.CurveMeasured").WithAlpha(0.55);
        }

        // 点表段：原始点和插值曲线画在一起，看得出插值有没有在点之间鼓出来。
        if (this.viewModel.TablePoints.Count > 0)
        {
            var table = PreviewPlot.Plot.Add.Scatter(
                this.viewModel.TablePoints.Select(point => point.BodyPositionMm - offset).ToArray(),
                this.viewModel.TablePoints.Select(point => point.DiameterMm * 1000.0).ToArray());
            table.LineWidth = 0f;
            table.MarkerSize = 8f;
            table.Color = PlotTheme.Curve(PreviewPlot, "Color.CurveMeasured");
        }

        // 导进来的对照线：虚线、另一个颜色，一眼看出哪条是设计、哪条是拿来比的。
        double[] referenceX = this.viewModel.ReferencePoints.Select(point => point.BodyPositionMm - offset).ToArray();
        double[] referenceY = this.viewModel.ReferencePoints.Select(point => point.DiameterMm * 1000.0).ToArray();
        if (referenceX.Length >= 2)
        {
            var reference = PreviewPlot.Plot.Add.Scatter(referenceX, referenceY);
            reference.LineWidth = 2f;
            reference.MarkerSize = 0;
            reference.LinePattern = LinePattern.Dotted;
            reference.Color = PlotTheme.Curve(PreviewPlot, "Color.CurveReference");
        }

        // 辊形图规范：直径量 µm，Y 由合成辊形定，绘图区 4 : 1（和库、作业、记录里同一条辊形一个样）。
        if (offset > 0.0)
        {
            PlotTheme.ShowProfileCentered(PreviewPlot, offset * 2.0, composedY);
        }
        else
        {
            PlotTheme.ShowProfile(PreviewPlot, composedX[^1], composedY);
        }
        PreviewPlot.Refresh();
    }
}
