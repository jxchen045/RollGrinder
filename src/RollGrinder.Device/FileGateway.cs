using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Device;

/// <summary>
/// 文件网关：从录制文件回放机床数据，用于离线复盘。
/// T-01 为空桩：所有操作抛出 <see cref="GatewayException"/>。
/// </summary>
internal sealed class FileGateway : IMachineGateway
{
    private const string NotImplementedMessage =
        "FileGateway is not implemented yet (T-01 skeleton). Start with --stub for now.";

    private readonly ITagMap tagMap;
    private readonly string dataDirectory;

    public FileGateway(ITagMap tagMap, string dataDirectory)
    {
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    }

    public GatewayConnectionState ConnectionState => GatewayConnectionState.Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken) => throw new GatewayException(NotImplementedMessage);

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<MachineStateSnapshot> ReadStateAsync(IReadOnlyList<string> logicalNames, CancellationToken cancellationToken) =>
        throw new GatewayException(NotImplementedMessage);

    public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken) =>
        throw new GatewayException(NotImplementedMessage);

    public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken) =>
        throw new GatewayException(NotImplementedMessage);

    public Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken) =>
        throw new GatewayException(NotImplementedMessage);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
