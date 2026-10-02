using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.App.Navigation;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 8 个区域与左栏（界面最终稿 D1、D2、4.1）：
/// 区域菜单正好 8 格，F(n) = 第 n 个区域；每个画面恰好属于一个区域；
/// 机床区的入口随 NC 方式；调试只在区域菜单里。
/// </summary>
public sealed class AreaCatalogTests
{
    [Fact]
    public void The_area_menu_lists_all_eight_areas_once_in_the_final_order()
    {
        AreaCatalog.MenuOrder.Should().Equal(
            AreaKey.Machine, AreaKey.Rolls, AreaKey.Profile, AreaKey.Steps,
            AreaKey.Parameters, AreaKey.Records, AreaKey.Diagnostics, AreaKey.Commissioning);
        AreaCatalog.MenuOrder.Should().BeEquivalentTo(Enum.GetValues<AreaKey>());
        AreaCatalog.MenuOrder.Count.Should().Be(AreaCatalog.MenuSlotCount);
    }

    [Fact]
    public void Every_page_belongs_to_exactly_one_area_and_every_area_has_a_page()
    {
        foreach (PageKey page in Enum.GetValues<PageKey>())
        {
            AreaKey area = AreaCatalog.AreaOf(page);
            AreaCatalog.PagesOf(area).Should().Contain(page);
        }

        foreach (AreaKey area in Enum.GetValues<AreaKey>())
        {
            AreaCatalog.PagesOf(area).Should().NotBeEmpty();
            AreaCatalog.AreaOf(AreaCatalog.EntryPage(area, MachineMode.Jog)).Should().Be(area);
            AreaCatalog.AreaOf(AreaCatalog.EntryPage(area, MachineMode.Auto)).Should().Be(area);
        }
    }

    [Fact]
    public void The_machine_area_holds_the_two_base_screens_the_action_pages_and_the_job_wizard()
    {
        AreaCatalog.PagesOf(AreaKey.Machine).Should().Equal(
            PageKey.ManualGrinding, PageKey.AutoGrinding, PageKey.Manual, PageKey.Job);
    }

    [Theory]
    [InlineData(MachineMode.Jog, PageKey.ManualGrinding)]
    [InlineData(MachineMode.Mda, PageKey.ManualGrinding)]
    [InlineData(MachineMode.Unknown, PageKey.ManualGrinding)]
    [InlineData(MachineMode.Auto, PageKey.AutoGrinding)]
    public void The_machine_area_opens_on_the_screen_of_the_nc_mode(MachineMode mode, PageKey expected)
    {
        AreaCatalog.EntryPage(AreaKey.Machine, mode).Should().Be(expected);
    }

    [Fact]
    public void Only_the_two_base_screens_follow_the_mode()
    {
        Enum.GetValues<PageKey>().Where(AreaCatalog.IsModeBasePage)
            .Should().BeEquivalentTo(new[] { PageKey.AutoGrinding, PageKey.ManualGrinding });
    }

    [Fact]
    public void Slot_number_is_the_function_key_number_and_exactly_the_current_area_is_marked()
    {
        IReadOnlyList<AreaSoftKey> keys = AreaCatalog.BuildMenu(AreaCatalog.MenuOrder, AreaKey.Records, _ => true);

        keys.Select(k => k.SlotNumber).Should().Equal(Enumerable.Range(1, 8));
        keys.Where(k => k.IsCurrent).Select(k => k.Area).Should().Equal(AreaKey.Records);
    }

    [Fact]
    public void Unavailable_areas_stay_listed_in_place()
    {
        var closed = new HashSet<AreaKey> { AreaKey.Machine, AreaKey.Commissioning };

        IReadOnlyList<AreaSoftKey> keys = AreaCatalog.BuildMenu(AreaCatalog.MenuOrder, AreaKey.Steps, a => !closed.Contains(a));

        keys.Should().HaveCount(8, "进不去的区域照样占着自己的格子，键位不跳动");
        keys.Where(k => !k.IsAvailable).Select(k => k.Area).Should().BeEquivalentTo(closed);
    }

    [Fact]
    public void A_ninth_area_is_refused()
    {
        AreaKey[] tooMany = Enumerable.Repeat(AreaKey.Records, 9).ToArray();

        FluentActions.Invoking(() => AreaCatalog.BuildMenu(tooMany, AreaKey.Records, _ => true))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_default_quick_bar_is_the_final_seven_entries()
    {
        IReadOnlyList<QuickBarEntry> entries = QuickBarCatalog.Resolve(null, out IReadOnlyList<string> rejected);

        entries.Select(e => e.Id).Should().Equal("machine", "rolls", "profile", "steps", "wheel", "records", "diagnostics");
        entries.Single(e => e.Id == "wheel").Should().Match<QuickBarEntry>(
            e => e.Area == AreaKey.Parameters && e.GroupKey == QuickBarCatalog.WheelGroup);
        rejected.Should().BeEmpty();
    }

    [Fact]
    public void The_quick_bar_follows_the_configuration_and_refuses_what_does_not_fit()
    {
        IReadOnlyList<QuickBarEntry> entries = QuickBarCatalog.Resolve(
            new[] { "records", "Machine", "records", "commissioning", "nonsense", "profile", "steps", "wheel", "library", "parameters", "diagnostics" },
            out IReadOnlyList<string> rejected);

        // 旧配置里的 "library" 按 "rolls" 认（库区拆开后左栏那一格给轧辊）。
        entries.Select(e => e.Id).Should().Equal("records", "machine", "profile", "steps", "wheel", "rolls", "parameters");
        rejected.Should().Equal("records", "commissioning", "nonsense", "diagnostics");
    }

    [Fact]
    public void Commissioning_is_menu_only()
    {
        AreaCatalog.IsMenuOnly(AreaKey.Commissioning).Should().BeTrue();
        QuickBarCatalog.Known.Should().NotContain(e => AreaCatalog.IsMenuOnly(e.Area));
        QuickBarCatalog.Known.Select(e => e.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_configuration_with_nothing_usable_falls_back_to_the_default()
    {
        QuickBarCatalog.Resolve(new[] { "nonsense" }, out IReadOnlyList<string> rejected)
            .Select(e => e.Id).Should().Equal(QuickBarCatalog.DefaultIds);
        rejected.Should().Equal("nonsense");
    }
}
