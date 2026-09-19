using System;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Device;

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
        services.AddSingleton<IMachineGateway>(_ => CreateGateway(options, machine, tagMap));

        return services;
    }

    private static IMachineGateway CreateGateway(IAppOptions options, MachineDescription machine, ITagMap tagMap) =>
        options.Gateway switch
        {
            GatewayKind.Stub => new StubGateway(tagMap),
            GatewayKind.File => new FileGateway(tagMap, options.DataDirectory),
            GatewayKind.OpcUa => new OpcUaGateway(tagMap, machine.Controller),
            _ => throw new GatewayException($"Unsupported gateway kind '{options.Gateway}'."),
        };
}
