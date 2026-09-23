using System;

namespace RollGrinder.Core;

/// <summary>
/// 领域层异常：辊形/工序/几何计算等业务规则被违反时抛出。
/// 网关层错误请使用 RollGrinder.Contracts.GatewayException。
///
/// <see cref="Exception.Message"/> 是给日志和开发者看的英文；操作员能直接触发的那几种，
/// 另带一个界面资源键 <see cref="ResourceKey"/>，报警条就显示本地化的那句话，
/// 而不是把英文原文拼在中文界面上。
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

    /// <param name="message">日志用英文说明。</param>
    /// <param name="resourceKey">界面资源键，报警条的标题。</param>
    /// <param name="detail">附在标题后面的具体对象（如用户名），不需翻译；没有就 null。</param>
    public DomainException(string message, string resourceKey, string? detail)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        ResourceKey = resourceKey;
        Detail = detail;
    }

    /// <summary>界面资源键；null 表示没有专门的说法，报警条用通用标题加英文原文。</summary>
    public string? ResourceKey { get; }

    /// <summary>附在标题后面的具体对象。</summary>
    public string? Detail { get; }
}
