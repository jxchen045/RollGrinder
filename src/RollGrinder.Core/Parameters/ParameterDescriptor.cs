using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Parameters;

/// <summary>
/// 一个参数的定义。界面按它生成输入控件，数据库按它做键值持久化，
/// 新增一类辊形或工序只需在自己的 <see cref="ParameterSchema"/> 里声明参数。
/// </summary>
/// <param name="Key">参数键，持久化与下发都用它。</param>
/// <param name="Kind">取值种类。</param>
/// <param name="Unit">工程单位（界面量：长度偏差用微米直径量）。</param>
/// <param name="DefaultValue">默认值。</param>
/// <param name="MinValue">数值下限（含），仅数值参数有效。</param>
/// <param name="MaxValue">数值上限（含），仅数值参数有效。</param>
/// <param name="IsRequired">是否必填。</param>
/// <param name="AllowedValues">选项参数的可选项键；其他种类为空。</param>
public sealed record ParameterDescriptor(
    string Key,
    ParameterValueKind Kind,
    ParameterUnit Unit,
    ParameterValue DefaultValue,
    double? MinValue = null,
    double? MaxValue = null,
    bool IsRequired = true,
    IReadOnlyList<string>? AllowedValues = null)
{
    /// <summary>
    /// 这个参数能不能在**正在执行的那道工序**上改。
    ///
    /// 改了不是马上生效：新值写进 NC 的 R 参数，NC 在下一道次读取。
    /// 上位机改完就脱手，被强制结束时 NC 拿最后收到的值把这支辊磨完（最高原则）。
    ///
    /// 只管"当前这一道"。**还没轮到的工序怎么改都行**——那跟重新编程没有区别。
    /// 默认 false：一个参数要允许在磨削当中动，得有人想清楚为什么。
    /// </summary>
    public bool IsLiveEditable { get; init; }

    /// <summary>界面文案的资源键，约定为 "Parameter_" + Key。界面不得自行拼中文。</summary>
    public string ResourceKey => "Parameter_" + Key;

    /// <summary>某个选项的界面文案资源键，约定为 "Choice_" + Key + "_" + 选项键。</summary>
    public string ChoiceResourceKey(string choice) => "Choice_" + Key + "_" + choice;

    /// <summary>声明一个数值参数。</summary>
    public static ParameterDescriptor Number(
        string key,
        ParameterUnit unit,
        double defaultValue,
        double? minValue = null,
        double? maxValue = null,
        bool isRequired = true)
    {
        if (minValue is not null && maxValue is not null && minValue > maxValue)
        {
            throw new DomainException($"Parameter '{key}' has an empty range.");
        }

        return new ParameterDescriptor(
            key,
            ParameterValueKind.Number,
            unit,
            ParameterValue.FromNumber(defaultValue),
            minValue,
            maxValue,
            isRequired);
    }

    /// <summary>声明一个开关参数。</summary>
    public static ParameterDescriptor Boolean(string key, bool defaultValue, bool isRequired = true) =>
        new(key, ParameterValueKind.Boolean, ParameterUnit.None, ParameterValue.FromBoolean(defaultValue), null, null, isRequired);

    /// <summary>
    /// 声明一个选项参数。取值必须是 <paramref name="allowedValues"/> 之一，
    /// 界面按选项数渲染成分段按钮——不给自由输入，从源头上防错选。
    /// </summary>
    public static ParameterDescriptor Choice(
        string key,
        IEnumerable<string> allowedValues,
        string defaultValue,
        bool isRequired = true)
    {
        ArgumentNullException.ThrowIfNull(allowedValues);
        ArgumentException.ThrowIfNullOrEmpty(defaultValue);

        string[] options = allowedValues.ToArray();
        if (options.Length < 2)
        {
            throw new DomainException($"Choice parameter '{key}' needs at least two options.");
        }

        if (options.Distinct(StringComparer.Ordinal).Count() != options.Length)
        {
            throw new DomainException($"Choice parameter '{key}' has duplicate options.");
        }

        if (!options.Contains(defaultValue, StringComparer.Ordinal))
        {
            throw new DomainException($"Choice parameter '{key}' defaults to an option it does not offer.");
        }

        return new ParameterDescriptor(
            key,
            ParameterValueKind.Choice,
            ParameterUnit.None,
            ParameterValue.FromChoice(defaultValue),
            null,
            null,
            isRequired,
            options);
    }

    /// <summary>声明一个点表参数；默认是空表，由用它的类型决定至少要几个点。</summary>
    public static ParameterDescriptor Points(string key, ParameterUnit unit, bool isRequired = true) =>
        new(key, ParameterValueKind.Points, unit, ParameterValue.FromPoints(Array.Empty<TablePoint>()), null, null, isRequired);

    /// <summary>声明一个文本参数。</summary>
    public static ParameterDescriptor Text(string key, string defaultValue = "", bool isRequired = false) =>
        new(key, ParameterValueKind.Text, ParameterUnit.None, ParameterValue.FromText(defaultValue), null, null, isRequired);
}
