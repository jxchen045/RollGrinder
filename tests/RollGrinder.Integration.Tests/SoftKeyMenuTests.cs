using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.App.Navigation;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>右侧竖向软键的菜单规则（修改稿原则 1）。</summary>
public sealed class SoftKeyMenuTests
{
    private sealed record Key(string Name);

    private static Key[] Keys(params string[] names) => names.Select(name => new Key(name)).ToArray();

    [Fact]
    public void The_root_fills_eight_slots_and_has_no_back_key()
    {
        var menu = new SoftKeyMenu<Key>();
        menu.SetRoot(Keys("insert", "delete", "up"));

        menu.Slots.Should().HaveCount(8);
        menu.Slots.Take(3).Select(slot => slot.Key!.Name).Should().Equal("insert", "delete", "up");
        menu.Slots.Skip(3).Should().OnlyContain(slot => slot.Key == null && !slot.IsBack, "空位是灰的，根层没有返回");
        menu.Depth.Should().Be(0);
        menu.TitleKey.Should().BeNull();
    }

    [Fact]
    public void A_sub_menu_puts_back_in_the_eighth_slot_and_back_climbs_one_level()
    {
        var menu = new SoftKeyMenu<Key>();
        menu.SetRoot(Keys("insert step"));

        menu.Open("Vk_InsertStep", Keys("grinding", "measuring"));
        menu.Open("Vk_Grinding", Keys("rough", "finish"));

        menu.Depth.Should().Be(2);
        menu.TitleKey.Should().Be("Vk_Grinding");
        menu.Slots[7].IsBack.Should().BeTrue("子菜单的第 8 格固定是返回");
        menu.Slots.Take(2).Select(slot => slot.Key!.Name).Should().Equal("rough", "finish");

        menu.Back().Should().BeTrue();
        menu.TitleKey.Should().Be("Vk_InsertStep", "一次只退一层");
        menu.Back().Should().BeTrue();
        menu.Back().Should().BeFalse("根层再退就交给外壳");
        menu.Slots[0].Key!.Name.Should().Be("insert step");
    }

    [Fact]
    public void Choosing_or_changing_the_root_closes_every_sub_menu()
    {
        var menu = new SoftKeyMenu<Key>();
        menu.SetRoot(Keys("a"));
        menu.Open("Vk_X", Keys("x"));
        menu.Open("Vk_Y", Keys("y"));

        menu.CloseAll();
        menu.Depth.Should().Be(0);

        menu.Open("Vk_X", Keys("x"));
        menu.SetRoot(Keys("b"));
        menu.Depth.Should().Be(0, "换了根层，旧的子菜单和当前内容对不上了");
    }

    [Fact]
    public void Too_many_keys_are_refused_instead_of_silently_cut_off()
    {
        var menu = new SoftKeyMenu<Key>();
        FluentActions.Invoking(() => menu.SetRoot(Keys("1", "2", "3", "4", "5", "6", "7", "8", "9")))
            .Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => menu.Open("Vk_X", Keys("1", "2", "3", "4", "5", "6", "7", "8")))
            .Should().Throw<InvalidOperationException>("第 8 格要留给返回，多了的用选择列表");
    }
}
