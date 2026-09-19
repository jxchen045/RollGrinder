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
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 文件网关：按录制文件的时间轴回放，写入不回灌到回放流，但会被记下来。
/// </summary>
public sealed class FileGatewayTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    private async Task<(IMachineGateway Gateway, MachineDescription Machine, string ReplayPath)> BuildAsync(
        string? content = null)
    {
        string replayDirectory = Path.Combine(this.workspace.Root, "data", "replay");
        Directory.CreateDirectory(replayDirectory);
        string replayPath = Path.Combine(replayDirectory, "session.jsonl");
        await File.WriteAllTextAsync(
            replayPath,
            content ?? await File.ReadAllTextAsync(Path.Combine(RepositoryLayout.Root, "deploy", "replay-sample.jsonl")));

        AppOptions options = AppOptions.Parse(new[] { "--gateway", "file" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        var provider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await provider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await provider.GetTagMapAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        return (services.BuildServiceProvider().GetRequiredService<IMachineGateway>(), machine, replayPath);
    }

    [Fact]
    public async Task Reading_before_connecting_is_refused()
    {
        (IMachineGateway gateway, _, _) = await BuildAsync();

        await gateway.Invoking(g => g.ReadTagAsync(MachineTagKeys.ChannelState, CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }

    [Fact]
    public async Task The_first_frame_is_served_right_after_connecting()
    {
        (IMachineGateway gateway, MachineDescription machine, _) = await BuildAsync();
        await gateway.ConnectAsync(CancellationToken.None);

        MachineStateSnapshot snapshot = await gateway.ReadStateAsync(
            MachineTagKeys.MonitoringKeys(machine), CancellationToken.None);

        snapshot.GetNumberOrNull(MachineTagKeys.ChannelState).Should().Be((double)(int)NcChannelState.Reset);
        snapshot.GetNumberOrNull(MachineTagKeys.MeasuredDiameterMm).Should().BeApproximately(650.6, 1e-9);
    }

    [Fact]
    public async Task Values_not_repeated_in_a_frame_are_carried_forward()
    {
        // 第二帧只写了通道状态与程序名，位置应沿用第一帧。
        string content = string.Join(Environment.NewLine, new[]
        {
            """{"offsetMs":0,"values":{"machine.channelState":0,"axis.Z.actualPositionMm":12.5}}""",
            """{"offsetMs":1,"values":{"machine.channelState":2}}""",
        });

        (IMachineGateway gateway, MachineDescription machine, _) = await BuildAsync(content);
        await gateway.ConnectAsync(CancellationToken.None);
        await Task.Delay(30);

        MachineStateSnapshot snapshot = await gateway.ReadStateAsync(
            MachineTagKeys.MonitoringKeys(machine), CancellationToken.None);

        snapshot.GetNumberOrNull(MachineTagKeys.ChannelState).Should().Be(2.0);
        snapshot.GetNumberOrNull(MachineTagKeys.AxisActualPositionMm("Z")).Should().Be(12.5);
    }

    [Fact]
    public async Task A_tag_the_recording_never_mentions_is_marked_untrustworthy()
    {
        (IMachineGateway gateway, _, _) = await BuildAsync(
            """{"offsetMs":0,"values":{"machine.channelState":0}}""");
        await gateway.ConnectAsync(CancellationToken.None);

        TagValue value = await gateway.ReadTagAsync(MachineTagKeys.MeasuredDiameterMm, CancellationToken.None);

        value.IsGood.Should().BeFalse("回放里没有的量不能装作 0");
    }

    [Fact]
    public async Task Writes_are_visible_to_reads_and_appended_to_a_write_log()
    {
        (IMachineGateway gateway, _, string replayPath) = await BuildAsync();
        await gateway.ConnectAsync(CancellationToken.None);

        await gateway.WriteTagAsync(
            MachineTagKeys.JobRollRadiusMm,
            new TagValue(MachineTagKeys.JobRollRadiusMm, TagDataType.Double, 325.0, DateTimeOffset.UtcNow),
            CancellationToken.None);

        TagValue read = await gateway.ReadTagAsync(MachineTagKeys.JobRollRadiusMm, CancellationToken.None);
        read.Raw.Should().Be(325.0);

        string[] writeLogs = Directory.GetFiles(Path.GetDirectoryName(replayPath)!, "writes-*.jsonl");
        writeLogs.Should().ContainSingle();
        (await File.ReadAllTextAsync(writeLogs[0])).Should().Contain(MachineTagKeys.JobRollRadiusMm);
    }

    [Fact]
    public async Task Writing_a_read_only_tag_is_refused()
    {
        (IMachineGateway gateway, _, _) = await BuildAsync();
        await gateway.ConnectAsync(CancellationToken.None);

        await gateway.Invoking(g => g.WriteTagAsync(
                MachineTagKeys.ChannelState,
                new TagValue(MachineTagKeys.ChannelState, TagDataType.Int32, 2, DateTimeOffset.UtcNow),
                CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }

    [Fact]
    public async Task A_malformed_recording_is_reported_with_its_line_number()
    {
        (IMachineGateway gateway, _, _) = await BuildAsync("{ this is not json }");

        (await gateway.Invoking(g => g.ConnectAsync(CancellationToken.None))
            .Should().ThrowAsync<GatewayException>())
            .WithMessage("*line 1*");
    }

    [Fact]
    public async Task An_empty_recording_is_refused()
    {
        (IMachineGateway gateway, _, _) = await BuildAsync(string.Empty);

        await gateway.Invoking(g => g.ConnectAsync(CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }

    [Fact]
    public void The_replay_switch_selects_the_file_gateway()
    {
        AppOptions options = AppOptions.Parse(
            new[] { "--replay", "recordings/session.jsonl" }, this.workspace.Root);

        options.Gateway.Should().Be(GatewayKind.File);
        options.ReplayFilePath.Should().Be(Path.Combine(this.workspace.Root, "recordings", "session.jsonl"));
    }

    [Fact]
    public async Task A_missing_replay_file_is_reported_rather_than_silently_ignored()
    {
        AppOptions options = AppOptions.Parse(
            new[] { "--replay", "nope.jsonl" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        var provider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await provider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await provider.GetTagMapAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        IMachineGateway gateway = services.BuildServiceProvider().GetRequiredService<IMachineGateway>();

        await gateway.Invoking(g => g.ConnectAsync(CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
    }

    public void Dispose() => this.workspace.Dispose();
}
