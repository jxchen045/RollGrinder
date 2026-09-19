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
public sealed record TagDescriptor(
    string Key,
    string Address,
    TagDataType DataType,
    TagAccess Access,
    string? Unit = null,
    double Scale = 1.0,
    string? Description = null);
