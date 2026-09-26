using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RollGrinder.App.Views;

/// <summary>另存为的命名框。一打开就把光标放进输入框、整段选中，直接打字就是新名字。</summary>
public partial class NamePromptOverlay : UserControl
{
    public NamePromptOverlay()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            });
        }
    }
}
