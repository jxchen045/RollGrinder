using System;

namespace RollGrinder.Contracts;

/// <summary>
/// 网关层异常：与机床通信、变量寻址、类型转换失败时抛出。
/// 界面层统一捕获并转为报警条目，禁止静默吞异常。
/// </summary>
public class GatewayException : Exception
{
    public GatewayException(string message)
        : base(message)
    {
    }

    public GatewayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// 界面资源键（可空）。带了键的，报警条显示本地化的那句话、不带英文细节——
    /// 例如离线模式下"没有机床可读写"是预期内的状态，不是通信故障。
    /// </summary>
    public string? ResourceKey { get; init; }
}
