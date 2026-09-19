using System;
using System.Windows;
using System.Windows.Threading;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>
/// 主窗口。刷新节拍由这里的定时器按 hmi.json 的 uiRefreshHz 驱动，
/// 与机床数据到达频率解耦；每一拍只驱动当前页。
/// </summary>
public partial class ShellWindow : Window
{
    private readonly ShellViewModel viewModel;
    private readonly DispatcherTimer timer;

    public ShellWindow(ShellViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;

        this.timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = viewModel.RefreshInterval,
        };
        this.timer.Tick += OnTick;

        Loaded += (_, _) => this.timer.Start();
        Closed += (_, _) =>
        {
            this.timer.Stop();
            this.timer.Tick -= OnTick;
        };
    }

    private void OnTick(object? sender, EventArgs e) => this.viewModel.Tick(DateTimeOffset.UtcNow);
}
