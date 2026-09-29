using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.App.Controls;
using RollGrinder.App.Navigation;
using RollGrinder.Composition;
using RollGrinder.Contracts.Dtos;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 界面最终稿落到配置与纯逻辑上的几处：machine.json 的左栏 / 按钮板动作 / 往复余量与定位位置，
/// Ctrl+L 改语言只改 hmi.json 那一格，误差曲线超差段。
/// </summary>
public sealed class FinalDesignConfigTests
{
    private static async Task<(AppOptions Options, MachineDescription Machine)> LoadAsync(TempWorkspace workspace)
    {
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);
        return (options, await new JsonMachineConfigProvider(options).GetMachineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_sample_machine_carries_the_quick_bar_panel_actions_and_positions()
    {
        using var workspace = new TempWorkspace();
        (_, MachineDescription machine) = await LoadAsync(workspace);

        QuickBarCatalog.Resolve(machine.QuickBar, out var rejected).Select(entry => entry.Id)
            .Should().Equal(QuickBarCatalog.DefaultIds);
        rejected.Should().BeEmpty();
        machine.IsOnPanel(MachineDescription.PanelCycleStart).Should().BeTrue("样例机床的循环启动在按钮板上");
        machine.ManualStrokeMarginMm.Should().BeGreaterThan(0);
        foreach (string key in new[] { "positionX1AtRollMm", "positionX1HomeMm", "positionXSafeMm", "positioningFeedMmPerMin", "grindingCurrentLimitA" })
        {
            machine.Thresholds.Should().ContainKey(key, "手动磨削的定位 ▸ 与电流条要读它");
        }
    }

    [Fact]
    public async Task The_sample_configuration_validates_cleanly()
    {
        using var workspace = new TempWorkspace();
        (AppOptions options, _) = await LoadAsync(workspace);
        var store = new ConfigDocumentStore(options, TimeProvider.System);

        ConfigDocumentStore.Validate(ConfigFileKind.Machine, await store.LoadAsync(ConfigFileKind.Machine, CancellationToken.None))
            .Should().BeEmpty("固定位置可以是 0，不算「必须为正」");
        ConfigDocumentStore.Validate(ConfigFileKind.TagMap, await store.LoadAsync(ConfigFileKind.TagMap, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_zero_limit_is_still_refused_but_a_zero_position_is_not()
    {
        var document = new JsonObject
        {
            ["thresholds"] = new JsonObject
            {
                ["positionX1HomeMm"] = 0.0,
                ["positioningFeedMmPerMin"] = 0.0,
            },
        };

        ConfigDocumentStore.Validate(ConfigFileKind.Machine, document)
            .Where(issue => issue.Path.StartsWith("thresholds.", StringComparison.Ordinal))
            .Select(issue => issue.Path)
            .Should().Equal("thresholds.positioningFeedMmPerMin");
    }

    [Fact]
    public async Task Switching_the_language_only_changes_the_culture_in_hmi_json()
    {
        using var workspace = new TempWorkspace();
        (AppOptions options, _) = await LoadAsync(workspace);
        HmiSettings before = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);

        await JsonHmiSettingsProvider.SaveCultureAsync(options, "en-US", CancellationToken.None);

        HmiSettings after = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);
        after.Culture.Should().Be("en-US");
        after.Should().Be(before with { Culture = "en-US" });
        File.Exists(Path.Combine(options.ConfigDirectory, "hmi.json")).Should().BeTrue();
    }

    [Fact]
    public void Excess_runs_include_the_neighbouring_points_so_the_bold_line_starts_at_the_band()
    {
        double[] z = { 0, 1, 2, 3, 4, 5, 6, 7 };
        double[] e = { 0, 1, 6, 7, 1, 0, -6, -1 };

        CurveMath.ExcessRuns(z, e, 5.0).Should().Equal((1, 4), (5, 7));
        CurveMath.ExcessRuns(z, e, 10.0).Should().BeEmpty();
        CurveMath.ExcessRuns(new double[] { 0, 1 }, new double[] { 9, 9 }, 5.0).Should().Equal((0, 1));
    }

    [Fact]
    public void Excess_runs_refuse_mismatched_input()
    {
        Action mismatch = () => CurveMath.ExcessRuns(new double[] { 0, 1 }, new double[] { 0 }, 1.0);
        Action negative = () => CurveMath.ExcessRuns(new double[] { 0 }, new double[] { 0 }, -1.0);

        mismatch.Should().Throw<ArgumentException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }
}
