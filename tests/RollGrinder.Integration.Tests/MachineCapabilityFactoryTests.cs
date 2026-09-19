using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Steps;
using Xunit;

namespace RollGrinder.Integration.Tests;

public sealed class MachineCapabilityFactoryTests
{
    private static async Task<MachineDescription> LoadSampleMachineAsync(TempWorkspace workspace)
    {
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);
        return await new JsonMachineConfigProvider(options).GetMachineAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Capability_is_taken_from_the_machine_description()
    {
        using var workspace = new TempWorkspace();
        MachineDescription machine = await LoadSampleMachineAsync(workspace);

        MachineCapability capability = MachineCapabilityFactory.Create(machine);

        capability.MaxInfeedPerPassRadiusMm.Should().Be(machine.Thresholds["maxInfeedPerPassRadiusMm"]);
        capability.MaxFeedMmPerMin.Should().Be(12000.0, "取自 Carriage 轴的 maxFeedMmPerMin");
        capability.MaxWorkpieceSpeedRpm.Should().Be(120.0);
        capability.MaxWheelSpeedRpm.Should().Be(1200.0);
        capability.MinRadiusMm.Should().Be(75.0, "最小直径 150 mm 的半径量");
        capability.MaxRadiusMm.Should().Be(650.0);
    }

    [Fact]
    public void A_missing_threshold_is_an_error_rather_than_a_guessed_default()
    {
        var machine = new MachineDescription(
            1,
            "RG-X",
            "no thresholds",
            new ControllerDescription("SinumerikOne", 1),
            new[] { new AxisDescription("Z", "Carriage", true, AxisClosedLoopKind.FullClosed, MaxFeedMmPerMin: 1000.0) },
            System.Array.Empty<MeasurementChannelDescription>(),
            new Dictionary<string, bool>(),
            new Dictionary<string, double>(),
            new WorkpieceLimits(100.0, 200.0, 100.0, 200.0, 1000.0),
            new Dictionary<string, int>());

        FluentActions.Invoking(() => MachineCapabilityFactory.Create(machine))
            .Should().Throw<GatewayException>();
    }

    [Fact]
    public void An_absent_axis_yields_no_capability_in_that_direction()
    {
        var machine = new MachineDescription(
            1,
            "RG-Y",
            "no wheel spindle",
            new ControllerDescription("SinumerikOne", 1),
            new[]
            {
                new AxisDescription("Z", "Carriage", true, AxisClosedLoopKind.FullClosed, MaxFeedMmPerMin: 1000.0),
                new AxisDescription("S", "WheelSpindle", false, AxisClosedLoopKind.OpenLoop, MaxSpeedRpm: 1200.0),
            },
            System.Array.Empty<MeasurementChannelDescription>(),
            new Dictionary<string, bool>(),
            new Dictionary<string, double> { ["maxInfeedPerPassRadiusMm"] = 0.05 },
            new WorkpieceLimits(100.0, 200.0, 100.0, 200.0, 1000.0),
            new Dictionary<string, int>());

        MachineCapability capability = MachineCapabilityFactory.Create(machine);

        capability.MaxWheelSpeedRpm.Should().Be(0.0, "机床没装这根轴时不得凭空给出能力");
    }
}
