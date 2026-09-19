using System;
using System.IO;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Contracts.Dtos;
using Xunit;

namespace RollGrinder.Integration.Tests;

public sealed class AppOptionsTests
{
    private static readonly string BaseDirectory = Path.Combine(Path.GetTempPath(), "rollgrinder-base");

    [Fact]
    public void Defaults_to_config_and_data_next_to_the_executable()
    {
        AppOptions options = AppOptions.Parse(Array.Empty<string>(), BaseDirectory);

        options.ConfigDirectory.Should().Be(Path.Combine(BaseDirectory, "config"));
        options.DataDirectory.Should().Be(Path.Combine(BaseDirectory, "data"));
        options.LogDirectory.Should().Be(Path.Combine(BaseDirectory, "data", "logs"));
        options.MachineConfigFilePath.Should().Be(Path.Combine(BaseDirectory, "config", "machine.json"));
        options.TagMapFilePath.Should().Be(Path.Combine(BaseDirectory, "config", "tagmap.json"));
    }

    [Fact]
    public void Defaults_to_the_opcua_gateway()
    {
        AppOptions options = AppOptions.Parse(Array.Empty<string>(), BaseDirectory);

        options.Gateway.Should().Be(GatewayKind.OpcUa);
        options.UseStub.Should().BeFalse();
    }

    [Fact]
    public void Stub_switch_selects_the_stub_gateway()
    {
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, BaseDirectory);

        options.Gateway.Should().Be(GatewayKind.Stub);
        options.UseStub.Should().BeTrue();
    }

    [Theory]
    [InlineData("opcua", GatewayKind.OpcUa)]
    [InlineData("stub", GatewayKind.Stub)]
    [InlineData("FILE", GatewayKind.File)]
    public void Gateway_option_is_case_insensitive(string value, GatewayKind expected)
    {
        AppOptions.Parse(new[] { "--gateway", value }, BaseDirectory).Gateway.Should().Be(expected);
    }

    [Fact]
    public void Directories_can_be_overridden()
    {
        string config = Path.Combine(Path.GetTempPath(), "cfg");
        string data = Path.Combine(Path.GetTempPath(), "dat");

        AppOptions options = AppOptions.Parse(new[] { "--config", config, "--data", data }, BaseDirectory);

        options.ConfigDirectory.Should().Be(Path.GetFullPath(config));
        options.DataDirectory.Should().Be(Path.GetFullPath(data));
    }

    [Fact]
    public void Relative_directory_overrides_are_resolved_against_the_base_directory()
    {
        AppOptions options = AppOptions.Parse(new[] { "--config", "site-config" }, BaseDirectory);

        options.ConfigDirectory.Should().Be(Path.Combine(BaseDirectory, "site-config"));
    }

    [Fact]
    public void Unknown_gateway_is_rejected()
    {
        Action parse = () => AppOptions.Parse(new[] { "--gateway", "modbus" }, BaseDirectory);

        parse.Should().Throw<ArgumentException>().WithMessage("*modbus*");
    }

    [Fact]
    public void Missing_option_value_is_rejected()
    {
        Action parse = () => AppOptions.Parse(new[] { "--config" }, BaseDirectory);

        parse.Should().Throw<ArgumentException>().WithMessage("*--config*");
    }

    [Fact]
    public void Unknown_arguments_are_ignored()
    {
        Action parse = () => AppOptions.Parse(new[] { "-embedding", "--stub" }, BaseDirectory);

        parse.Should().NotThrow();
    }
}
