using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.App.Navigation;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 页面菜单 = 底部软键条原地换成区域键。守两条约定：
/// 第 n 格 = F(n) = Ctrl+n；区域最多 7 个，第 8 格永远留给导航槽（取消）。
/// </summary>
public sealed class AreaMenuLayoutTests
{
    [Fact]
    public void Every_page_is_in_the_menu_exactly_once()
    {
        AreaMenuLayout.DefaultOrder.Should().OnlyHaveUniqueItems();
        AreaMenuLayout.DefaultOrder.Should().BeEquivalentTo(Enum.GetValues<PageKey>(),
            "新增页面必须排进菜单，否则只能靠任务跳转进去，等于没有入口");
    }

    [Fact]
    public void All_areas_fit_beside_the_navigation_key()
    {
        AreaMenuLayout.DefaultOrder.Count.Should().BeLessThanOrEqualTo(AreaMenuLayout.AreaSlotCount);
        AreaMenuLayout.AreaSlotCount.Should().Be(7, "8 格软键里第 8 格是导航槽");
    }

    [Fact]
    public void Slot_number_matches_function_key_and_ctrl_shortcut()
    {
        IReadOnlyList<AreaSoftKey> keys = AreaMenuLayout.Build(
            AreaMenuLayout.DefaultOrder, PageKey.Profile, _ => true);

        keys.Select(k => k.ShortcutNumber).Should().Equal(Enumerable.Range(1, keys.Count));
        keys.Select(k => k.Area).Should().Equal(AreaMenuLayout.DefaultOrder);
    }

    [Fact]
    public void Exactly_the_current_area_is_marked()
    {
        IReadOnlyList<AreaSoftKey> keys = AreaMenuLayout.Build(
            AreaMenuLayout.DefaultOrder, PageKey.Records, _ => true);

        keys.Where(k => k.IsCurrent).Select(k => k.Area).Should().Equal(new[] { PageKey.Records });
    }

    [Fact]
    public void Unavailable_areas_stay_listed_in_place()
    {
        // 离线时进不去的页照样占着自己的格子，键位不跳动，只是标成不可用。
        var offline = new HashSet<PageKey> { PageKey.AutoGrinding, PageKey.Manual, PageKey.Diagnostics };

        IReadOnlyList<AreaSoftKey> keys = AreaMenuLayout.Build(
            AreaMenuLayout.DefaultOrder, PageKey.Steps, area => !offline.Contains(area));

        keys.Should().HaveCount(AreaMenuLayout.DefaultOrder.Count);
        keys.Where(k => !k.IsAvailable).Select(k => k.Area).Should().BeEquivalentTo(offline);
    }

    [Fact]
    public void An_eighth_area_is_refused_rather_than_squeezing_out_cancel()
    {
        PageKey[] tooMany = Enumerable.Repeat(PageKey.Records, AreaMenuLayout.AreaSlotCount + 1).ToArray();

        Action act = () => AreaMenuLayout.Build(tooMany, PageKey.Records, _ => true);

        act.Should().Throw<InvalidOperationException>();
    }
}
