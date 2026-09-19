using System;
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
        string samples = workspace.CreateSampleDirectory();

        IReadOnlyList<string> created = await ConfigBootstrapper.EnsureConfigurationAsync(
            options, samples, CancellationToken.None);

        created.Should().HaveCount(Directory.GetFiles(workspace.SampleDirectory, "*.sample.json").Length);
        File.Exists(options.MachineConfigFilePath).Should().BeTrue();
        File.Exists(options.TagMapFilePath).Should().BeTrue();
        File.Exists(Path.Combine(options.ConfigDirectory, "hmi.json")).Should().BeTrue();
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

/// <summary>
/// 升级后最常见的现场问题：配置是旧版本生成的，缺了新字段。
/// 报错必须说清楚是哪份文件、缺哪一项、怎么办。
/// </summary>
public sealed class StaleConfigurationTests
{
    [Fact]
    public async Task A_config_from_an_older_version_says_which_file_and_what_to_do()
    {
        using var workspace = new TempWorkspace();
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);
        Directory.CreateDirectory(options.ConfigDirectory);

        // 旧版本的 hmi.json：没有补偿增益这些后加的字段。
        await File.WriteAllTextAsync(
            Path.Combine(options.ConfigDirectory, "hmi.json"),
            """
            {
              "schemaVersion": 1,
              "culture": "zh-CN",
              "pollIntervalMs": 100,
              "uiRefreshHz": 8,
              "profileSampleCount": 101,
              "chartHistorySeconds": 300,
              "recordRetentionDays": 730,
              "alarmHistoryLimit": 500
            }
            """);

        Func<Task> load = () => JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);

        (await load.Should().ThrowAsync<RollGrinder.Contracts.GatewayException>())
            .WithMessage("*hmi.json*")
            .WithMessage("*CompensationGain*")
            .WithMessage("*hmi.sample.json*");
    }

    [Fact]
    public async Task A_machine_config_missing_a_field_points_at_its_template_too()
    {
        using var workspace = new TempWorkspace();
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);
        Directory.CreateDirectory(options.ConfigDirectory);

        await File.WriteAllTextAsync(options.MachineConfigFilePath, """{ "schemaVersion": 1 }""");

        Func<Task> load = () => new JsonMachineConfigProvider(options).GetMachineAsync(CancellationToken.None);

        (await load.Should().ThrowAsync<RollGrinder.Contracts.GatewayException>())
            .WithMessage("*machine.sample.json*");
    }
}
