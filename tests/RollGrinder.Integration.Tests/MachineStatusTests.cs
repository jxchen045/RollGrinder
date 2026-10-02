using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Monitoring;
using RollGrinder.Sim;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 机构状态指示（修改稿 3③ 状态带、5.6 手动页到位显示、问题 Q7）：
/// 读不到就是读不到，不当成"关"；仿真机床按手动动作把到位状态置上。
/// </summary>
public sealed class MachineStatusTests
{
    private static MachineStateSnapshot Snapshot(params (string Key, object? Raw)[] values) => new(
        DateTimeOffset.UnixEpoch,
        GatewayConnectionState.Connected,
        values.Select(v => new TagValue(v.Key, TagDataType.Boolean, v.Raw, DateTimeOffset.UnixEpoch, v.Raw is not null)).ToArray());

    [Fact]
    public void The_status_keys_in_contracts_match_the_catalogue()
    {
        // Contracts 不引用 Services，状态位是手抄的——这里盯着两边别走散。
        MachineTagKeys.StatusIndicatorKeys.Should().BeEquivalentTo(
            MachineStatusCatalog.All.Select(indicator => indicator.TagKey)
                .Where(key => key.StartsWith(MachineTagKeys.StatusPrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Every_indicator_is_monitored_and_mapped_in_the_sample_tag_map()
    {
        string path = Path.Combine(RepositoryLayout.Root, "config", "tagmap.sample.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        HashSet<string> mapped = document.RootElement.GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetProperty("key").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        using var workspace = new TempWorkspace();
        IReadOnlyList<string> monitored = MachineTagKeys.MonitoringKeys(await LoadSampleMachineAsync(workspace));

        foreach (StatusIndicator indicator in MachineStatusCatalog.All)
        {
            mapped.Should().Contain(indicator.TagKey);
            monitored.Should().Contain(indicator.TagKey);
        }

        mapped.Should().Contain(MachineTagKeys.OperatingMode);
        monitored.Should().Contain(MachineTagKeys.OperatingMode);
    }

    [Fact]
    public void Fault_and_moving_bits_override_on_and_off_and_the_five_groups_cover_every_indicator()
    {
        StatusIndicator tailstock = MachineStatusCatalog.Tailstock;
        tailstock.Read(Snapshot((tailstock.TagKey, true), (tailstock.MovingTagKey, true))).Should().Be(IndicatorState.Moving);
        tailstock.Read(Snapshot((tailstock.TagKey, true), (tailstock.MovingTagKey, true), (tailstock.FaultTagKey, true)))
            .Should().Be(IndicatorState.Fault, "故障盖过一切");
        tailstock.Read(Snapshot((tailstock.TagKey, false), (tailstock.FaultTagKey, false))).Should().Be(IndicatorState.Off);
        StatusIndicator.Combine(new[] { IndicatorState.On, IndicatorState.Moving }).Should().Be(IndicatorState.Moving);

        MachineStatusCatalog.Groups.Should().HaveCount(5);
        MachineStatusCatalog.Groups.SelectMany(group => group.Indicators)
            .Should().BeSubsetOf(MachineStatusCatalog.All).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void An_unmapped_or_bad_status_is_unknown_not_off()
    {
        StatusIndicator quill = MachineStatusCatalog.Quill;
        quill.Read(Snapshot()).Should().Be(IndicatorState.Unknown);
        quill.Read(Snapshot((quill.TagKey, null))).Should().Be(IndicatorState.Unknown);
        quill.Read(Snapshot((quill.TagKey, false))).Should().Be(IndicatorState.Off);
        quill.Read(Snapshot((quill.TagKey, true))).Should().Be(IndicatorState.On);
        quill.Read(Snapshot((quill.TagKey, 1))).Should().Be(IndicatorState.On, "PLC 位映成整数也认");

        var disconnected = Snapshot((quill.TagKey, true)) with { ConnectionState = GatewayConnectionState.Disconnected };
        quill.Read(disconnected).Should().Be(IndicatorState.Unknown, "断线时的旧值不能当真");
    }

    [Theory]
    [InlineData(IndicatorState.On, IndicatorState.Unknown, IndicatorState.On)]
    [InlineData(IndicatorState.Off, IndicatorState.Off, IndicatorState.Off)]
    [InlineData(IndicatorState.Off, IndicatorState.Unknown, IndicatorState.Off)]
    [InlineData(IndicatorState.Unknown, IndicatorState.Unknown, IndicatorState.Unknown)]
    public void Combined_lamps_are_on_when_any_part_is_on(IndicatorState a, IndicatorState b, IndicatorState expected)
    {
        StatusIndicator.Combine(new[] { a, b }).Should().Be(expected);
    }

    [Fact]
    public async Task The_simulator_moves_the_status_bits_with_the_manual_pulses()
    {
        using var workspace = new TempWorkspace();
        var sim = new SimulatedMachine(await LoadSampleMachineAsync(workspace));

        sim.Read(MachineTagKeys.Status("quill.extended")).Should().Be(true, "开机时辊子装着，套筒伸出");
        sim.Read(MachineTagKeys.OperatingMode).Should().Be(0, "空闲时报 JOG");

        Pulse(sim, "quill.retract");
        sim.Read(MachineTagKeys.Status("quill.extended")).Should().Be(false);

        Pulse(sim, "arms.toRoll");
        sim.Read(MachineTagKeys.Status("outerArm.lowered")).Should().Be(true);
        sim.Read(MachineTagKeys.Status("innerArm.lowered")).Should().Be(true);
        Pulse(sim, "arms.home");
        sim.Read(MachineTagKeys.Status("outerArm.lowered")).Should().Be(false);
    }

    private static async Task<MachineDescription> LoadSampleMachineAsync(TempWorkspace workspace)
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);
        return await new JsonMachineConfigProvider(options).GetMachineAsync(CancellationToken.None);
    }

    private static void Pulse(SimulatedMachine sim, string action)
    {
        string key = MachineTagKeys.ManualCommand(action);
        sim.Write(key, new TagValue(key, TagDataType.Boolean, true, DateTimeOffset.UtcNow));
        sim.Write(key, new TagValue(key, TagDataType.Boolean, false, DateTimeOffset.UtcNow));
    }
}
