using System;

namespace RollGrinder.Contracts.Dtos;

/// <summary>
/// 一个变量的取值与采样时刻。Raw 为已按 Scale 换算后的工程量。
/// </summary>
/// <param name="Key">逻辑名。</param>
/// <param name="DataType">数据类型。</param>
/// <param name="Raw">取值，装箱为 object；由调用方按 DataType 取用。</param>
/// <param name="SampledAtUtc">采样时刻（UTC）。</param>
/// <param name="IsGood">机床侧质量标记；false 表示该值不可信。</param>
public sealed record TagValue(
    string Key,
    TagDataType DataType,
    object? Raw,
    DateTimeOffset SampledAtUtc,
    bool IsGood = true);
