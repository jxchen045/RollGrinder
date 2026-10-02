using System;
using RollGrinder.App.Localization;

namespace RollGrinder.App.ViewModels;

/// <summary>只读的"标签 → 取值"格子。</summary>
public sealed class LabelValueViewModel
{
    public LabelValueViewModel(string labelResourceKey, string valueText, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Label = localizer[labelResourceKey];
        ValueText = valueText;
    }

    private LabelValueViewModel(string label, string valueText)
    {
        Label = label;
        ValueText = valueText;
    }

    /// <summary>标签不是文案而是数据本身（辊号、改动的条目）。</summary>
    public static LabelValueViewModel Raw(string label, string valueText) => new(label, valueText);

    public string Label { get; }

    public string ValueText { get; }
}
