using System;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Services.Monitoring;

/// <summary>
/// 机床状态监视：后台按 hmi.json 的周期取数，界面只读最新快照。
/// ViewModel 不直接调用网关，一律经这里。
/// </summary>
public interface IMachineMonitor
{
    /// <summary>最新快照。任何时候都可读，不阻塞。</summary>
    MachineStateSnapshot Current { get; }

    /// <summary>启动后台取数。</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>停止后台取数。上位机退出不影响 NC 继续磨削。</summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>有新快照时触发（来自后台线程）。</summary>
    event EventHandler<MachineStateSnapshot>? SnapshotUpdated;
}
