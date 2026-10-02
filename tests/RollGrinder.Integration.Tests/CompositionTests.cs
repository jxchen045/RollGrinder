using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data;
using RollGrinder.Services;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Library;
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 服务容器能整个建起来：每个注册的构造参数都有人提供（界面层页面用到的服务都从这里来）。
/// 缺注册时这里就红，而不是等上位机启动时才崩。
/// </summary>
public sealed class CompositionTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    [Fact]
    public async Task The_whole_service_container_validates_and_resolves_the_roll_centric_services()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);
        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);
        await new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName)).MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        provider.GetRequiredService<IRollPlanningService>().Should().NotBeNull();
        provider.GetRequiredService<ILibraryService>().Should().NotBeNull();
        provider.GetRequiredService<IRollLedgerService>().Should().NotBeNull();
        provider.GetRequiredService<IJobDownloadService>().Should().NotBeNull();
        provider.GetRequiredService<IRecordService>().Should().NotBeNull();
    }
}
