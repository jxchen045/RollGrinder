using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>
/// 工艺程序页。选工序不再用下拉：右侧竖向软键"插入工序 ▸"按类别开子菜单（修改稿原则 1）。
/// 光标进了哪个参数格，就告诉视图模型——简图上亮对应的量，说明行跟着换（原则 2）。
/// </summary>
public partial class StepsView : UserControl
{
    public StepsView()
    {
        InitializeComponent();
        GotKeyboardFocus += OnGotKeyboardFocus;
    }

    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is not StepsViewModel viewModel)
        {
            return;
        }

        // 往上找到参数格：焦点可能在输入框里，也可能在某个选项键上。
        for (DependencyObject? node = e.NewFocus as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: ParameterRowViewModel row })
            {
                viewModel.FocusedParameter = row;
                return;
            }

            if (node is StepsView)
            {
                return;
            }
        }
    }
}
