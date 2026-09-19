using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Opc.Ua;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Device;

/// <summary>
/// 逻辑变量与 OPC UA 之间的取值换算。
/// 约定：物理量 = 原始值 × Scale，写入时做逆运算。
/// 这里不碰会话，便于脱离机床做单元测试。
/// </summary>
internal static partial class OpcUaValueMapper
{
    /// <summary>
    /// NodeId 的合法写法：可选的 ns=，加上 i=/s=/g=/b= 之一。
    /// 光写一个裸串在 OPC UA 里会被当成 ns=0 的字符串标识——那通常是 tagmap 写错了，
    /// 与其让它悄悄指向一个不存在的节点，不如当场报错。
    /// </summary>
    [GeneratedRegex(@"^(ns=\d+;)?[isgb]=.+$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdSyntax();

    /// <summary>把 tagmap 里的地址解析成 NodeId。</summary>
    public static NodeId ParseNodeId(TagDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!NodeIdSyntax().IsMatch(descriptor.Address))
        {
            throw new GatewayException(
                $"Tag '{descriptor.Key}' has an OPC UA node id '{descriptor.Address}' that is missing its "
                + "i=/s=/g=/b= identifier part.");
        }

        try
        {
            NodeId nodeId = NodeId.Parse(descriptor.Address);
            if (NodeId.IsNull(nodeId))
            {
                throw new GatewayException($"Tag '{descriptor.Key}' has an empty OPC UA node id.");
            }

            return nodeId;
        }
        catch (ServiceResultException ex)
        {
            throw new GatewayException(
                $"Tag '{descriptor.Key}' has an invalid OPC UA node id '{descriptor.Address}': {ex.Message}", ex);
        }
    }

    /// <summary>把一次读取的结果换算成 <see cref="TagValue"/>；质量不好时 IsGood 为 false。</summary>
    public static TagValue ToTagValue(TagDescriptor descriptor, DataValue? dataValue, DateTimeOffset fallbackTimestampUtc)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        DateTimeOffset sampledAtUtc = dataValue is not null && dataValue.SourceTimestamp != DateTime.MinValue
            ? new DateTimeOffset(DateTime.SpecifyKind(dataValue.SourceTimestamp, DateTimeKind.Utc))
            : fallbackTimestampUtc;

        if (dataValue is null || StatusCode.IsNotGood(dataValue.StatusCode) || dataValue.Value is null)
        {
            return new TagValue(descriptor.Key, descriptor.DataType, null, sampledAtUtc, IsGood: false);
        }

        try
        {
            object converted = Convert(descriptor, dataValue.Value, applyScale: true);
            return new TagValue(descriptor.Key, descriptor.DataType, converted, sampledAtUtc);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // 类型对不上是配置问题，不是瞬时故障：标成不可信，让报警带上细节。
            return new TagValue(descriptor.Key, descriptor.DataType, null, sampledAtUtc, IsGood: false);
        }
    }

    /// <summary>把要写入的值换算成 OPC UA 端的原始值。</summary>
    public static object ToOpcValue(TagDescriptor descriptor, TagValue value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(value);

        if (value.Raw is null)
        {
            throw new GatewayException($"Tag '{descriptor.Key}' cannot be written with a null value.");
        }

        try
        {
            return Convert(descriptor, value.Raw, applyScale: false);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new GatewayException(
                $"Tag '{descriptor.Key}' expects {descriptor.DataType} but got '{value.Raw}'.", ex);
        }
    }

    private static object Convert(TagDescriptor descriptor, object raw, bool applyScale)
    {
        switch (descriptor.DataType)
        {
            case TagDataType.Boolean:
                return System.Convert.ToBoolean(raw, CultureInfo.InvariantCulture);

            case TagDataType.Int32:
            {
                double scaled = Scale(System.Convert.ToDouble(raw, CultureInfo.InvariantCulture), descriptor, applyScale);
                return checked((int)Math.Round(scaled, MidpointRounding.AwayFromZero));
            }

            case TagDataType.Double:
                return Scale(System.Convert.ToDouble(raw, CultureInfo.InvariantCulture), descriptor, applyScale);

            case TagDataType.String:
                return System.Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;

            default:
                throw new GatewayException($"Tag '{descriptor.Key}' has an unsupported data type {descriptor.DataType}.");
        }
    }

    private static double Scale(double value, TagDescriptor descriptor, bool applyScale)
    {
        if (descriptor.Scale == 0.0)
        {
            throw new GatewayException($"Tag '{descriptor.Key}' has a scale of zero.");
        }

        return applyScale ? value * descriptor.Scale : value / descriptor.Scale;
    }
}
