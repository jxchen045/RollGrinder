using System;
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
public sealed record ParameterDescriptor(
    string Key,
    ParameterValueKind Kind,
    ParameterUnit Unit,
    ParameterValue DefaultValue,
    double? MinValue = null,
    double? MaxValue = null,
    bool IsRequired = true)
{
    /// <summary>界面文案的资源键，约定为 "Parameter_" + Key。界面不得自行拼中文。</summary>
    public string ResourceKey => "Parameter_" + Key;

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

    /// <summary>声明一个文本参数。</summary>
    public static ParameterDescriptor Text(string key, string defaultValue = "", bool isRequired = false) =>
        new(key, ParameterValueKind.Text, ParameterUnit.None, ParameterValue.FromText(defaultValue), null, null, isRequired);
}
