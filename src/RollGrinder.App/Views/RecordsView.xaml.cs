using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>记录页。导出需要文件对话框，这一步留在视图里。</summary>
public partial class RecordsView : UserControl
{
    public RecordsView()
    {
        InitializeComponent();
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
