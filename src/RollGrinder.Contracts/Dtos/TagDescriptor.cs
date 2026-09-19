namespace RollGrinder.Contracts.Dtos;

/// <summary>变量数据类型。</summary>
public enum TagDataType
{
    Boolean = 0,
    Int32 = 1,
    Double = 2,
    String = 3,
}

/// <summary>变量访问权限。</summary>
public enum TagAccess
{
    Read = 0,
    Write = 1,
    ReadWrite = 2,
}

/// <summary>
/// 一个逻辑变量的物理描述，来自 tagmap.json。
/// </summary>
/// <param name="Key">逻辑名，例如 machine.channelState。</param>
/// <param name="Address">物理地址，例如 OPC UA NodeId。</param>
/// <param name="DataType">数据类型。</param>
/// <param name="Access">访问权限。</param>
/// <param name="Unit">工程单位，仅用于显示与校验，可为空。</param>
/// <param name="Scale">物理值 = 原始值 * Scale。</param>
/// <param name="Description">说明，可为空。</param>
/// <param name="ArrayLength">数组长度；大于 1 表示这是一组变量，地址里用 {index} 占位。</param>
/// <param name="IndexOffset">下标偏置：地址里的 {index} 渲染成 IndexOffset + 下标，例如 R 参数从 R[130] 起。</param>
public sealed record TagDescriptor(
    string Key,
    string Address,
    TagDataType DataType,
    TagAccess Access,
    string? Unit = null,
    double Scale = 1.0,
    string? Description = null,
    int ArrayLength = 1,
    int IndexOffset = 0)
{
    /// <summary>地址中的下标占位符。</summary>
    public const string IndexPlaceholder = "{index}";

    /// <summary>是否为数组变量。</summary>
    public bool IsArray => ArrayLength > 1 || Address.Contains(IndexPlaceholder, System.StringComparison.Ordinal);

    /// <summary>取数组中第 index 项的描述；下标从 0 开始。</summary>
    public TagDescriptor AtIndex(int index)
    {
        if (!IsArray)
        {
            throw new GatewayException($"Tag '{Key}' is not an array.");
        }

        if (index < 0 || index >= ArrayLength)
        {
            throw new GatewayException($"Tag '{Key}' has {ArrayLength} entries; index {index} is out of range.");
        }

        return this with
        {
            Key = TagKeySyntax.Indexed(Key, index),
            Address = Address.Replace(
                IndexPlaceholder,
                (IndexOffset + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.StringComparison.Ordinal),
            ArrayLength = 1,
        };
    }
}
