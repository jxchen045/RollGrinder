using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Services;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Measurement;
using RollGrinder.Services.Monitoring;
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 启动冒烟：按 Program.Main 的顺序把非界面部分整套跑一遍——
/// 首次建配置、迁库、装配 DI、起后台服务。
/// 界面类型是 WPF 的，只能在 Windows 上验证；这里先把装配错误挡在前面。
/// </summary>
public sealed class StartupSmokeTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    private async Task<IHost> BuildHostAsync(params string[] args)
    {
        AppOptions options = AppOptions.Parse(args, this.workspace.Root);

        await ConfigBootstrapper.EnsureConfigurationAsync(
            options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);

        await new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName))
            .MigrateAsync(CancellationToken.None);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddMachineAccess(options, machine, tagMap);
        builder.Services.AddDomainRegistries();
        builder.Services.AddDataStore(options);
        builder.Services.AddApplicationServices(settings);
        return builder.Build();
    }

    [Fact]
    public async Task The_whole_service_graph_resolves_in_simulation_mode()
    {
        using IHost host = await BuildHostAsync("--gateway", "sim");

        // 界面用到的每一个服务都必须能解析出来，否则窗口一打开就炸。
        host.Services.GetRequiredService<IMachineGateway>().Should().NotBeNull();
        host.Services.GetRequiredService<IMachineMonitor>().Should().NotBeNull();
        host.Services.GetRequiredService<IAlarmLog>().Should().NotBeNull();
        host.Services.GetRequiredService<IJobDownloadService>().Should().NotBeNull();
        host.Services.GetRequiredService<IMeasurementService>().Should().NotBeNull();
        host.Services.GetRequiredService<ICompensationService>().Should().NotBeNull();
        host.Services.GetRequiredService<IRecordService>().Should().NotBeNull();
        host.Services.GetRequiredService<RollProfileTypeRegistry>().All.Should().HaveCount(4);
        host.Services.GetRequiredService<GrindingStepTypeRegistry>().All.Should().HaveCount(4);
        host.Services.GetRequiredService<MachineCapability>().Should().NotBeNull();
        host.Services.GetRequiredService<HmiSettings>().UiRefreshHz.Should().BeInRange(5, 10);
    }

    [Fact]
    public async Task Background_services_start_and_stop_cleanly()
    {
        using IHost host = await BuildHostAsync("--gateway", "sim");

        await host.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        IMachineMonitor monitor = host.Services.GetRequiredService<IMachineMonitor>();
        monitor.Current.ConnectionState.Should().Be(GatewayConnectionState.Connected);
        monitor.Current.Values.Should().NotBeEmpty("后台取数应当已经发布过快照");

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_machine_that_cannot_be_reached_does_not_hold_up_the_hmi()
    {
        // 默认走 OPC UA，配置里的端点在这台机器上不存在（握手会一直等到超时）：
        // 界面必须立刻起来，把"连不上"当成报警显示，而不是让人对着空白屏等。
        using IHost host = await BuildHostAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await host.Invoking(h => h.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "连不上机床不得拖住启动——握手在后台做，界面先出来");

        await host.StopAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        this.workspace.Dispose();
    }
}
