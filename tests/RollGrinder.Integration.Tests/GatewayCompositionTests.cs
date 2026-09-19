using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Device;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 组合根按配置装配网关实现，业务代码只见 <see cref="IMachineGateway"/>。
/// </summary>
public sealed class GatewayCompositionTests
{
    private static async Task<ServiceProvider> BuildAsync(TempWorkspace workspace, params string[] args)
    {
        AppOptions options = AppOptions.Parse(args, workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);

        var provider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await provider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await provider.GetTagMapAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Stub_switch_yields_the_stub_gateway()
    {
        using var workspace = new TempWorkspace();
        await using ServiceProvider services = await BuildAsync(workspace, "--stub");

        services.GetRequiredService<IMachineGateway>().Should().BeOfType<StubGateway>();
    }

    [Fact]
    public async Task Default_configuration_yields_the_opcua_gateway()
    {
        using var workspace = new TempWorkspace();
        await using ServiceProvider services = await BuildAsync(workspace);

        services.GetRequiredService<IMachineGateway>().Should().BeOfType<OpcUaGateway>();
    }

    [Fact]
    public async Task File_gateway_can_be_selected()
    {
        using var workspace = new TempWorkspace();
        await using ServiceProvider services = await BuildAsync(workspace, "--gateway", "file");

        services.GetRequiredService<IMachineGateway>().Should().BeOfType<FileGateway>();
    }

    [Fact]
    public async Task Gateway_is_a_singleton()
    {
        using var workspace = new TempWorkspace();
        await using ServiceProvider services = await BuildAsync(workspace, "--stub");

        services.GetRequiredService<IMachineGateway>()
            .Should().BeSameAs(services.GetRequiredService<IMachineGateway>());
    }

    [Fact]
    public async Task Stub_gateway_round_trips_a_written_tag()
    {
        using var workspace = new TempWorkspace();
        await using ServiceProvider services = await BuildAsync(workspace, "--stub");
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();

        await gateway.ConnectAsync(CancellationToken.None);
        gateway.ConnectionState.Should().Be(GatewayConnectionState.Connected);

        var written = new TagValue("job.rollRadiusMm", TagDataType.Double, 325.5, DateTimeOffset.UtcNow);
        await gateway.WriteTagAsync("job.rollRadiusMm", written, CancellationToken.None);

        TagValue read = await gateway.ReadTagAsync("job.rollRadiusMm", CancellationToken.None);
        read.Raw.Should().Be(325.5);
    }

    [Fact]
    public async Task Stub_gateway_refuses_to_write_a_read_only_tag()
    {
        using var workspace = new TempWorkspace();
        await using ServiceProvider services = await BuildAsync(workspace, "--stub");
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();

        var value = new TagValue("machine.channelState", TagDataType.Int32, 3, DateTimeOffset.UtcNow);

        await gateway.Invoking(g => g.WriteTagAsync("machine.channelState", value, CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }

    [Fact]
    public async Task Unmapped_tag_raises_a_gateway_exception()
    {
        using var workspace = new TempWorkspace();
        await using ServiceProvider services = await BuildAsync(workspace, "--stub");
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();

        await gateway.Invoking(g => g.ReadTagAsync("nope", CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }
}
