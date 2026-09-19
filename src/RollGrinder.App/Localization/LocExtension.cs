using System;
using System.Windows.Markup;

namespace RollGrinder.App.Localization;

/// <summary>
/// XAML 用法：Text="{loc:Loc Main_Title}"。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => LocalizationScope.Current[Key];
}
