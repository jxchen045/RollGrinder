using System;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Nc;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Manual;
using RollGrinder.Services.Measurement;
using RollGrinder.Services.Monitoring;
using RollGrinder.Services.Records;
using RollGrinder.Services.Session;

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
        services.AddSingleton<IUserSession, UserSession>();
        services.AddSingleton<IUserDirectory, UserDirectory>();

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
        services.AddSingleton<IMeasurementService, MeasurementService>();
        services.AddSingleton<IManualCommandService, ManualCommandService>();
        services.AddSingleton<ICompensationService, CompensationService>();
        services.AddSingleton<IRecordService, RecordService>();

        services.AddHostedService<MachineMonitorHostedService>();
        services.AddHostedService<AlarmArchiveHostedService>();

        return services;
    }
}
