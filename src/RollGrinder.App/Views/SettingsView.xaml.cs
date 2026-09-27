using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>
/// 设置页：现场标定值 + 改动记录；子视图"砂轮"与换砂轮向导。
/// 砂轮页里光标进了哪个参数格，就告诉视图模型——简图上亮对应的量，说明行跟着换。
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        GotKeyboardFocus += OnGotKeyboardFocus;
    }

    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        for (DependencyObject? node = e.NewFocus as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: ParameterRowViewModel row })
            {
                viewModel.FocusedWheelKey = row.Key;
                return;
            }

            if (node is SettingsView)
            {
                return;
            }
        }
    }
}
