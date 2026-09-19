using System;
using System.Collections.Generic;
using System.Globalization;

namespace RollGrinder.Contracts.Dtos;

/// <summary>
/// 机床状态的不可变快照。界面只消费快照，刷新频率与数据到达频率解耦。
/// 一次快照里的变量数以十计，查找按线性扫描，避免 with 复制出陈旧索引。
/// </summary>
/// <param name="CapturedAtUtc">快照生成时刻（UTC）。</param>
/// <param name="ConnectionState">网关连接状态。</param>
/// <param name="Values">本次快照包含的变量值。</param>
public sealed record MachineStateSnapshot(
    DateTimeOffset CapturedAtUtc,
    GatewayConnectionState ConnectionState,
    IReadOnlyList<TagValue> Values)
{
    /// <summary>一个未连接的空快照。</summary>
    public static MachineStateSnapshot Empty(DateTimeOffset capturedAtUtc) =>
        new(capturedAtUtc, GatewayConnectionState.Disconnected, Array.Empty<TagValue>());

    /// <summary>按逻辑名取值；不存在返回 false。</summary>
    public bool TryGet(string logicalName, out TagValue? value)
    {
        value = Find(logicalName);
        return value is not null;
    }

    /// <summary>取数值，缺失或质量不好时返回 null。界面据此显示"--"而不是 0。</summary>
    public double? GetNumberOrNull(string logicalName)
    {
        TagValue? value = Find(logicalName);
        if (value is null || !value.IsGood || value.Raw is null)
        {
            return null;
        }

        return value.Raw switch
        {
            double number => number,
            int integer => integer,
            bool flag => flag ? 1.0 : 0.0,
            IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>取布尔值，缺失或质量不好时返回 null。</summary>
    public bool? GetBooleanOrNull(string logicalName)
    {
        TagValue? value = Find(logicalName);
        return value is not null && value.IsGood && value.Raw is bool flag ? flag : null;
    }

    /// <summary>取文本，缺失或质量不好时返回 null。</summary>
    public string? GetTextOrNull(string logicalName)
    {
        TagValue? value = Find(logicalName);
        return value is not null && value.IsGood ? value.Raw as string : null;
    }

    private TagValue? Find(string logicalName)
    {
        for (int i = 0; i < Values.Count; i++)
        {
            if (string.Equals(Values[i].Key, logicalName, StringComparison.Ordinal))
            {
                return Values[i];
            }
        }

        return null;
    }
}
