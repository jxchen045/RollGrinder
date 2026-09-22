using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Device;

/// <summary>
/// 离线网关：**根本没有机床**。
///
/// 与 <see cref="StubGateway"/> 的区别是态度——打桩是"假装有台机床，写什么都收下"，
/// 用于开发时把界面跑起来；离线是明说没有：连接状态恒为断开，读回来是空快照，
/// 任何写入都抛 <see cref="GatewayException"/> 并说清楚原因。
///
/// 这样"离线时不该发生的写入"会在开发期就炸出来，而不是悄悄写进一个假机床里，
/// 等接上真机床才发现某个页面一直在偷偷下发。
/// </summary>
internal sealed class OfflineGateway : IMachineGateway
{
    /// <summary>拒绝写入时的说明，界面按它取文案。</summary>
    public const string RefusalResourceKey = "Alarm_OfflineNoMachine";

    private readonly TimeProvider timeProvider;

    public OfflineGateway(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>恒为断开：离线就是没有机床，不假装连上了。</summary>
    public GatewayConnectionState ConnectionState => GatewayConnectionState.Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<MachineStateSnapshot> ReadStateAsync(
        IReadOnlyList<string> logicalNames,
        CancellationToken cancellationToken) =>
        Task.FromResult(MachineStateSnapshot.Empty(this.timeProvider.GetUtcNow()));

    public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken) =>
        Task.FromException<TagValue>(Refuse());

    public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken) =>
        Task.FromException(Refuse());

    public Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken) =>
        Task.FromException(Refuse());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static GatewayException Refuse() =>
        new("The HMI is running offline; there is no machine to read from or write to.");
}
