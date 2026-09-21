using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RollGrinder.App.Printing;
using Microsoft.Win32;
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
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (this.viewModel is not null)
        {
            this.viewModel.ReportChanged -= OnReportChanged;
        }
    }

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

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 按所选打印机的可打印区域重新分页：换一台纸张不同的打印机也不会切掉边。
        IDocumentPaginatorSource paginator = document;
        document.PageHeight = dialog.PrintableAreaHeight;
        document.PageWidth = dialog.PrintableAreaWidth;

        dialog.PrintDocument(paginator.DocumentPaginator, document.Name);
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RecordsViewModel viewModel)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = string.Create(CultureInfo.InvariantCulture, $"records-{DateTime.Now:yyyyMMdd-HHmm}.csv"),
            DefaultExt = ".csv",
            Filter = "CSV|*.csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await viewModel.ExportAsync(dialog.FileName, CancellationToken.None);
    }
}
