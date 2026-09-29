using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using RollGrinder.App.Interaction;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Controls;

/// <summary>
/// 触摸输入（最终稿 4.5、4.6、F8），全部用 WPF 自带能力：
///
/// 1. 输入框"点一下选中、再点一下弹键盘"（<see cref="TapToEditProperty"/>）：
///    第一下只是选中（青底），对话行写说明、单位、范围（<see cref="HintProperty"/>）；
///    已选中时再点一下，数值格弹出数字键盘，"↵ 输入"收下并跳到下一格，超出范围不收。
///    键盘上按回车同样收下并跳到下一格。
/// 2. 长按 0.6 秒 = 打开 / 编辑这一行（<see cref="LongPressCommandProperty"/>），扫动翻页不算。
/// 3. <see cref="IsPendingProperty"/>：会马上影响机床、还没确认写入的值标红。
/// </summary>
public static class TouchInput
{
    public static readonly DependencyProperty TapToEditProperty = DependencyProperty.RegisterAttached(
        "TapToEdit", typeof(bool), typeof(TouchInput), new PropertyMetadata(false, OnTapToEditChanged));

    /// <summary>数值格：再点一下弹数字键盘。文本格（名称、备注）弹系统键盘，不弹数字键盘。</summary>
    public static readonly DependencyProperty IsNumericProperty = DependencyProperty.RegisterAttached(
        "IsNumeric", typeof(bool), typeof(TouchInput), new PropertyMetadata(true));

