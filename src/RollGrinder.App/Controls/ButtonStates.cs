using System.Windows;
using System.Windows.Media;

namespace RollGrinder.App.Controls;

/// <summary>
/// 按钮的按下色由每种按钮样式自己声明，共用模板按声明取色；
/// <see cref="IsSelectedProperty"/> 表示"当前"（切换格的选中项、正显示着的功能组），
/// <see cref="IsUnavailableProperty"/> 表示软键按不了（灰字、留在原位，但照样接得住点击，外壳在对话行说原因）。
///
/// 触摸屏没有悬停（最终稿 4.6），所以没有悬停色。
/// 模板用 TemplateBinding 把按下色绑到叠层上，触发器只切换叠层可见性，不放 Binding——
/// 见 Controls.xaml 里 SecondaryButton 的说明（第三轮现场"闪烁"问题的根因）。
/// 禁用态全系统一种灰，直接写在模板里。所有取值都来自 Palette.Light.xaml，对比度由 ButtonContrastTests 逐对核算。
/// </summary>
public static class ButtonStates
{
    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground", typeof(Brush), typeof(ButtonStates), new PropertyMetadata(null));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(ButtonStates), new PropertyMetadata(false));

    public static readonly DependencyProperty IsUnavailableProperty = DependencyProperty.RegisterAttached(
        "IsUnavailable", typeof(bool), typeof(ButtonStates), new PropertyMetadata(false));

    public static Brush? GetPressedBackground(DependencyObject element) => (Brush?)element.GetValue(PressedBackgroundProperty);

    public static void SetPressedBackground(DependencyObject element, Brush? value) => element.SetValue(PressedBackgroundProperty, value);

    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);

    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);

    public static bool GetIsUnavailable(DependencyObject element) => (bool)element.GetValue(IsUnavailableProperty);

    public static void SetIsUnavailable(DependencyObject element, bool value) => element.SetValue(IsUnavailableProperty, value);
}
