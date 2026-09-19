using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Device;

/// <summary>
/// 打桩网关：不连接任何机床，把写入的值记在内存里，读取时回放。
/// 用于无机床环境下跑通界面与流程；需要会"动"的数据请用仿真网关。
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

    public Task<MachineStateSnapshot> ReadStateAsync(IReadOnlyList<string> logicalNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logicalNames);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = new List<TagValue>(logicalNames.Count);
        foreach (string logicalName in logicalNames)
        {
            // tagmap 里没有的变量说明本台机床没有这一项，跳过而不是报错。
            if (!this.tagMap.TryResolve(logicalName, out TagDescriptor? descriptor) || descriptor is null)
            {
                continue;
            }

            snapshot.Add(Read(descriptor));
        }

        return Task.FromResult(new MachineStateSnapshot(DateTimeOffset.UtcNow, ConnectionState, snapshot));
    }

    public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(this.tagMap.Resolve(logicalName)));
    }

    public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        Write(logicalName, value);
        return Task.CompletedTask;
    }

    public Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        foreach (TagWrite write in writes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(write.LogicalName, write.Value);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        ConnectionState = GatewayConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    private TagValue Read(TagDescriptor descriptor)
    {
        lock (this.gate)
        {
            if (this.values.TryGetValue(descriptor.Key, out TagValue? stored))
            {
                return stored;
            }
        }

        return new TagValue(descriptor.Key, descriptor.DataType, DefaultOf(descriptor.DataType), DateTimeOffset.UtcNow);
    }

    private void Write(string logicalName, TagValue value)
    {
        TagDescriptor descriptor = this.tagMap.Resolve(logicalName);
        if (descriptor.Access == TagAccess.Read)
        {
            throw new GatewayException($"Tag '{logicalName}' is read-only.");
        }

        lock (this.gate)
        {
            this.values[logicalName] = value;
        }
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