    /// <summary>下限（含）；NaN 不限。</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.RegisterAttached(
        "Minimum", typeof(double), typeof(TouchInput), new PropertyMetadata(double.NaN));

    /// <summary>上限（含）；NaN 不限。</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.RegisterAttached(
        "Maximum", typeof(double), typeof(TouchInput), new PropertyMetadata(double.NaN));

    /// <summary>最多几位小数；-1 不限。</summary>
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.RegisterAttached(
        "Decimals", typeof(int), typeof(TouchInput), new PropertyMetadata(-1));

    /// <summary>键盘标题（这一格叫什么）。</summary>
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.RegisterAttached(
        "Caption", typeof(string), typeof(TouchInput), new PropertyMetadata(null));

    /// <summary>选中时对话行上的说明。</summary>
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(string), typeof(TouchInput), new PropertyMetadata(null));

    /// <summary>还没确认写入的值：标红。</summary>
    public static readonly DependencyProperty IsPendingProperty = DependencyProperty.RegisterAttached(
        "IsPending", typeof(bool), typeof(TouchInput), new PropertyMetadata(false));

    public static readonly DependencyProperty LongPressCommandProperty = DependencyProperty.RegisterAttached(
        "LongPressCommand", typeof(ICommand), typeof(TouchInput), new PropertyMetadata(null, OnLongPressCommandChanged));

    /// <summary>长按命令的参数；不设用元素的 DataContext（列表里就是那一行）。</summary>
    public static readonly DependencyProperty LongPressParameterProperty = DependencyProperty.RegisterAttached(
        "LongPressParameter", typeof(object), typeof(TouchInput), new PropertyMetadata(null));

    private static readonly DependencyProperty WasFocusedProperty = DependencyProperty.RegisterAttached(
        "WasFocused", typeof(bool), typeof(TouchInput), new PropertyMetadata(false));

    private static readonly DependencyProperty PressStateProperty = DependencyProperty.RegisterAttached(
        "PressState", typeof(LongPressState), typeof(TouchInput), new PropertyMetadata(null));

    public static bool GetTapToEdit(DependencyObject element) => (bool)element.GetValue(TapToEditProperty);

    public static void SetTapToEdit(DependencyObject element, bool value) => element.SetValue(TapToEditProperty, value);

    public static bool GetIsNumeric(DependencyObject element) => (bool)element.GetValue(IsNumericProperty);

    public static void SetIsNumeric(DependencyObject element, bool value) => element.SetValue(IsNumericProperty, value);

    public static double GetMinimum(DependencyObject element) => (double)element.GetValue(MinimumProperty);

    public static void SetMinimum(DependencyObject element, double value) => element.SetValue(MinimumProperty, value);

    public static double GetMaximum(DependencyObject element) => (double)element.GetValue(MaximumProperty);

    public static void SetMaximum(DependencyObject element, double value) => element.SetValue(MaximumProperty, value);

    public static int GetDecimals(DependencyObject element) => (int)element.GetValue(DecimalsProperty);

    public static void SetDecimals(DependencyObject element, int value) => element.SetValue(DecimalsProperty, value);

    public static string? GetCaption(DependencyObject element) => (string?)element.GetValue(CaptionProperty);

    public static void SetCaption(DependencyObject element, string? value) => element.SetValue(CaptionProperty, value);

    public static string? GetHint(DependencyObject element) => (string?)element.GetValue(HintProperty);

    public static void SetHint(DependencyObject element, string? value) => element.SetValue(HintProperty, value);

    public static bool GetIsPending(DependencyObject element) => (bool)element.GetValue(IsPendingProperty);

    public static void SetIsPending(DependencyObject element, bool value) => element.SetValue(IsPendingProperty, value);

    public static ICommand? GetLongPressCommand(DependencyObject element) => (ICommand?)element.GetValue(LongPressCommandProperty);

    public static void SetLongPressCommand(DependencyObject element, ICommand? value) => element.SetValue(LongPressCommandProperty, value);

    public static object? GetLongPressParameter(DependencyObject element) => element.GetValue(LongPressParameterProperty);

    public static void SetLongPressParameter(DependencyObject element, object? value) => element.SetValue(LongPressParameterProperty, value);

    // ── 点一下选中、再点一下弹键盘 ──────────────────────────────────────────

    private static void OnTapToEditChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            box.PreviewMouseLeftButtonDown += OnBoxPressed;
            box.PreviewMouseLeftButtonUp += OnBoxReleased;
            box.GotKeyboardFocus += OnBoxFocused;
            box.LostKeyboardFocus += OnBoxUnfocused;
            box.PreviewKeyDown += OnBoxKeyDown;
        }
        else
        {
            box.PreviewMouseLeftButtonDown -= OnBoxPressed;
            box.PreviewMouseLeftButtonUp -= OnBoxReleased;
            box.GotKeyboardFocus -= OnBoxFocused;
            box.LostKeyboardFocus -= OnBoxUnfocused;
            box.PreviewKeyDown -= OnBoxKeyDown;
        }
    }

    private static void OnBoxPressed(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        box.SetValue(WasFocusedProperty, box.IsKeyboardFocusWithin);
    }

    private static void OnBoxReleased(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        if (!(bool)box.GetValue(WasFocusedProperty) || box.IsReadOnly || !box.IsEnabled)
        {
            // 第一下：只选中（青底 + 对话行说明），不弹键盘。
            return;
        }

        if (GetIsNumeric(box) && Shell(box) is { } shell)
        {
            OpenKeypad(box, shell);
        }
    }

    private static void OnBoxFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        box.SelectAll();
        Shell(box)?.ShowFieldHint(GetHint(box) ?? GetCaption(box));
    }

    private static void OnBoxUnfocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        Shell(box)?.ShowFieldHint(null);
    }

    /// <summary>键盘上的回车：收下并跳到下一格（最终稿 4.5 "↵ 输入"）。有待确认的事时窗口先把回车拿去确认。</summary>
    private static void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { AcceptsReturn: false } box)
        {
            return;
        }

        if (Accept(box, box.Text))
        {
            e.Handled = true;
        }
    }

    private static void OpenKeypad(TextBox box, ShellViewModel shell)
    {
        double min = GetMinimum(box);
        double max = GetMaximum(box);
        int decimals = GetDecimals(box);
        shell.Keypad.Open(
            GetCaption(box) ?? string.Empty,
            box.Text,
            double.IsNaN(min) ? null : min,
            double.IsNaN(max) ? null : max,
            decimals < 0 ? null : decimals,
            text => Accept(box, text));
    }

    /// <summary>把文本写回输入框、推给绑定；绑定校验不过就不收。收下后焦点移到下一格。</summary>
    private static bool Accept(TextBox box, string text)
    {
        box.Text = text;
        BindingExpression? binding = box.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource();
        if (Validation.GetHasError(box))
        {
            return false;
        }

        box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        return true;
    }

    private static ShellViewModel? Shell(DependencyObject element) =>
        Window.GetWindow(element)?.DataContext as ShellViewModel;

    // ── 长按 ────────────────────────────────────────────────────────────────

    private static void OnLongPressCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseLeftButtonDown -= OnLongPressDown;
        element.PreviewMouseMove -= OnLongPressMove;
        element.PreviewMouseLeftButtonUp -= OnLongPressUp;
        element.MouseLeave -= OnLongPressCancel;
        if (e.NewValue is not null)
        {
            element.PreviewMouseLeftButtonDown += OnLongPressDown;
            element.PreviewMouseMove += OnLongPressMove;
            element.PreviewMouseLeftButtonUp += OnLongPressUp;
            element.MouseLeave += OnLongPressCancel;
        }
    }

    private static void OnLongPressDown(object sender, MouseButtonEventArgs e)
    {
        var element = (UIElement)sender;
        LongPressState state = element.GetValue(PressStateProperty) as LongPressState ?? new LongPressState(element);
        element.SetValue(PressStateProperty, state);
        Point at = e.GetPosition(element);
        state.Begin(at);
    }

    private static void OnLongPressMove(object sender, MouseEventArgs e)
    {
        var element = (UIElement)sender;
        if (element.GetValue(PressStateProperty) is LongPressState state)
        {
            Point at = e.GetPosition(element);
            state.Detector.Move(at.X, at.Y);
        }
    }

    private static void OnLongPressUp(object sender, MouseButtonEventArgs e)
    {
        var element = (UIElement)sender;
        if (element.GetValue(PressStateProperty) is LongPressState state)
        {
            state.End();

            // 已经当长按处理过了：抬起时不再当成一次普通点击。
            if (state.Detector.Fired)
            {
                e.Handled = true;
            }
        }
    }

    private static void OnLongPressCancel(object sender, MouseEventArgs e)
    {
        if (((UIElement)sender).GetValue(PressStateProperty) is LongPressState state)
        {
            state.End();
        }
    }

    /// <summary>一个元素上的长按计时：按下起表，50 ms 查一次。</summary>
    private sealed class LongPressState
    {
        private readonly UIElement element;
        private readonly DispatcherTimer timer;

        public LongPressState(UIElement element)
        {
            this.element = element;
            this.timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
            this.timer.Tick += OnTick;
        }

        public LongPressDetector Detector { get; } = new();

        public void Begin(Point at)
        {
            Detector.Down(at.X, at.Y, DateTimeOffset.UtcNow);
            this.timer.Start();
        }

        public void End()
        {
            Detector.Up();
            this.timer.Stop();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (!Detector.Poll(DateTimeOffset.UtcNow))
            {
                return;
            }

            this.timer.Stop();
            ICommand? command = GetLongPressCommand(this.element);
            object? parameter = GetLongPressParameter(this.element)
                ?? (this.element as FrameworkElement)?.DataContext;
            if (command?.CanExecute(parameter) == true)
            {
                command.Execute(parameter);
            }
        }
    }
}
