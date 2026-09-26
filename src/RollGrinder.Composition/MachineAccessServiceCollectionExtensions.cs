using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Device;
using RollGrinder.Sim;

namespace RollGrinder.Composition;

/// <summary>
/// 组合根：唯一允许按网关种类分支的地方。
/// 业务代码与 ViewModel 只看得到 <see cref="IMachineGateway"/>。
/// </summary>
public static class MachineAccessServiceCollectionExtensions
{
    /// <summary>
    /// 注册机床访问相关服务。机床描述与变量映射需在调用前异步载入，
    /// 以免在 DI 工厂里同步等待。
    /// </summary>
    public static IServiceCollection AddMachineAccess(
        this IServiceCollection services,
        IAppOptions options,
        MachineDescription machine,
        ITagMap tagMap)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(tagMap);

        services.AddSingleton(options);
        services.AddSingleton(machine);
        services.AddSingleton(tagMap);
        services.AddSingleton<IMachineConfigProvider>(_ => new JsonMachineConfigProvider(options));
        services.AddSingleton(MachineCapabilityFactory.Create(machine));

        // 仿真加速时机床时间比墙上时间快：判"磨得快得不可能"时要按这个倍数换算。
        services.AddSingleton(options.Gateway == GatewayKind.Sim && options.SimulationSpeed > 1.0
            ? new MachineTimeScale(options.SimulationSpeed)
            : MachineTimeScale.RealTime);
        services.AddSingleton<IMachineGateway>(provider => CreateGateway(
            options,
            machine,
            tagMap,
            provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));

        return services;
    }

    /// <summary>PKI 目录（OPC UA 客户端证书），放在 data/ 下随现场数据一起保留。</summary>
    public const string PkiDirectoryName = "pki";

    private static IMachineGateway CreateGateway(
        IAppOptions options,
        MachineDescription machine,
        ITagMap tagMap,
        ILoggerFactory loggerFactory) =>
        options.Gateway switch
        {
            GatewayKind.Stub => new StubGateway(tagMap),
            GatewayKind.Offline => new OfflineGateway(TimeProvider.System),
            GatewayKind.Sim => new SimulationGateway(
                tagMap,
                machine,
                options.SimulationSpeed > 1.0
                    ? new AcceleratedTimeProvider(TimeProvider.System, options.SimulationSpeed)
                    : TimeProvider.System),
            GatewayKind.File => new FileGateway(tagMap, options.DataDirectory, options.ReplayFilePath, TimeProvider.System),
            GatewayKind.OpcUa => new OpcUaGateway(
                tagMap,
                machine.Controller,
                Path.Combine(options.DataDirectory, PkiDirectoryName),
                loggerFactory),
            _ => throw new GatewayException($"Unsupported gateway kind '{options.Gateway}'."),
        };
}
