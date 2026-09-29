using System;
using System.Globalization;

namespace RollGrinder.App.Interaction;

/// <summary>数字键盘上的一个键。</summary>
public enum NumericKey
{
    /// <summary>0–9 用 <see cref="NumericEntry.Digit"/>。</summary>
    Digit = 0,

    /// <summary>小数点。</summary>
    DecimalPoint = 1,

    /// <summary>正负号（切换）。</summary>
    Sign = 2,

    /// <summary>退格。</summary>
    Backspace = 3,

    /// <summary>清空。</summary>
    Clear = 4,
}

/// <summary>收下一个数时为什么不收。</summary>
public enum NumericEntryError
{
    /// <summary>收下了。</summary>
    None = 0,

    /// <summary>什么也没输。</summary>
    Empty = 1,

    /// <summary>不是一个数。</summary>
    NotANumber = 2,

    /// <summary>小于下限。</summary>
    BelowMinimum = 3,

    /// <summary>大于上限。</summary>
    AboveMaximum = 4,
}

/// <summary>
/// 数字键盘的输入缓冲（最终稿 4.5）：再点一下选中的格弹出键盘，"↵ 输入"收下并跳到下一格，超出范围不收。
///
/// 规则：
/// 1. 键盘刚弹出时显示原值，第一个数字键**替换**原值（Operate 的习惯：选中即覆盖）；退格则从原值上改；
/// 2. 小数点、正负号各只有一个；不许负数的格按正负号没反应；
/// 3. 小数位数超过格子的精度不再接受；
/// 4. 一律用"."作小数点，与界面语言无关——机床上的数不随中英文切换变样。
/// 纯逻辑，单测覆盖。
/// </summary>
public sealed class NumericEntry
{
    private bool replaceOnNextKey;

    /// <summary>开始编辑一个格。</summary>
    /// <param name="initialText">格里原来的字。</param>
    /// <param name="minimum">下限（含）；null 不限。</param>
    /// <param name="maximum">上限（含）；null 不限。</param>
    /// <param name="decimals">最多几位小数；null 不限，0 表示整数。</param>
    public NumericEntry(string? initialText, double? minimum = null, double? maximum = null, int? decimals = null)
    {
        if (minimum is { } min && maximum is { } max && min > max)
        {
            throw new ArgumentException("Minimum must not exceed maximum.", nameof(minimum));
        }

        Minimum = minimum;
        Maximum = maximum;
        Decimals = decimals;
        Text = (initialText ?? string.Empty).Trim();
        this.replaceOnNextKey = true;
    }

    /// <summary>下限。</summary>
    public double? Minimum { get; }

    /// <summary>上限。</summary>
    public double? Maximum { get; }

    /// <summary>小数位数上限。</summary>
    public int? Decimals { get; }

    /// <summary>许不许负数：下限不小于零就不许。</summary>
    public bool AllowsNegative => Minimum is not { } min || min < 0;

    /// <summary>当前缓冲里的字。</summary>
    public string Text { get; private set; }

    /// <summary>按一个数字键。</summary>
    public void Digit(int digit)
    {
        if (digit is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(digit));
        }

        TakeOverIfFresh();
        int point = Text.IndexOf('.', StringComparison.Ordinal);
        if (point >= 0 && Decimals is { } decimals && Text.Length - point - 1 >= decimals)
        {
            return;
        }

        // 前导零不累积："0" 再按 5 是 "5"，"-0" 再按 5 是 "-5"。
        if (Text is "0" or "-0")
        {
            Text = Text[..^1];
        }

        Text += digit.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>按一个功能键。</summary>
    public void Press(NumericKey key)
    {
        switch (key)
        {
            case NumericKey.DecimalPoint:
                TakeOverIfFresh();
                if (Decimals == 0 || Text.Contains('.', StringComparison.Ordinal))
                {
                    return;
                }

                Text += Text is "" or "-" ? "0." : ".";
                break;

            case NumericKey.Sign:
                this.replaceOnNextKey = false;
                if (!AllowsNegative)
                {
                    return;
                }

                Text = Text.StartsWith('-') ? Text[1..] : "-" + Text;
                break;

            case NumericKey.Backspace:
                this.replaceOnNextKey = false;
                if (Text.Length > 0)
                {
                    Text = Text[..^1];
                }

                break;

            case NumericKey.Clear:
                this.replaceOnNextKey = false;
                Text = string.Empty;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "Use Digit() for digits.");
        }
    }

    /// <summary>"↵ 输入"：能收就给出值，不能收说明原因。</summary>
    public NumericEntryError TryCommit(out double value)
    {
        value = 0;
        if (Text is "" or "-")
        {
            return NumericEntryError.Empty;
        }

        if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            return NumericEntryError.NotANumber;
        }

        if (Minimum is { } min && value < min)
        {
            return NumericEntryError.BelowMinimum;
        }

        if (Maximum is { } max && value > max)
        {
            return NumericEntryError.AboveMaximum;
        }

        return NumericEntryError.None;
    }

    private void TakeOverIfFresh()
    {
        if (this.replaceOnNextKey)
        {
            Text = string.Empty;
            this.replaceOnNextKey = false;
        }
    }
}
