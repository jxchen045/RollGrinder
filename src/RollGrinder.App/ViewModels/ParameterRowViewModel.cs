using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

        // 开关与选项在界面上是同一种东西——一排分段按钮，所以走同一套数据。
        Choices = new ObservableCollection<ParameterChoiceViewModel>(
            BuildChoices(descriptor, localizer, option => Text = option));

        RefreshChoiceSelection();
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

    /// <summary>开关或选项型：界面渲染成一排分段按钮，不给自由输入。</summary>
    public bool HasChoices => Choices.Count > 0;

    /// <summary>数值或文本型：界面渲染成输入框。</summary>
    public bool IsEditableText => this.descriptor.Kind is ParameterValueKind.Number or ParameterValueKind.Text;

    /// <summary>可选项（数值与文本型为空）。</summary>
    public ObservableCollection<ParameterChoiceViewModel> Choices { get; }

    /// <summary>
    /// 这一格当前是否生效。互斥参数（连续进给 / 周期进给）里没被选中的那个置为 false，
    /// 界面把它压暗并禁掉输入——格子还在原位，只是按不动。
    /// </summary>
    [ObservableProperty]
    private bool isApplicable = true;

    /// <summary>开关型参数的绑定目标。</summary>
    public bool BooleanValue
    {
        get => bool.TryParse(Text, out bool parsed) && parsed;
        set => Text = value ? "true" : "false";
    }

    partial void OnTextChanged(string value) => RefreshChoiceSelection();

    private static IEnumerable<ParameterChoiceViewModel> BuildChoices(
        ParameterDescriptor descriptor,
        IStringLocalizer localizer,
        Action<string> select)
    {
        if (descriptor.Kind == ParameterValueKind.Boolean)
        {
            yield return Choice("true", localizer["Common_On"]);
            yield return Choice("false", localizer["Common_Off"]);
            yield break;
        }

        foreach (string option in descriptor.AllowedValues ?? Array.Empty<string>())
        {
            yield return Choice(option, localizer[descriptor.ChoiceResourceKey(option)]);
        }

        ParameterChoiceViewModel Choice(string key, string label) =>
            new(key, label, new RelayCommand(() => select(key)));
    }

    private void RefreshChoiceSelection()
    {
        foreach (ParameterChoiceViewModel choice in Choices)
        {
            choice.IsSelected = string.Equals(choice.Key, Text, StringComparison.Ordinal);
        }
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

/// <summary>选项型参数的一个可选项。</summary>
public sealed partial class ParameterChoiceViewModel : ObservableObject
{
    public ParameterChoiceViewModel(string key, string label, ICommand command)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Command = command ?? throw new ArgumentNullException(nameof(command));
    }

    /// <summary>选项键（持久化与下发用）。</summary>
    public string Key { get; }

    /// <summary>界面文案。</summary>
    public string Label { get; }

    public ICommand Command { get; }

    [ObservableProperty]
    private bool isSelected;
}
