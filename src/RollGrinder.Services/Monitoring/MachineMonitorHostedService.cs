using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RollGrinder.Contracts;
using RollGrinder.Services.Alarms;

namespace RollGrinder.Services.Monitoring;

/// <summary>
/// 随宿主启停后台取数。连不上机床只报警不拦启动——界面要能起来让人看到原因。
/// </summary>
public sealed class MachineMonitorHostedService : IHostedService
{
    private readonly IMachineMonitor monitor;
    private readonly IAlarmSink alarms;

    public MachineMonitorHostedService(IMachineMonitor monitor, IAlarmSink alarms)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.monitor.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException ex)
        {
            this.alarms.RaiseException(ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.monitor.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException ex)
        {
            this.alarms.RaiseException(ex);
        }
    }
}
