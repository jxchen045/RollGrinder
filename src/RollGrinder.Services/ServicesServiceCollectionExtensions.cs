using System;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Nc;
using RollGrinder.Services.Jobs;
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

        services.AddSingleton(provider => new NcJobTranslator(
            provider.GetRequiredService<RollProfileTypeRegistry>(),
            provider.GetRequiredService<GrindingStepTypeRegistry>(),
            provider.GetRequiredService<ITagMap>(),
            provider.GetRequiredService<MachineDescription>()));
        services.AddSingleton<IJobDownloadService, JobDownloadService>();

        services.AddHostedService<MachineMonitorHostedService>();

        return services;
    }
}
