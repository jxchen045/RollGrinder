using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.Services.Manual;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>手动页分 6 页（修改稿 4、5.6）：每页不超过 8 个动作，每个机构动作都有且只有一个位置。</summary>
public sealed class ManualPageLayoutTests
{
    [Fact]
    public void There_are_six_pages_of_at_most_eight_actions()
    {
        ManualPageLayout.Pages.Should().HaveCount(6);
        ManualPageLayout.Pages.Should().OnlyContain(page => page.ActionKeys.Count <= ManualPageLayout.MaxActionsPerPage);
        ManualPageLayout.Pages.Select(page => page.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_mechanism_action_is_on_exactly_one_page_or_in_the_cycles_menu()
    {
        string[] placed = ManualPageLayout.Pages.SelectMany(page => page.ActionKeys)
            .Where(key => !ManualCommandCatalog.Cycles.Any(cycle => cycle.Key == key))
            .Concat(ManualPageLayout.AuxiliaryKeys.Where(key => !ManualCommandCatalog.Cycles.Any(cycle => cycle.Key == key)))
            .ToArray();

        placed.Should().OnlyHaveUniqueItems();
        placed.Should().BeEquivalentTo(
            ManualCommandCatalog.MeasuringArm.Concat(ManualCommandCatalog.Tailstock).Concat(ManualCommandCatalog.Other)
                .Select(command => command.Key));
    }

    [Fact]
    public void Every_cycle_is_in_the_cycles_menu_and_the_menu_fits_beside_hmi_reset()
    {
        ManualPageLayout.AuxiliaryKeys.Should().Contain(ManualCommandCatalog.Cycles.Select(cycle => cycle.Key));
        ManualPageLayout.AuxiliaryKeys.Count.Should().BeLessThanOrEqualTo(6, "子菜单 7 格，还要放一个 HMI 复位");
    }

    [Fact]
    public void Every_listed_action_exists_in_the_catalogue()
    {
        string[] known = ManualCommandCatalog.All.Select(command => command.Key).ToArray();
        ManualPageLayout.Pages.SelectMany(page => page.ActionKeys).Concat(ManualPageLayout.AuxiliaryKeys)
            .Should().OnlyContain(key => known.Contains(key));
    }
}
