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

    /// <summary>仿真实现：会按下发参数模拟走刀与去除量，用于无机床联调。</summary>
    Sim = 3,

    /// <summary>
    /// 离线：**根本没有机床**。办公室电脑上编程序、编辑辊形、查记录、打印用这一种。
    /// 与 <see cref="Stub"/> 不同——打桩是"假装有台机床"，离线是明说没有：
    /// 连接状态恒为断开，任何写入都会被拒绝并说清楚原因。
    /// </summary>
    Offline = 4,
}
