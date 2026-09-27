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
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is StepsViewModel old)
            {
                old.ProgramOptionsFocusRequested -= OnProgramOptionsFocusRequested;
            }

            if (e.NewValue is StepsViewModel current)
            {
                current.ProgramOptionsFocusRequested += OnProgramOptionsFocusRequested;
            }
        };
    }

    /// <summary>"程序步骤"竖键：把键盘焦点移到第一个程序步骤开关上，之后 Tab、回车就能逐个切。</summary>
    private void OnProgramOptionsFocusRequested(object? sender, System.EventArgs e)
    {
        ProgramOptionsList.BringIntoView();
        if (FirstFocusable(ProgramOptionsList) is { } target)
        {
            target.Focus();
        }
    }

    private static UIElement? FirstFocusable(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Focusable: true, IsEnabled: true, IsVisible: true } element and not ItemsControl)
            {
                return element;
            }

            if (FirstFocusable(child) is { } found)
            {
                return found;
            }
        }

        return null;
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
