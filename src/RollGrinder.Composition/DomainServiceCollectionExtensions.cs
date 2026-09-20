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

        // 工序类型：设计稿的 11 种，外加无火花光磨。
        // 注册顺序就是界面里"插入工序"下拉的顺序，按一支辊的工艺先后排。
        services.AddSingleton<IGrindingStepType, StartStepType>();
        services.AddSingleton<IGrindingStepType, ShortStrokeStepType>();
        services.AddSingleton<IGrindingStepType, RoughGrindingStepType>();
        services.AddSingleton<IGrindingStepType, WheelDressStepType>();
        services.AddSingleton<IGrindingStepType, SemiFinishGrindingStepType>();
        services.AddSingleton<IGrindingStepType, FinishGrindingStepType>();
        services.AddSingleton<IGrindingStepType, SparkOutStepType>();
        services.AddSingleton<IGrindingStepType, MeasureStepType>();
        services.AddSingleton<IGrindingStepType, PolishStepType>();
        services.AddSingleton<IGrindingStepType, ChamferStepType>();
        services.AddSingleton<IGrindingStepType, EddyCurrentStepType>();
        services.AddSingleton<IGrindingStepType, EndStepType>();

        services.AddSingleton<RollProfileTypeRegistry>();
        services.AddSingleton<GrindingStepTypeRegistry>();
        services.AddSingleton<GrindingJobValidator>();

        return services;
    }
}
