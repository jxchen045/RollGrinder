namespace RollGrinder.Contracts.Dtos;

/// <summary>一次写入请求。</summary>
/// <param name="LogicalName">逻辑变量名。</param>
/// <param name="Value">要写入的值。</param>
public sealed record TagWrite(string LogicalName, TagValue Value);
