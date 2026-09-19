using System;
using System.Globalization;

namespace RollGrinder.Core.Parameters;

/// <summary>参数取值种类。</summary>
public enum ParameterValueKind
{
    Number = 0,
    Boolean = 1,
    Text = 2,
}

/// <summary>
/// 一个参数的取值。不可变，取错种类立即抛 <see cref="DomainException"/>，不做静默转换。
/// </summary>
public sealed record ParameterValue
{
    private readonly double number;
    private readonly bool boolean;
    private readonly string text;

    private ParameterValue(ParameterValueKind kind, double number, bool boolean, string text)
    {
        Kind = kind;
        this.number = number;
        this.boolean = boolean;
        this.text = text;
    }

    public ParameterValueKind Kind { get; }

    /// <summary>数值取值。</summary>
    public double Number => Kind == ParameterValueKind.Number
        ? this.number
        : throw new DomainException($"Parameter value of kind {Kind} is not a number.");

    /// <summary>开关取值。</summary>
    public bool Boolean => Kind == ParameterValueKind.Boolean
        ? this.boolean
        : throw new DomainException($"Parameter value of kind {Kind} is not a boolean.");

    /// <summary>文本取值。</summary>
    public string Text => Kind == ParameterValueKind.Text
        ? this.text
        : throw new DomainException($"Parameter value of kind {Kind} is not text.");

    public static ParameterValue FromNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new DomainException("Parameter numbers must be finite.");
        }

        return new ParameterValue(ParameterValueKind.Number, value, false, string.Empty);
    }

    public static ParameterValue FromBoolean(bool value) =>
        new(ParameterValueKind.Boolean, 0.0, value, string.Empty);

    public static ParameterValue FromText(string value) =>
        new(ParameterValueKind.Text, 0.0, false, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>与 <see cref="Parse"/> 对应的持久化文本形式（不随界面语言变化）。</summary>
    public string ToInvariantString() => Kind switch
    {
        ParameterValueKind.Number => this.number.ToString("R", CultureInfo.InvariantCulture),
        ParameterValueKind.Boolean => this.boolean ? "true" : "false",
        ParameterValueKind.Text => this.text,
        _ => throw new DomainException($"Unsupported parameter value kind {Kind}."),
    };

    /// <summary>从持久化文本还原。</summary>
    public static ParameterValue Parse(ParameterValueKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        switch (kind)
        {
            case ParameterValueKind.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    throw new DomainException($"'{text}' is not a valid number.");
                }

                return FromNumber(number);

            case ParameterValueKind.Boolean:
                if (!bool.TryParse(text, out bool boolean))
                {
                    throw new DomainException($"'{text}' is not a valid boolean.");
                }

                return FromBoolean(boolean);

            case ParameterValueKind.Text:
                return FromText(text);

            default:
                throw new DomainException($"Unsupported parameter value kind {kind}.");
        }
    }
}
