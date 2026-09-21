using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.Converters;

/// <summary>工序行的底色：当前工序红、下一道黄、其余浅灰。</summary>
public sealed class StepStateToBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is StepRowState state
            ? state switch
            {
                StepRowState.Current => "Brush.CurrentStep",
                StepRowState.Next => "Brush.NextStep",
                _ => "Brush.StepIdle",
            }
            : "Brush.StepIdle";

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>工序行的字色：跟着底色走，保证对比度。</summary>
public sealed class StepStateToForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is StepRowState state
            ? state switch
            {
                StepRowState.Current => "Brush.OnCurrentStep",
                StepRowState.Next => "Brush.OnNextStep",
                StepRowState.Done => "Brush.TextMuted",
                _ => "Brush.TextSecondary",
            }
            : "Brush.TextSecondary";

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>枚举相等比较：用于分段按钮的选中态。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null
        && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>报警级别 → 顶栏状态条底色。</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is Services.Alarms.AlarmSeverity severity
            ? severity switch
            {
                Services.Alarms.AlarmSeverity.Error => "Brush.CurrentStep",
                Services.Alarms.AlarmSeverity.Warning => "Brush.WarningFill",
                _ => "Brush.AccentDark",
            }
            : "Brush.AccentDark";

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>功能键种类 → 按钮样式。</summary>
public sealed class FunctionKeyStyleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is FunctionKeyKind kind
            ? kind switch
            {
                FunctionKeyKind.Primary => "PrimaryButton",
                FunctionKeyKind.Start => "StartButton",
                FunctionKeyKind.Danger => "DangerButton",
                FunctionKeyKind.Navigation => "NavigationButton",
                _ => "SecondaryButton",
            }
            : "SecondaryButton";

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>资源键 → 本地化文案。顶栏的上下文标签用它。</summary>
public sealed class LocalizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && key.Length > 0 ? Localization.LocalizationScope.Current[key] : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>比例 → 像素宽度。进度条用。</summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double number ? Math.Clamp(number, 0.0, 1.0) : 0.0;
        double total = parameter is string text && double.TryParse(text, NumberStyles.Float, culture, out double parsed)
            ? parsed
            : 100.0;

        return fraction * total;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>布尔取反。浮层盖住时，底下的页面用它变成不可点。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool flag || !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool flag || !flag;
}

/// <summary>页名 → "…… 有未保存的修改"。文案在 resx 里，这里只做填空。</summary>
public sealed class LeaveTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Localization.LocalizationScope.Current.Format("Leave_TitleFormat", value as string ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>有文字就显示，没文字就收起来。瞬时提示条用它。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>文本非空 → 可见。空字符串就收起来，不留一行空白。</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
