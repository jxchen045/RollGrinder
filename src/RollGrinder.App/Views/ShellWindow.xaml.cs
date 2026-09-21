using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Views;

/// <summary>
/// 主窗口。刷新节拍由这里的定时器按 hmi.json 的 uiRefreshHz 驱动，
/// 与机床数据到达频率解耦；每一拍只驱动当前页。
///
/// 键盘映射对应操作面板的软键：F1–F8 = 底部八格（第 8 格是导航槽），
/// Esc = 退一级 / 收浮层，Ctrl+1…6 = 直接切区域。
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

        Loaded += async (_, _) =>
        {
            this.timer.Start();

            // 把用户名列表拉进来，登录框的下拉才有东西可选。
            await viewModel.InitializeAsync(System.Threading.CancellationToken.None);
        };
        Closed += (_, _) =>
        {
            this.timer.Stop();
            this.timer.Tick -= OnTick;
        };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnTick(object? sender, EventArgs e) => this.viewModel.Tick(DateTimeOffset.UtcNow);

    /// <summary>
    /// WPF 的 PasswordBox 不让绑 Password（绑了就会把明文留在依赖属性系统里），
    /// 所以口令由这几个事件推进视图模型，用完即清。
    /// </summary>
    private void OnSignInPasswordChanged(object sender, RoutedEventArgs e) =>
        this.viewModel.SignInPassword = ((System.Windows.Controls.PasswordBox)sender).Password;

    private void OnNewPasswordChanged(object sender, RoutedEventArgs e) =>
        this.viewModel.NewPassword = ((System.Windows.Controls.PasswordBox)sender).Password;

    private void OnConfirmPasswordChanged(object sender, RoutedEventArgs e) =>
        this.viewModel.ConfirmPassword = ((System.Windows.Controls.PasswordBox)sender).Password;

    private void OnNewUserPasswordChanged(object sender, RoutedEventArgs e) =>
        this.viewModel.NewUserPassword = ((System.Windows.Controls.PasswordBox)sender).Password;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 软键优先于输入框：操作面板上按 F 键就该走功能条，哪怕焦点在某个数值框里。
        if (e.Key is >= Key.F1 and <= Key.F8)
        {
            this.viewModel.PressFunctionKey(e.Key - Key.F1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            this.viewModel.PressEscape();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && e.Key is >= Key.D1 and <= Key.D6)
        {
            int index = e.Key - Key.D1;
            if (index < this.viewModel.AreaMenuItems.Count)
            {
                AreaMenuItemViewModel item = this.viewModel.AreaMenuItems[index];
                if (item.Command.CanExecute(null))
                {
                    item.Command.Execute(null);
                }
            }

            e.Handled = true;
        }
    }
}
