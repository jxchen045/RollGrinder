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

    public string Label { get; }

    public string ValueText { get; }
}
