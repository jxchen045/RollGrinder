namespace RollGrinder.Contracts.Dtos;

/// <summary>网关实现种类，仅供组合根装配使用。</summary>
public enum GatewayKind
{
    /// <summary>通过 OPC UA 访问 SINUMERIK ONE。</summary>
    OpcUa = 0,

    /// <summary>打桩实现，用于无机床环境下的开发与测试。</summary>
    Stub = 1,

    /// <summary>文件实现，用于离线回放与现场取证。</summary>
    File = 2,
}
