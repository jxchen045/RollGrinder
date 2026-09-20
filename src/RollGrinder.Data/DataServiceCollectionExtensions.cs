using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Contracts;

namespace RollGrinder.Data;

/// <summary>存储层的装配。数据库文件固定落在 data/ 下，升级时保留。</summary>
public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddDataStore(this IServiceCollection services, IAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName)));
        services.AddSingleton<IRollRepository, SqliteRollRepository>();
        services.AddSingleton<IJobRepository, SqliteJobRepository>();
        services.AddSingleton<IRollProfileRepository, SqliteRollProfileRepository>();
        services.AddSingleton<IGrindingRecordRepository, SqliteGrindingRecordRepository>();
        services.AddSingleton<IMeasurementRepository, SqliteMeasurementRepository>();
        services.AddSingleton<ICompensationRepository, SqliteCompensationRepository>();
        services.AddSingleton<IAlarmRepository, SqliteAlarmRepository>();

        return services;
    }
}
