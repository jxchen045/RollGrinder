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
}
