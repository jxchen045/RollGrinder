using System;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;

namespace RollGrinder.Composition;

/// <summary>
/// 领域注册表的装配。新增一类辊形或工序：在这里多注册一行实现类即可，
/// 界面、NC 生成器与数据库都不改。
/// </summary>
public static class DomainServiceCollectionExtensions
{
    public static IServiceCollection AddDomainRegistries(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRollProfileType, CylindricalProfileType>();
        services.AddSingleton<IRollProfileType, TaperProfileType>();
        services.AddSingleton<IRollProfileType, CrownProfileType>();
        services.AddSingleton<IRollProfileType, CvcProfileType>();

        services.AddSingleton<IGrindingStepType, RoughGrindingStepType>();
        services.AddSingleton<IGrindingStepType, FinishGrindingStepType>();
        services.AddSingleton<IGrindingStepType, SparkOutStepType>();
        services.AddSingleton<IGrindingStepType, MeasureStepType>();

        services.AddSingleton<RollProfileTypeRegistry>();
        services.AddSingleton<GrindingStepTypeRegistry>();
        services.AddSingleton<GrindingJobValidator>();

        return services;
    }
}
