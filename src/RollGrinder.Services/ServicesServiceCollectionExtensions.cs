using System;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Calibration;
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
        services.AddSingleton<ICalibrationService, CalibrationService>();
        services.AddSingleton<IWheelChangeService, WheelChangeService>();
        services.AddSingleton<IStepParameterUpdateService, StepParameterUpdateService>();
        services.AddSingleton<IStepFlowControlService, StepFlowControlService>();

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
        // 机床时间倍率：仿真开了加速时由组合根另行注册；没注册就是真机的 1 倍。
        services.AddSingleton(provider => new Records.CyclePlausibilityMonitor(
            provider.GetService<MachineTimeScale>() ?? MachineTimeScale.RealTime));
        services.AddSingleton<IJobDownloadService, JobDownloadService>();
        services.AddSingleton<IMeasurementService, MeasurementService>();
        services.AddSingleton<ICentringService, CentringService>();
        services.AddSingleton<IManualCommandService, ManualCommandService>();
        services.AddSingleton<ISurfaceTraceService, SurfaceTraceService>();


        services.AddSingleton<ICompensationService, CompensationService>();
        services.AddSingleton<IRecordService, RecordService>();
        services.AddSingleton<IReportService, ReportService>();
        services.AddSingleton<IReportPrintQueue, ReportPrintQueue>();
        services.AddSingleton<Diagnostics.IDiagnosticsExportService, Diagnostics.DiagnosticsExportService>();

        services.AddHostedService<MachineMonitorHostedService>();

        // 机床报警搬运工：没有别的入口，靠宿主启动它订上监视器。
        services.AddHostedService<MachineAlarmWatcher>();

        // 测量工序走完就把那一趟的读数存成一次测量；同样没有别的入口。
        services.AddHostedService<MeasurementCaptureService>();
        services.AddHostedService<RecordCompletionService>();
        services.AddHostedService<AlarmArchiveHostedService>();

        return services;
    }
}
