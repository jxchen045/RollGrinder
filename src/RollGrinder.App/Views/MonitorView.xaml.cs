using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>
/// 监控页。图表控件不适合纯绑定，这里只做"把视图模型的数据画出来"这一件事。
/// </summary>
public partial class MonitorView : UserControl
{
    private MonitorViewModel? viewModel;

    public MonitorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (this.viewModel is not null)
        {
            this.viewModel.HistoryChanged -= OnHistoryChanged;
        }

        this.viewModel = DataContext as MonitorViewModel;
        if (this.viewModel is not null)
        {
            this.viewModel.HistoryChanged += OnHistoryChanged;
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (this.viewModel is not null)
        {
            this.viewModel.HistoryChanged -= OnHistoryChanged;
        }
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (this.viewModel is null)
        {
            return;
        }

        IReadOnlyList<(double Seconds, double DiameterMm)> history = this.viewModel.DiameterHistory;
        if (history.Count < 2)
        {
            return;
        }

        double[] seconds = history.Select(sample => sample.Seconds).ToArray();
        double[] diameters = history.Select(sample => sample.DiameterMm).ToArray();

        TrendPlot.Plot.Clear();
        TrendPlot.Plot.Add.Scatter(seconds, diameters);
        TrendPlot.Plot.Axes.AutoScale();
        TrendPlot.Refresh();
    }
}
