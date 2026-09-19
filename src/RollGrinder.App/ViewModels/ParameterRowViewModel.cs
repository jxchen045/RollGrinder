using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.App.Localization;
using RollGrinder.Core;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 一行参数输入。界面按 schema 生成，新增辊形或工序类型不改界面代码。
/// 文案取自资源键 Parameter_&lt;key&gt;，缺资源时退回参数键本身。
/// </summary>
public sealed partial class ParameterRowViewModel : ObservableObject
{
    private readonly ParameterDescriptor descriptor;
    private readonly IStringLocalizer localizer;

    public ParameterRowViewModel(ParameterDescriptor descriptor, ParameterValue value, IStringLocalizer localizer)
    {
        this.descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        this.text = value.ToInvariantString();
    }

    [ObservableProperty]
    private string text;

    [ObservableProperty]
    private string? errorText;

    public string Key => this.descriptor.Key;

    public ParameterValueKind Kind => this.descriptor.Kind;

    /// <summary>界面标签。</summary>
    public string Label
    {
        get
        {
            string localized = this.localizer[this.descriptor.ResourceKey];
            return localized.StartsWith('!') ? this.descriptor.Key : localized;
        }
    }

    /// <summary>单位后缀，无量纲时为空。</summary>
    public string UnitText => this.descriptor.Unit == ParameterUnit.None
        ? string.Empty
        : this.localizer["Unit_" + this.descriptor.Unit];

    /// <summary>取值范围提示，未限定时为空。</summary>
    public string RangeText => this.descriptor is { MinValue: not null, MaxValue: not null }
        ? string.Format(
            CultureInfo.CurrentCulture,
            "{0} … {1}",
            this.descriptor.MinValue,
            this.descriptor.MaxValue)
        : string.Empty;

    public bool IsBoolean => this.descriptor.Kind == ParameterValueKind.Boolean;

    /// <summary>开关型参数的绑定目标。</summary>
    public bool BooleanValue
    {
        get => bool.TryParse(Text, out bool parsed) && parsed;
        set => Text = value ? "true" : "false";
    }

    /// <summary>把当前输入解析成参数值；解析不了返回 null 并填 ErrorText。</summary>
    public ParameterValue? ToParameterValue()
    {
        try
        {
            ErrorText = null;
            return ParameterValue.Parse(this.descriptor.Kind, Text);
        }
        catch (DomainException ex)
        {
            ErrorText = ex.Message;
            return null;
        }
    }
}
