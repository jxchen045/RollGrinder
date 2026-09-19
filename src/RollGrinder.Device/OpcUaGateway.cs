using System;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Device;

/// <summary>
/// 经 OPC UA 访问 SINUMERIK ONE 的网关。
/// T-01 为空桩：OPC UA 客户端库尚未引入，所有操作抛出 <see cref="GatewayException"/>。
/// </summary>
internal sealed class OpcUaGateway : IMachineGateway
{
    private const string NotImplementedMessage =
        "OpcUaGateway is not implemented yet (T-01 skeleton). Start with --stub for now.";

    private readonly ITagMap tagMap;
    private readonly ControllerDescription controller;

    public OpcUaGateway(ITagMap tagMap, ControllerDescription controller)
    {
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public GatewayConnectionState ConnectionState => GatewayConnectionState.Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken) => throw new GatewayException(NotImplementedMessage);

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<MachineStateSnapshot> ReadStateAsync(CancellationToken cancellationToken) =>
        throw new GatewayException(NotImplementedMessage);

    public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken) =>
        throw new GatewayException(NotImplementedMessage);

    public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken) =>
        throw new GatewayException(NotImplementedMessage);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
