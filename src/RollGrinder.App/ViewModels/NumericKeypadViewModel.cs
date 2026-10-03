using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 数字键盘（最终稿 4.5）：点一下选中一格（青底），再点一下弹出；"↵ 输入"收下并跳到下一格，超出范围不收。
/// 停靠在工作区右下（不挡对话行与软键），键 72×72，戴手套也按得准。
/// 输入规则在 <see cref="NumericEntry"/>（纯逻辑，有单测）；这里只管开关与把结果交回输入框。
/// </summary>
public sealed partial class NumericKeypadViewModel : ObservableObject
{
    private readonly ShellInteraction interaction;
    private readonly IStringLocalizer localizer;
    private NumericEntry? entry;
    private Func<string, bool>? commit;

    public NumericKeypadViewModel(ShellInteraction interaction, IStringLocalizer localizer)
    {
        this.interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>键盘开着。</summary>
    [ObservableProperty]
    private bool isOpen;

    /// <summary>正在输的是哪一格（标签）。</summary>
    [ObservableProperty]
    private string caption = string.Empty;

    /// <summary>允许的范围，例如"允许 18 ~ 45"；不限时为空。</summary>
    [ObservableProperty]
    private string rangeText = string.Empty;

    /// <summary>缓冲里的字。外面直接改它（实体键盘敲进显示格、粘贴）时，缓冲跟着换，范围校验照样生效。</summary>
    [ObservableProperty]
    private string text = string.Empty;

    partial void OnTextChanged(string value)
    {
        if (this.entry is not null && !string.Equals(this.entry.Text, value, StringComparison.Ordinal))
        {
            this.entry = new NumericEntry(value, this.entry.Minimum, this.entry.Maximum, this.entry.Decimals);
        }
    }

    /// <summary>
    /// 弹出键盘。<paramref name="accept"/> 收下文本并写回输入框，写回失败（例如格子自己的校验不过）返回 false，键盘不关。
    /// </summary>
    public void Open(string caption, string? initialText, double? minimum, double? maximum, int? decimals, Func<string, bool> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        this.entry = new NumericEntry(initialText, minimum, maximum, decimals);
        this.commit = accept;
        Caption = caption ?? string.Empty;
        RangeText = (minimum, maximum) switch
        {
            ({ } min, { } max) => this.localizer.Format("Keypad_RangeFormat", Format(min), Format(max)),
            ({ } min, null) => this.localizer.Format("Keypad_MinimumFormat", Format(min)),
            (null, { } max) => this.localizer.Format("Keypad_MaximumFormat", Format(max)),
            _ => string.Empty,
        };
        Text = this.entry.Text;
        IsOpen = true;
    }

    /// <summary>数字键。</summary>
    [RelayCommand]
    private void Digit(string digit)
    {
        if (this.entry is null || !int.TryParse(digit, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            return;
        }

        this.entry.Digit(value);
        Text = this.entry.Text;
    }

    /// <summary>功能键：DecimalPoint / Sign / Backspace / Clear。</summary>
    [RelayCommand]
    private void Key(string key)
    {
        if (this.entry is null || !Enum.TryParse(key, out NumericKey parsed) || parsed == NumericKey.Digit)
        {
            return;
        }

        this.entry.Press(parsed);
        Text = this.entry.Text;
    }

    /// <summary>"↵ 输入"：能收就写回并关掉（视图接着把焦点移到下一格），不能收在对话行说原因。</summary>
    /// <returns>收下了返回 true。</returns>
    public bool Enter()
    {
        if (this.entry is null || this.commit is null)
        {
            return false;
        }

        NumericEntryError error = this.entry.TryCommit(out _);
        if (error != NumericEntryError.None)
        {
            this.interaction.Fail(this.localizer.Format("Keypad_Error_" + error, RangeText));
            return false;
        }

        if (!this.commit(this.entry.Text))
        {
            return false;
        }

        Close();
        return true;
    }

    /// <summary>关掉，不改原值。</summary>
    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        this.entry = null;
        this.commit = null;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
