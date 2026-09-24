using System.Windows;
using System.Windows.Media;

namespace RollGrinder.App.Controls;

/// <summary>
/// 按钮的悬停色、按下色由每种按钮样式自己声明，共用模板按声明取色；
/// 分段选择键（曲线切换、参数选项）用 <see cref="IsSelectedProperty"/> 表示"当前选中"。
///
/// 为什么不在模板里写死：以前模板统一把悬停改成浅灰、按下改成浅蓝——白字的实心按钮
/// （主操作、启动、危险、导航槽）一悬停就成了白字配浅灰，对比度只剩约 1.1:1，字几乎看不见。
///
/// 模板用 TemplateBinding 把这两个颜色绑到各自的叠层上，触发器只切换叠层可见性，不放 Binding——
/// 见 Controls.xaml 里 SecondaryButton 的说明（"鼠标停在按钮上闪烁"的根因）。
/// 禁用态全系统一种中性灰，直接写在模板里，不需要每个样式声明。
///
/// 所有取值都来自 Palette.Light.xaml，对比度由 ButtonContrastTests 逐对核算。
/// </summary>
public static class ButtonStates
{
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(ButtonStates), new PropertyMetadata(null));

    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground", typeof(Brush), typeof(ButtonStates), new PropertyMetadata(null));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(ButtonStates), new PropertyMetadata(false));

    public static Brush? GetHoverBackground(DependencyObject element) => (Brush?)element.GetValue(HoverBackgroundProperty);

    public static void SetHoverBackground(DependencyObject element, Brush? value) => element.SetValue(HoverBackgroundProperty, value);

    public static Brush? GetPressedBackground(DependencyObject element) => (Brush?)element.GetValue(PressedBackgroundProperty);

    public static void SetPressedBackground(DependencyObject element, Brush? value) => element.SetValue(PressedBackgroundProperty, value);

    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);

    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
}
