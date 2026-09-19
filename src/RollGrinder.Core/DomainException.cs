using System;

namespace RollGrinder.Core;

/// <summary>
/// 领域层异常：辊形/工序/几何计算等业务规则被违反时抛出。
/// 网关层错误请使用 RollGrinder.Contracts.GatewayException。
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
