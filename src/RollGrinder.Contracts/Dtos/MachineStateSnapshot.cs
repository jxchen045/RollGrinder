using System;
using System.Collections.Generic;

namespace RollGrinder.Contracts.Dtos;

/// <summary>
/// 机床状态的不可变快照。界面只消费快照，与数据到达频率解耦。
/// T-01 仅定义骨架字段，磨削相关字段由后续任务扩展。
/// </summary>
/// <param name="CapturedAtUtc">快照生成时刻（UTC）。</param>
/// <param name="ConnectionState">网关连接状态。</param>
/// <param name="Values">本次快照包含的变量值。</param>
public sealed record MachineStateSnapshot(
    DateTimeOffset CapturedAtUtc,
    GatewayConnectionState ConnectionState,
    IReadOnlyList<TagValue> Values)
{
    /// <summary>一个未连接的空快照。</summary>
    public static MachineStateSnapshot Empty(DateTimeOffset capturedAtUtc) =>
        new(capturedAtUtc, GatewayConnectionState.Disconnected, Array.Empty<TagValue>());
}
