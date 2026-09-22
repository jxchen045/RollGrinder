using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>
/// 工序编程页。
///
/// 选工序的下拉按实机的工序槽分组。分组是**呈现**，所以留在视图里：
/// 视图模型给的仍然是一列按槽排好序的工序，槽名只是其中一个属性。
/// </summary>
public partial class StepsView : UserControl
{
    public StepsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is not StepsViewModel viewModel)
        {
            StepTypePicker.ItemsSource = null;
            return;
        }

        // 视图模型已经按槽排过序，这里只按槽名切组——CollectionView 是按出现顺序分组的，
        // 所以组的先后就是实机屏幕上那 5 个槽的先后。
        var grouped = new CollectionViewSource { Source = viewModel.StepTypeOptions };
        grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StepTypeOptionViewModel.SlotName)));

        StepTypePicker.ItemsSource = grouped.View;
    }
}
