using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RollGrinder.App.Controls;
using RollGrinder.App.ViewModels;
using Serilog;

namespace RollGrinder.App.Views;

/// <summary>
/// 主窗口。刷新节拍由这里的定时器按 hmi.json 的 uiRefreshHz 驱动，
/// 与机床数据到达频率解耦；每一拍只驱动当前页。
///
/// 键盘（最终稿 4.6）：F1–F8 横键，Shift+F1–F8 竖键，F10 区域菜单，Esc 返回 / 取消，
/// 回车 = 确认（有待确认的事时），Ctrl+1…7 左栏，Ctrl+P 截屏，Ctrl+L 语言，
/// Ctrl+C / X / V 在段表和工序序列里复制、剪切、粘贴（输入框里照常是文字的复制粘贴）；
/// 不在输入框里时按 i 开关帮助。
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

        // 现场全屏、盖住任务栏（hmi.json fullScreen，默认开）；调试时关掉就是普通最大化窗口。
        if (LayoutProfile.FullScreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
        }

        this.timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = viewModel.RefreshInterval,
        };
        this.timer.Tick += OnTick;
        viewModel.ViewRequested += OnViewRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += async (_, _) =>
        {
            this.timer.Start();

            // 把用户名列表拉进来，登录框才有人可选。
            await viewModel.InitializeAsync(System.Threading.CancellationToken.None);
        };
        Closed += (_, _) =>
        {
            this.timer.Stop();
            this.timer.Tick -= OnTick;
            viewModel.ViewRequested -= OnViewRequested;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnTick(object? sender, EventArgs e) => this.viewModel.Tick(DateTimeOffset.UtcNow);

    /// <summary>签退后清掉口令框：PasswordBox 不绑定，只能在这里清。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsSignInOpen) && this.viewModel.IsSignInOpen)
        {
            SignInPasswordBox.Clear();
        }
    }

    /// <summary>
    /// WPF 的 PasswordBox 不让绑 Password（绑了就会把明文留在依赖属性系统里），
    /// 所以口令由这几个事件推进视图模型，用完即清。
    /// </summary>
    private void OnSignInPasswordChanged(object sender, RoutedEventArgs e) =>
        this.viewModel.SignInPassword = ((PasswordBox)sender).Password;

    private void OnNewPasswordChanged(object sender, RoutedEventArgs e) =>
        this.viewModel.NewPassword = ((PasswordBox)sender).Password;

    private void OnConfirmPasswordChanged(object sender, RoutedEventArgs e) =>
        this.viewModel.ConfirmPassword = ((PasswordBox)sender).Password;

    private void OnNewUserPasswordChanged(object sender, RoutedEventArgs e) =>
        this.viewModel.NewUserPassword = ((PasswordBox)sender).Password;

    /// <summary>登录框的 PIN 数字键：往口令框里加一位 / 退一位 / 清空。</summary>
    private void OnPinKey(object sender, RoutedEventArgs e)
    {
        string key = (string)((Button)sender).Tag;
        SignInPasswordBox.Password = key switch
        {
            "C" => string.Empty,
            "B" => SignInPasswordBox.Password.Length > 0 ? SignInPasswordBox.Password[..^1] : string.Empty,
            _ => SignInPasswordBox.Password + key,
        };
    }

    private void OnKeypadEnter(object sender, RoutedEventArgs e) => this.viewModel.Keypad.Enter();

    private void OnCloseUserMenu(object sender, RoutedEventArgs e) => this.viewModel.IsUserMenuOpen = false;

    /// <summary>功能键块里要视图层做的事：系统触摸键盘、计算器、截屏。</summary>
    private void OnViewRequested(object? sender, ShellViewRequest request)
    {
        switch (request)
        {
            case ShellViewRequest.Screenshot:
                SaveScreenshot();
                break;

            case ShellViewRequest.Calculator:
                Launch("calc.exe", "Fb_CalculatorFailed");
                break;

            case ShellViewRequest.TouchKeyboard:
                // 数值格有数字键盘；名称、备注这类文字用 Windows 的触摸键盘。
                Launch(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe"),
                    "Fb_KeyboardFailed");
                break;

            default:
                break;
        }
    }

    private void SaveScreenshot()
    {
        string fileName = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png";
        string path = Path.Combine(this.viewModel.ScreenshotDirectory, fileName);
        try
        {
            bool saved = WindowCapture.Save(this, path);
            this.viewModel.Report(saved ? "Fb_ScreenshotSaved" : "Fb_ScreenshotFailed", !saved, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning(ex, "Screenshot to {Path} failed", path);
            this.viewModel.Report("Fb_ScreenshotFailed", true, path);
        }
    }

    private void Launch(string program, string failureKey)
    {
        try
        {
            Process.Start(new ProcessStartInfo(program) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warning(ex, "Could not start {Program}", program);
            this.viewModel.Report(failureKey, true);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // 软键优先于输入框：按 F 键就该走软键，哪怕焦点在某个数值框里。
        if (key is >= Key.F1 and <= Key.F8)
        {
            if (shift)
            {
                this.viewModel.PressVerticalKey(key - Key.F1);
            }
            else
            {
                this.viewModel.PressHorizontalKey(key - Key.F1);
            }

            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.F10:
                this.viewModel.ToggleAreaMenuCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.Escape:
                this.viewModel.PressEscape();
                e.Handled = true;
                return;

            case Key.Enter when this.viewModel.Keypad.IsOpen:
                this.viewModel.Keypad.Enter();
                e.Handled = true;
                return;

            case Key.Enter when this.viewModel.PressEnter():
                e.Handled = true;
                return;

            default:
                break;
        }

        if (control)
        {
            if (key is >= Key.D1 and <= Key.D7)
            {
                this.viewModel.PressQuickBar(key - Key.D1);
                e.Handled = true;
            }
            else if (key == Key.P)
            {
                SaveScreenshot();
                e.Handled = true;
            }
            else if (key == Key.L)
            {
                this.viewModel.ToggleLanguage();
                e.Handled = true;
            }
            else if (key is Key.C or Key.X or Key.V && Keyboard.FocusedElement is not TextBoxBase and not PasswordBox)
            {
                e.Handled = this.viewModel.Clipboard(key switch
                {
                    Key.C => ClipboardAction.Copy,
                    Key.X => ClipboardAction.Cut,
                    _ => ClipboardAction.Paste,
                });
            }

            return;
        }

        // i：帮助。只在没有在输入框里打字时生效，免得把字母 i 吃掉。
        if (key == Key.I && modifiers == ModifierKeys.None && Keyboard.FocusedElement is not TextBoxBase and not PasswordBox)
        {
            this.viewModel.ToggleHelpCommand.Execute(null);
            e.Handled = true;
        }
    }
}
