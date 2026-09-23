using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Linq;
using RollGrinder.App.Printing;
using RollGrinder.Services.Records;
using ScottPlot;
using RollGrinder.App.Interaction;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>
/// 记录页。导出需要文件对话框、报表要排版与打印，这几步留在视图里。
/// </summary>
public partial class RecordsView : UserControl
{
    private RecordsViewModel? viewModel;

    public RecordsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        this.viewModel = DataContext as RecordsViewModel;
        if (this.viewModel is not null)
        {
            this.viewModel.ReportChanged += OnReportChanged;
            this.viewModel.CurveChanged += OnCurveChanged;
            this.viewModel.ExportRequested += OnExportRequested;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (this.viewModel is not null)
        {
            this.viewModel.ReportChanged -= OnReportChanged;
            this.viewModel.CurveChanged -= OnCurveChanged;
            this.viewModel.ExportRequested -= OnExportRequested;
        }
    }

    /// <summary>
    /// 记录曲线。一张图上可能有两条线（磨前/磨后、圆度/偏心），
    /// 所以每条线自己一个颜色，图例写明哪条是哪条。
    /// </summary>
    private void OnCurveChanged(object? sender, EventArgs e)
    {
        RecordPlot.Plot.Clear();

        RecordCurve? curve = this.viewModel?.Curve;
        if (this.viewModel is null || curve is null || !curve.HasData)
        {
            RecordPlot.Refresh();
            return;
        }

        RecordPlot.Plot.Add.HorizontalLine(0.0, 1f, Colors.Gray, LinePattern.Dotted);

        for (int i = 0; i < curve.Series.Count; i++)
        {
            RecordCurveSeries series = curve.Series[i];
            if (series.Points.Count < 2)
            {
                continue;
            }

            var line = RecordPlot.Plot.Add.Scatter(
                series.Points.Select(point => point.X).ToArray(),
                series.Points.Select(point => point.Y).ToArray());
            line.LineWidth = 2.5f;
            line.MarkerSize = 0;
            line.Color = SeriesColors[i % SeriesColors.Length];
            line.LegendText = this.viewModel.Localizer[series.LabelResourceKey];
        }

        RecordPlot.Plot.Axes.Bottom.Label.Text = this.viewModel.Localizer[curve.AxisUnitResourceKey];
        RecordPlot.Plot.Axes.Left.Label.Text = this.viewModel.Localizer[curve.ValueUnitResourceKey];
        RecordPlot.Plot.ShowLegend();
        RecordPlot.Plot.Axes.AutoScale();
        RecordPlot.Refresh();
    }

    /// <summary>一张图上最多两条线，两个颜色够用且分得开。</summary>
    private static readonly Color[] SeriesColors =
    {
        Color.FromHex("#B3241C"),
        Color.FromHex("#15507F"),
    };

    private void OnReportChanged(object? sender, EventArgs e)
    {
        ReportPreview.Document = this.viewModel?.Report is { } report
            ? ReportDocumentBuilder.Build(report, this.viewModel.Localizer)
            : null;
    }

    /// <summary>
    /// 打的就是预览里那一份文档——重新排一次版就可能与看到的不一样。
    /// </summary>
    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        if (ReportPreview.Document is not FlowDocument document)
        {
            return;
        }

        // 打印队列里显示报表标题（磨前工艺单 / 磨削报告），而不是一个空名字。
        string title = this.viewModel?.Report is { } report
            ? this.viewModel.Localizer[report.TitleResourceKey]
            : document.Name;
        InteractionScope.DocumentOutput.Print(document, title, askOperator: true);
    }

    /// <summary>功能键上的"导出"与页面上的按钮走同一条路。</summary>
    private void OnExportRequested(object? sender, EventArgs e) => OnExportClick(this, new RoutedEventArgs());

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RecordsViewModel viewModel)
        {
            return;
        }

        string? path = InteractionScope.FileDialogs.PickSavePath(
            string.Create(CultureInfo.InvariantCulture, $"records-{DateTime.Now:yyyyMMdd-HHmm}.csv"),
            ".csv",
            "CSV|*.csv");
        if (path is null)
        {
            return;
        }

        await viewModel.ExportAsync(path, CancellationToken.None);
    }
}
