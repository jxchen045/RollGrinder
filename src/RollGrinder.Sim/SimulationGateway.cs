using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Sim;

/// <summary>
/// 仿真网关：把 <see cref="SimulatedMachine"/> 包成 <see cref="IMachineGateway"/>。
/// 时间由 <see cref="TimeProvider"/> 提供，测试可注入假时钟。
/// </summary>
internal sealed class SimulationGateway : IMachineGateway
{
    private readonly ITagMap tagMap;
    private readonly SimulatedMachine simulatedMachine;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();

    private long lastTimestamp;

    public SimulationGateway(ITagMap tagMap, MachineDescription machine, TimeProvider timeProvider)
    {
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.simulatedMachine = new SimulatedMachine(machine);
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.lastTimestamp = timeProvider.GetTimestamp();
    }

    public GatewayConnectionState ConnectionState { get; private set; } = GatewayConnectionState.Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.gate)
        {
            this.lastTimestamp = this.timeProvider.GetTimestamp();
            ConnectionState = GatewayConnectionState.Connected;
        }

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
        EnsureConnected();

        var values = new List<TagValue>(logicalNames.Count);
        lock (this.gate)
        {
            AdvanceLocked();
            DateTimeOffset now = this.timeProvider.GetUtcNow();
            foreach (string logicalName in logicalNames)
            {
                if (!this.tagMap.TryResolve(logicalName, out TagDescriptor? descriptor) || descriptor is null)
                {
                    continue;
                }

                object? raw = this.simulatedMachine.Read(logicalName);
                values.Add(new TagValue(logicalName, descriptor.DataType, raw, now, raw is not null));
            }

            return Task.FromResult(new MachineStateSnapshot(now, ConnectionState, values));
        }
    }

    public Task<TagValue> ReadTagAsync(string logicalName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();

        TagDescriptor descriptor = this.tagMap.Resolve(logicalName);
        lock (this.gate)
        {
            AdvanceLocked();
            object? raw = this.simulatedMachine.Read(logicalName);
            return Task.FromResult(new TagValue(
                logicalName,
                descriptor.DataType,
                raw,
                this.timeProvider.GetUtcNow(),
                raw is not null));
        }
    }

    public Task WriteTagAsync(string logicalName, TagValue value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        WriteCore(logicalName, value);
        return Task.CompletedTask;
    }

    public Task WriteTagsAsync(IReadOnlyList<TagWrite> writes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        EnsureConnected();
        foreach (TagWrite write in writes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCore(write.LogicalName, write.Value);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        ConnectionState = GatewayConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    private void WriteCore(string logicalName, TagValue value)
    {
        TagDescriptor descriptor = this.tagMap.Resolve(logicalName);
        if (descriptor.Access == TagAccess.Read)
        {
            throw new GatewayException($"Tag '{logicalName}' is read-only.");
        }

        lock (this.gate)
        {
            AdvanceLocked();
            this.simulatedMachine.Write(logicalName, value);
        }
    }

    private void AdvanceLocked()
    {
        long now = this.timeProvider.GetTimestamp();
        TimeSpan elapsed = this.timeProvider.GetElapsedTime(this.lastTimestamp, now);
        this.lastTimestamp = now;
        this.simulatedMachine.Advance(elapsed);
    }

    private void EnsureConnected()
    {
        if (ConnectionState != GatewayConnectionState.Connected)
        {
            throw new GatewayException("The simulation gateway is not connected; call ConnectAsync first.");
        }
    }
}
