using System;
using System.Linq;
using System.Windows.Controls;
using RollGrinder.App.ViewModels;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Units;

namespace RollGrinder.App.Views;

/// <summary>
/// 测量与补偿页。图表显示偏差与补偿曲线，纵轴按界面惯例用直径量微米。
/// </summary>
public partial class MeasurementView : UserControl
{
    private MeasurementViewModel? viewModel;

    public MeasurementView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (this.viewModel is not null)
        {
            this.viewModel.ProfilesChanged -= OnProfilesChanged;
        }

        this.viewModel = DataContext as MeasurementViewModel;
        if (this.viewModel is not null)
        {
            this.viewModel.ProfilesChanged += OnProfilesChanged;
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (this.viewModel is not null)
        {
            this.viewModel.ProfilesChanged -= OnProfilesChanged;
        }
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        if (this.viewModel is null)
        {
            return;
        }

        ProfilePlot.Plot.Clear();
        Draw(this.viewModel.Deviation);
        Draw(this.viewModel.Compensation);
        ProfilePlot.Plot.Axes.AutoScale();
        ProfilePlot.Refresh();
    }

    private void Draw(RollProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        double[] positions = profile.Points.Select(point => point.BodyPositionMm).ToArray();
        double[] micrometres = profile.Points
            .Select(point => UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm))
            .ToArray();

        ProfilePlot.Plot.Add.Scatter(positions, micrometres);
    }
}
