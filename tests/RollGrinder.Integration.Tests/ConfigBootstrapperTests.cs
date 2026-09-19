using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Composition;
using Xunit;

namespace RollGrinder.Integration.Tests;

public sealed class ConfigBootstrapperTests
{
    [Fact]
    public async Task First_start_creates_config_and_data_directories()
    {
        using var workspace = new TempWorkspace();
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);

        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);

        Directory.Exists(options.ConfigDirectory).Should().BeTrue();
        Directory.Exists(options.DataDirectory).Should().BeTrue();
        Directory.Exists(options.LogDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task First_start_copies_every_sample_file()
    {
        using var workspace = new TempWorkspace();
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);

        IReadOnlyList<string> created = await ConfigBootstrapper.EnsureConfigurationAsync(
            options, workspace.CreateSampleDirectory(), CancellationToken.None);

        created.Should().HaveCount(2);
        File.Exists(options.MachineConfigFilePath).Should().BeTrue();
        File.Exists(options.TagMapFilePath).Should().BeTrue();
    }

    [Fact]
    public async Task Existing_configuration_is_never_overwritten()
    {
        using var workspace = new TempWorkspace();
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);
        string samples = workspace.CreateSampleDirectory();

        await ConfigBootstrapper.EnsureConfigurationAsync(options, samples, CancellationToken.None);
        await File.WriteAllTextAsync(options.MachineConfigFilePath, "{ \"siteEdited\": true }");

        IReadOnlyList<string> createdOnSecondStart = await ConfigBootstrapper.EnsureConfigurationAsync(
            options, samples, CancellationToken.None);

        createdOnSecondStart.Should().BeEmpty("升级或再次启动都不得覆盖现场配置");
        (await File.ReadAllTextAsync(options.MachineConfigFilePath)).Should().Contain("siteEdited");
    }
}
