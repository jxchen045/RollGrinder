using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Contracts;

/// <summary>
/// 业务层访问机床的唯一通道。实现（OPC UA / 仿真 / 打桩 / 文件）由 DI 按配置装配，
/// 业务代码不得判断当前是哪一种实现。失败一律抛 <see cref="GatewayException"/>。
/// 参数下发完成后，磨削由 NC 与 PLC 执行；本接口的任何调用都不在实时控制回路里。
/// </summary>
public interface IMachineGateway : IAsyncDisposable
{
    /// <summary>当前连接状态（不阻塞，取最近一次已知状态）。</summary>
    GatewayConnectionState ConnectionState { get; }

    /// <summary>建立与机床的连接。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>断开与机床的连接；上位机退出不影响 NC 继续磨削。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>读取给定逻辑变量的一次快照。</summary>
    Task<MachineStateSnapshot> ReadStateAsync(IReadOnlyList<string> logicalNames, CancellationToken cancellationToken);

    /// <summary>按逻辑变量名读取一个值。</summary>
    Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken);

    /// <summary>按逻辑变量名写入一个值。</summary>
    Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken);

    /// <summary>
    /// 按给定顺序批量写入。用于参数下发：调用方把"参数有效"标志放在最后一条，
    /// 保证 NC 只在整组参数写完后才认这份数据。
    /// </summary>
    Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken);
}
