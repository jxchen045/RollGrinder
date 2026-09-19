namespace RollGrinder.Contracts.Dtos;

/// <summary>网关连接状态。</summary>
public enum GatewayConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Faulted = 3,
}
