using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using RollGrinder.App.Interaction;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>
/// 诊断页。导出诊断快照与备份都要挑一个文件路径，对话框留在视图里。
/// </summary>
public partial class DiagnosticsView : UserControl
{
    private DiagnosticsViewModel? viewModel;

    public DiagnosticsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        this.viewModel = DataContext as DiagnosticsViewModel;
        if (this.viewModel is not null)
        {
            this.viewModel.SnapshotExportRequested += OnSnapshotExportRequested;
            this.viewModel.BackupRequested += OnBackupRequested;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (this.viewModel is not null)
        {
            this.viewModel.SnapshotExportRequested -= OnSnapshotExportRequested;
            this.viewModel.BackupRequested -= OnBackupRequested;
        }
    }

    private async void OnSnapshotExportRequested(object? sender, EventArgs e)
    {
        if (this.viewModel is null || !TryPickPath("diagnostics", ".txt", "Text|*.txt", out string path))
        {
            return;
        }

        await this.viewModel.ExportSnapshotAsync(path, CancellationToken.None);
    }

    private async void OnBackupRequested(object? sender, EventArgs e)
    {
        if (this.viewModel is null || !TryPickPath("rollgrinder-backup", ".zip", "Zip|*.zip", out string path))
        {
            return;
        }

        await this.viewModel.BackupAsync(path, CancellationToken.None);
    }

    private static bool TryPickPath(string prefix, string extension, string filter, out string path)
    {
        string? chosen = InteractionScope.FileDialogs.PickSavePath(
            string.Create(CultureInfo.InvariantCulture, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmm}{extension}"),
            extension,
            filter);
        path = chosen ?? string.Empty;
        return chosen is not null;
    }
}
