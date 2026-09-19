using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 仿真网关：按下发参数"动起来"，用于无机床联调。
/// 走公开的装配入口，不直接 new 内部类型。
/// </summary>
public sealed class SimulationGatewayTests
{
    private static async Task<(IMachineGateway Gateway, MachineDescription Machine)> BuildAsync(TempWorkspace workspace)
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);

        var provider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await provider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await provider.GetTagMapAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        ServiceProvider built = services.BuildServiceProvider();
        return (built.GetRequiredService<IMachineGateway>(), machine);
    }

    private static TagValue Number(string key, double value) =>
        new(key, TagDataType.Double, value, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Sim_gateway_refuses_reads_before_connecting()
    {
        using var workspace = new TempWorkspace();
        (IMachineGateway gateway, _) = await BuildAsync(workspace);

        await gateway.Invoking(g => g.ReadTagAsync(MachineTagKeys.ChannelState, CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }

    [Fact]
    public async Task Sim_gateway_stays_idle_until_parameters_are_marked_valid()
    {
        using var workspace = new TempWorkspace();
        (IMachineGateway gateway, MachineDescription machine) = await BuildAsync(workspace);
        await gateway.ConnectAsync(CancellationToken.None);

        MachineStateSnapshot snapshot = await gateway.ReadStateAsync(
            MachineTagKeys.MonitoringKeys(machine), CancellationToken.None);

        snapshot.GetNumberOrNull(MachineTagKeys.ChannelState).Should().Be((double)(int)NcChannelState.Reset);
    }

    [Fact]
    public async Task Parameter_handover_starts_the_simulated_program()
    {
        using var workspace = new TempWorkspace();
        (IMachineGateway gateway, MachineDescription machine) = await BuildAsync(workspace);
        await gateway.ConnectAsync(CancellationToken.None);

        await gateway.WriteTagsAsync(
            new[]
            {
                new TagWrite(MachineTagKeys.JobRollRadiusMm, Number(MachineTagKeys.JobRollRadiusMm, 325.0)),
                new TagWrite(MachineTagKeys.JobBodyLengthMm, Number(MachineTagKeys.JobBodyLengthMm, 2000.0)),
                new TagWrite(MachineTagKeys.JobFeedMmPerMin, Number(MachineTagKeys.JobFeedMmPerMin, 2000.0)),
                new TagWrite(
                    MachineTagKeys.JobParametersValid,
                    new TagValue(MachineTagKeys.JobParametersValid, TagDataType.Boolean, true, DateTimeOffset.UtcNow)),
            },
            CancellationToken.None);

        MachineStateSnapshot snapshot = await gateway.ReadStateAsync(
            MachineTagKeys.MonitoringKeys(machine), CancellationToken.None);

        snapshot.GetNumberOrNull(MachineTagKeys.ChannelState).Should().Be((double)(int)NcChannelState.Running);
        snapshot.GetTextOrNull(MachineTagKeys.ProgramName).Should().NotBeNullOrEmpty();

        // 程序刚起步，实测直径应为目标直径加上待磨余量。
        double? measuredDiameterMm = snapshot.GetNumberOrNull(MachineTagKeys.MeasuredDiameterMm);
        measuredDiameterMm.Should().NotBeNull();
        measuredDiameterMm!.Value.Should().BeGreaterThan(UnitConversion.RadiusMmToDiameterMm(325.0));
    }

    [Fact]
    public async Task Unmapped_monitoring_keys_are_skipped_rather_than_failing_the_poll()
    {
        using var workspace = new TempWorkspace();
        (IMachineGateway gateway, _) = await BuildAsync(workspace);
        await gateway.ConnectAsync(CancellationToken.None);

        MachineStateSnapshot snapshot = await gateway.ReadStateAsync(
            new List<string> { MachineTagKeys.ChannelState, "axis.Q.actualPositionMm" },
            CancellationToken.None);

        snapshot.Values.Should().ContainSingle(value => value.Key == MachineTagKeys.ChannelState);
        snapshot.TryGet("axis.Q.actualPositionMm", out _).Should().BeFalse();
    }
}
