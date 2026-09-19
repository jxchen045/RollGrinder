using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using Xunit;

namespace RollGrinder.Integration.Tests;

public sealed class MachineConfigProviderTests
{
    private static async Task<(AppOptions Options, JsonMachineConfigProvider Provider)> PrepareAsync(TempWorkspace workspace)
    {
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);
        return (options, new JsonMachineConfigProvider(options));
    }

    [Fact]
    public async Task Sample_machine_description_is_loaded()
    {
        using var workspace = new TempWorkspace();
        (AppOptions _, JsonMachineConfigProvider provider) = await PrepareAsync(workspace);

        MachineDescription machine = await provider.GetMachineAsync(CancellationToken.None);

        machine.SchemaVersion.Should().Be(1);
        machine.MachineId.Should().NotBeNullOrWhiteSpace();
        machine.Controller.Kind.Should().Be("SinumerikOne");
        machine.Axes.Should().NotBeEmpty();
        machine.Axes.Should().Contain(axis => axis.Name == "X" && axis.IsPresent);
        machine.Axes.Should().Contain(axis => !axis.IsPresent, "机床差异由配置描述，缺装的轴也要能描述出来");
        machine.Thresholds.Should().ContainKey("maxInfeedPerPassRadiusMm");
        machine.Workpiece.MaxBodyLengthMm.Should().BeGreaterThan(machine.Workpiece.MinBodyLengthMm);
    }

    [Fact]
    public async Task Sample_tag_map_is_loaded_and_resolvable()
    {
        using var workspace = new TempWorkspace();
        (AppOptions _, JsonMachineConfigProvider provider) = await PrepareAsync(workspace);

        ITagMap tagMap = await provider.GetTagMapAsync(CancellationToken.None);

        tagMap.Tags.Should().NotBeEmpty();
        TagDescriptor infeed = tagMap.Resolve("job.rollRadiusMm");
        infeed.DataType.Should().Be(TagDataType.Double);
        infeed.Access.Should().Be(TagAccess.ReadWrite);
        infeed.Address.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Unknown_tag_raises_a_gateway_exception()
    {
        using var workspace = new TempWorkspace();
        (AppOptions _, JsonMachineConfigProvider provider) = await PrepareAsync(workspace);
        ITagMap tagMap = await provider.GetTagMapAsync(CancellationToken.None);

        tagMap.Invoking(map => map.Resolve("does.not.exist"))
            .Should().Throw<GatewayException>();
    }

    [Fact]
    public async Task Missing_configuration_file_raises_a_gateway_exception()
    {
        using var workspace = new TempWorkspace();
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);
        Directory.CreateDirectory(options.ConfigDirectory);
        var provider = new JsonMachineConfigProvider(options);

        await provider.Invoking(p => p.GetMachineAsync(CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }

    [Fact]
    public async Task Malformed_configuration_file_raises_a_gateway_exception()
    {
        using var workspace = new TempWorkspace();
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);
        Directory.CreateDirectory(options.ConfigDirectory);
        await File.WriteAllTextAsync(options.MachineConfigFilePath, "{ not json");
        var provider = new JsonMachineConfigProvider(options);

        await provider.Invoking(p => p.GetMachineAsync(CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }
}
