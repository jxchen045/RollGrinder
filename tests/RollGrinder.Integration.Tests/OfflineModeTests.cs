using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using RollGrinder.Data;
using RollGrinder.Services;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 离线模式：**根本没有机床**。办公室电脑上编程序、编辑辊形、查记录、打印。
/// 与打桩不同——打桩是"假装有台机床"，离线是明说没有。
/// </summary>
public sealed class OfflineModeTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    [Theory]
    [InlineData("--offline")]
    [InlineData("--gateway", "offline")]
    public void Both_spellings_select_offline(params string[] args)
    {
        AppOptions options = AppOptions.Parse(args, this.workspace.Root);

        options.Gateway.Should().Be(GatewayKind.Offline);
        options.IsOffline.Should().BeTrue();
        options.UseStub.Should().BeFalse("离线不是打桩");
    }

    [Fact]
    public void Every_other_gateway_is_not_offline()
    {
        foreach (string kind in new[] { "opcua", "stub", "sim" })
        {
            AppOptions.Parse(new[] { "--gateway", kind }, this.workspace.Root).IsOffline.Should().BeFalse(kind);
        }
    }

    [Fact]
    public async Task The_offline_gateway_never_reports_connected()
    {
        await using ServiceProvider services = await BuildAsync();
        var gateway = services.GetRequiredService<IMachineGateway>();

        await gateway.ConnectAsync(CancellationToken.None);

        gateway.ConnectionState.Should().Be(
            GatewayConnectionState.Disconnected, "离线就是没有机床，不假装连上了");
    }

    [Fact]
    public async Task The_offline_gateway_refuses_writes_and_says_why()
    {
        // 悄悄写进一个假机床里最糟：等接上真机床才发现某个页面一直在偷偷下发。
        await using ServiceProvider services = await BuildAsync();
        var gateway = services.GetRequiredService<IMachineGateway>();

        await gateway.Invoking(g => g.WriteTagAsync(
                "manual.coolant",
                new TagValue("manual.coolant", TagDataType.Boolean, true, DateTimeOffset.UnixEpoch),
                CancellationToken.None))
            .Should().ThrowAsync<GatewayException>().WithMessage("*offline*");

        await gateway.Invoking(g => g.WriteTagsAsync(
                Array.Empty<TagWrite>(), CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }

    [Fact]
    public async Task Reading_state_offline_gives_an_empty_snapshot_rather_than_throwing()
    {
        // 监控循环照常转，只是什么都读不到——界面显示"--"，不是一屏异常。
        await using ServiceProvider services = await BuildAsync();
        var gateway = services.GetRequiredService<IMachineGateway>();

        MachineStateSnapshot snapshot = await gateway.ReadStateAsync(
            new[] { MachineTagKeys.ChannelState }, CancellationToken.None);

        snapshot.GetNumberOrNull(MachineTagKeys.ChannelState).Should().BeNull();
        snapshot.ConnectionState.Should().Be(GatewayConnectionState.Disconnected);
    }

    [Fact]
    public async Task The_database_still_works_offline()
    {
        // 离线的全部意义就在这里：编程、辊形、记录、设置照常。
        await using ServiceProvider services = await BuildAsync();

        var profiles = services.GetRequiredService<IRollProfileRepository>();
        (await profiles.ListAsync(10, CancellationToken.None)).Should().BeEmpty();

        var programs = services.GetRequiredService<IProgramRepository>();
        (await programs.ListAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    private async Task<ServiceProvider> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--offline" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(
            options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);

        var database = new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName));
        await database.MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// 离线时哪几页开放，是一条**页面自己声明**的规则，不是壳层写死的名单。
///
/// App 是 WPF 目标，测试工程加载不了它，所以和分层守卫一样读程序集元数据：
/// 类型自己声明了 get_WorksOffline，就说明它覆写了默认的 false。
/// </summary>
public sealed class OfflinePageCapabilityTests
{
    [Fact]
    public void The_pages_that_only_touch_the_database_are_open_offline()
    {
        // 这张表是想清楚过的：改动它意味着某一页对机床的依赖变了。
        var expected = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["StepsViewModel"] = true,
            ["ProfileViewModel"] = true,
            ["RecordsViewModel"] = true,
            ["SettingsViewModel"] = true,
            ["AutoGrindingViewModel"] = false,
            ["ManualViewModel"] = false,
            ["DiagnosticsViewModel"] = false,
        };

        IReadOnlySet<string> overriding = TypesDeclaring("get_WorksOffline");

        foreach (KeyValuePair<string, bool> pair in expected)
        {
            overriding.Contains(pair.Key).Should().Be(
                pair.Value,
                $"{pair.Key} 离线{(pair.Value ? "应当" : "不应")}可用——可用的页面必须自己覆写 WorksOffline");
        }
    }

    /// <summary>App 程序集里，自己声明了某个方法的类型名集合。</summary>
    private static IReadOnlySet<string> TypesDeclaring(string methodName)
    {
        string appOutputRoot = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "bin");
        string? assemblyPath = Directory
            .EnumerateFiles(appOutputRoot, "RollGrinder.App.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        assemblyPath.Should().NotBeNull($"在 '{appOutputRoot}' 下未找到 RollGrinder.App.dll；请先 dotnet build");

        using FileStream stream = File.OpenRead(assemblyPath!);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                if (string.Equals(
                        metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name),
                        methodName,
                        StringComparison.Ordinal))
                {
                    names.Add(metadata.GetString(type.Name));
                    break;
                }
            }
        }

        return names;
    }
}
