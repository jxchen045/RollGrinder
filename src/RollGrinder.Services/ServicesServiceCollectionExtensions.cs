using System;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services;

/// <summary>应用服务的装配。ViewModel 只依赖这里注册的接口。</summary>
public static class ServicesServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, HmiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<AlarmLog>(provider => new AlarmLog(
            settings.AlarmHistoryLimit,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAlarmLog>(provider => provider.GetRequiredService<AlarmLog>());
        services.AddSingleton<IAlarmSink>(provider => provider.GetRequiredService<AlarmLog>());

        services.AddSingleton<IMachineMonitor>(provider => new MachineMonitor(
            provider.GetRequiredService<IMachineGateway>(),
            provider.GetRequiredService<MachineDescription>(),
            settings,
            provider.GetRequiredService<IAlarmSink>(),
            provider.GetRequiredService<TimeProvider>()));

        services.AddHostedService<MachineMonitorHostedService>();

        return services;
    }
}
