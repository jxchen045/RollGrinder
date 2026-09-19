using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Device;

/// <summary>
/// 打桩网关：不连接任何机床，把写入的值记在内存里，读取时回放。
/// 用于无机床环境下跑通界面与流程。T-01 只保证契约可用，不模拟磨削过程。
/// </summary>
internal sealed class StubGateway : IMachineGateway
{
    private readonly ITagMap tagMap;
    private readonly Dictionary<string, TagValue> values = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public StubGateway(ITagMap tagMap)
    {
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
    }

    public GatewayConnectionState ConnectionState { get; private set; } = GatewayConnectionState.Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionState = GatewayConnectionState.Connected;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionState = GatewayConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task<MachineStateSnapshot> ReadStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<TagValue> snapshot;
        lock (this.gate)
        {
            snapshot = new List<TagValue>(this.values.Values);
        }

        return Task.FromResult(new MachineStateSnapshot(DateTimeOffset.UtcNow, ConnectionState, snapshot));
    }

    public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TagDescriptor descriptor = this.tagMap.Resolve(logicalName);
        lock (this.gate)
        {
            if (this.values.TryGetValue(logicalName, out TagValue? stored))
            {
                return Task.FromResult(stored);
            }
        }

        return Task.FromResult(new TagValue(descriptor.Key, descriptor.DataType, DefaultOf(descriptor.DataType), DateTimeOffset.UtcNow));
    }

    public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TagDescriptor descriptor = this.tagMap.Resolve(logicalName);
        if (descriptor.Access == TagAccess.Read)
        {
            throw new GatewayException($"Tag '{logicalName}' is read-only.");
        }

        lock (this.gate)
        {
            this.values[logicalName] = value;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        ConnectionState = GatewayConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    private static object? DefaultOf(TagDataType dataType) => dataType switch
    {
        TagDataType.Boolean => false,
        TagDataType.Int32 => 0,
        TagDataType.Double => 0d,
        TagDataType.String => string.Empty,
        _ => null,
    };
}
