using System.Windows.Controls;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>手动磨削画面。位置图上点一处交给视图模型当定位目标。</summary>
public partial class ManualGrindingView : UserControl
{
    public ManualGrindingView()
    {
        InitializeComponent();
        Strip.TargetPicked += (_, z) => (DataContext as ManualGrindingViewModel)?.PickPositionTarget(z);
    }
}
