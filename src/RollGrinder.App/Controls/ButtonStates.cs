using System.Windows;
using System.Windows.Media;

namespace RollGrinder.App.Controls;

/// <summary>
/// 按钮在悬停、按下、禁用时各用什么颜色，由每种按钮样式自己声明，共用的按钮模板按声明取色。
///
/// 为什么不在模板里写死：以前模板统一把悬停改成浅灰、按下改成浅蓝——白字的实心按钮
/// （主操作、启动、危险、导航槽）一悬停就成了白字配浅灰，对比度只剩约 1.1:1，字几乎看不见。
/// 禁用也不再用整体半透明：红键半透明成粉色，白字对比度只有 2.3:1，而且看起来像"警告"而不是"不可用"。
///
/// 所有取值都来自 Palette.Light.xaml，对比度由 ButtonContrastTests 逐对核算。
/// </summary>
public static class ButtonStates
{
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(ButtonStates), new PropertyMetadata(null));

    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground", typeof(Brush), typeof(ButtonStates), new PropertyMetadata(null));

    public static readonly DependencyProperty DisabledBackgroundProperty = DependencyProperty.RegisterAttached(
        "DisabledBackground", typeof(Brush), typeof(ButtonStates), new PropertyMetadata(null));

    public static readonly DependencyProperty DisabledForegroundProperty = DependencyProperty.RegisterAttached(
        "DisabledForeground", typeof(Brush), typeof(ButtonStates), new PropertyMetadata(null));

    public static readonly DependencyProperty DisabledBorderBrushProperty = DependencyProperty.RegisterAttached(
        "DisabledBorderBrush", typeof(Brush), typeof(ButtonStates), new PropertyMetadata(null));

    public static Brush? GetHoverBackground(DependencyObject element) => (Brush?)element.GetValue(HoverBackgroundProperty);

    public static void SetHoverBackground(DependencyObject element, Brush? value) => element.SetValue(HoverBackgroundProperty, value);

    public static Brush? GetPressedBackground(DependencyObject element) => (Brush?)element.GetValue(PressedBackgroundProperty);

    public static void SetPressedBackground(DependencyObject element, Brush? value) => element.SetValue(PressedBackgroundProperty, value);

    public static Brush? GetDisabledBackground(DependencyObject element) => (Brush?)element.GetValue(DisabledBackgroundProperty);

    public static void SetDisabledBackground(DependencyObject element, Brush? value) => element.SetValue(DisabledBackgroundProperty, value);

    public static Brush? GetDisabledForeground(DependencyObject element) => (Brush?)element.GetValue(DisabledForegroundProperty);

    public static void SetDisabledForeground(DependencyObject element, Brush? value) => element.SetValue(DisabledForegroundProperty, value);

    public static Brush? GetDisabledBorderBrush(DependencyObject element) => (Brush?)element.GetValue(DisabledBorderBrushProperty);

    public static void SetDisabledBorderBrush(DependencyObject element, Brush? value) => element.SetValue(DisabledBorderBrushProperty, value);
}
