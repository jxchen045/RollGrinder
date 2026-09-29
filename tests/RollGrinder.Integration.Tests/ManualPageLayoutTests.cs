using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.Services.Manual;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 手动动作页（界面最终稿 F2、5.4）：测量臂、尾架、头架拨盘、托瓦四个动作页 + 测量对中，每页不超过 8 个竖键；
/// 砂轮、冷却液、各轴归位、测砂轮直径、校测量臂在手动磨削页；头架正反转、回参考点在按钮板上。
/// 每个机构动作都有且只有一个位置。
/// </summary>
public sealed class ManualPageLayoutTests
{
    [Fact]
    public void There_are_four_action_pages_and_measure_centring_of_at_most_eight_actions()
    {
        ManualPageLayout.Pages.Should().HaveCount(5);
        ManualPageLayout.Pages.Select(page => page.Key).Should().Equal(
            "measuringArm", "tailstock", "driver", "steadyRest", ManualPageLayout.MeasureAndCentringKey);
        ManualPageLayout.Pages.Should().OnlyContain(page => page.ActionKeys.Count <= ManualPageLayout.MaxActionsPerPage);
    }

    [Fact]
    public void Every_mechanism_action_is_in_exactly_one_place()
    {
        // 唯一的例外是"校测量臂…"：最终稿里测量臂页（5.4）和手动磨削竖键第二页（5.1）都有它——
        // 同一个键、同一条确认，只是两处入口。
        string[] placed = ManualPageLayout.Pages.SelectMany(page => page.ActionKeys)
            .Concat(ManualPageLayout.GrindingPageKeys.Where(key => key != "arms.calibrate"))
            .Concat(ManualPageLayout.PanelOnlyKeys)
            .Where(key => !ManualCommandCatalog.Cycles.Any(cycle => cycle.Key == key))
            .ToArray();

        placed.Should().OnlyHaveUniqueItems();
        placed.Should().BeEquivalentTo(
            ManualCommandCatalog.MeasuringArm.Concat(ManualCommandCatalog.Tailstock).Concat(ManualCommandCatalog.Other)
                .Select(command => command.Key));
    }

    [Fact]
    public void Every_cycle_is_in_the_cycles_menu_or_on_the_panel()
    {
        ManualPageLayout.AuxiliaryKeys.Concat(ManualPageLayout.PanelOnlyKeys)
            .Should().Contain(ManualCommandCatalog.Cycles.Select(cycle => cycle.Key));
        ManualPageLayout.AuxiliaryKeys.Count.Should().BeLessThanOrEqualTo(ManualPageLayout.MaxActionsPerPage);
    }

    [Fact]
    public void The_headstock_direction_is_only_on_the_panel()
    {
        // 最终稿 F2：头架正反转在硬件按钮板上，屏幕上不再有这两个键（调速改成手动磨削页的给定）。
        ManualPageLayout.PanelOnlyKeys.Should().Contain(new[] { "headstock.forward", "headstock.reverse" });
        ManualPageLayout.Pages.SelectMany(page => page.ActionKeys).Concat(ManualPageLayout.GrindingPageKeys)
            .Should().NotContain(new[] { "headstock.forward", "headstock.reverse" });
    }

    [Fact]
    public void Every_listed_action_exists_in_the_catalogue()
    {
        string[] known = ManualCommandCatalog.All.Select(command => command.Key).ToArray();
        ManualPageLayout.Pages.SelectMany(page => page.ActionKeys)
            .Concat(ManualPageLayout.AuxiliaryKeys)
            .Concat(ManualPageLayout.GrindingPageKeys)
            .Concat(ManualPageLayout.PanelOnlyKeys)
            .Should().OnlyContain(key => known.Contains(key));
    }
}
