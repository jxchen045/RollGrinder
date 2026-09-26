using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RollGrinder.Core.Parameters;

/// <summary>参数取值种类。</summary>
public enum ParameterValueKind
{
    Number = 0,
    Boolean = 1,
    Text = 2,

    /// <summary>有限选项之一（例如变速模式）。取值是选项键，界面渲染成分段按钮。</summary>
    Choice = 3,

    /// <summary>一张点表（X, Y 数对），例如点表辊形的 Z（mm）与直径偏差（µm）。界面渲染成表格。</summary>
    Points = 4,
}

/// <summary>点表里的一个点。</summary>
/// <param name="X">横坐标（点表辊形里是段内 Z，mm）。</param>
/// <param name="Y">纵坐标（点表辊形里是直径偏差，µm）。</param>
public readonly record struct TablePoint(double X, double Y);

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

    /// <summary>选项取值（选项键）。</summary>
    public string Choice => Kind == ParameterValueKind.Choice
        ? this.text
        : throw new DomainException($"Parameter value of kind {Kind} is not a choice.");

    /// <summary>
    /// 点表取值。值里只存规范化的文本（"x,y;x,y"），每次取时解析——
    /// 这样两个点表相等就是文本相等，record 的值相等性不会被数组引用搅乱。
    /// </summary>
    public IReadOnlyList<TablePoint> Points => Kind == ParameterValueKind.Points
        ? ParsePoints(this.text)
        : throw new DomainException($"Parameter value of kind {Kind} is not a point table.");

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

    /// <summary>从一组点建点表值。点的次序原样保留（是否递增由用它的类型判断）。</summary>
    public static ParameterValue FromPoints(IEnumerable<TablePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        TablePoint[] list = points.ToArray();
        if (list.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            throw new DomainException("Point table values must be finite.");
        }

        return new ParameterValue(
            ParameterValueKind.Points,
            0.0,
            false,
            string.Join(";", list.Select(point => string.Create(CultureInfo.InvariantCulture, $"{point.X:R},{point.Y:R}"))));
    }

    /// <summary>从选项键建值。是否属于允许集合由 schema 校验，这里不判断。</summary>
    public static ParameterValue FromChoice(string value) =>
        new(ParameterValueKind.Choice, 0.0, false, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>
    /// 调试与异常信息用。必须自己写：record 自动生成的 ToString 会挨个读取每个属性，
    /// 而 Number / Boolean / Text / Choice 这几个属性取错种类就抛——
    /// 结果是"打印一个值"本身会炸，而且炸在毫不相干的地方。
    /// </summary>
    public override string ToString() => Kind + ":" + ToInvariantString();

    /// <summary>与 <see cref="Parse"/> 对应的持久化文本形式（不随界面语言变化）。</summary>
    public string ToInvariantString() => Kind switch
    {
        ParameterValueKind.Number => this.number.ToString("R", CultureInfo.InvariantCulture),
        ParameterValueKind.Boolean => this.boolean ? "true" : "false",
        ParameterValueKind.Text or ParameterValueKind.Choice or ParameterValueKind.Points => this.text,
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

            case ParameterValueKind.Choice:
                return FromChoice(text);

            case ParameterValueKind.Points:
                return FromPoints(ParsePoints(text));

            default:
                throw new DomainException($"Unsupported parameter value kind {kind}.");
        }
    }

    private static TablePoint[] ParsePoints(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<TablePoint>();
        }

        string[] pairs = text.Split(';');
        var points = new TablePoint[pairs.Length];
        for (int i = 0; i < pairs.Length; i++)
        {
            string[] parts = pairs[i].Split(',');
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                throw new DomainException($"'{pairs[i]}' is not a valid point.");
            }

            points[i] = new TablePoint(x, y);
        }

        return points;
    }
}
