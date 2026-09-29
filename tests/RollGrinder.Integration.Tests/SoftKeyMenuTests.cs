using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.App.Navigation;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>软键规则（界面最终稿 4.4）：竖键第 8 格三选一、第 7 / 8 格取消 / 确认、横键分页。</summary>
public sealed class SoftKeyMenuTests
{
    private sealed record Key(string Name);

    private static Key?[] Keys(params string?[] names) => names.Select(name => name is null ? null : new Key(name)).ToArray();

    [Fact]
    public void The_root_fills_eight_slots_and_has_no_back_key()
    {
        var menu = new SoftKeyMenu<Key>();
        menu.SetRoot(Keys("insert", "delete", "up"));

        menu.Slots.Should().HaveCount(8);
        menu.Slots.Take(3).Select(slot => slot.Key!.Name).Should().Equal("insert", "delete", "up");
        menu.Slots.Skip(3).Should().OnlyContain(slot => slot.Kind == SoftKeySlotKind.Empty, "空位画成空键，根层没有返回");
        menu.Depth.Should().Be(0);
        menu.TitleKey.Should().BeNull();
        menu.PageCount.Should().Be(1);
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
    public void More_than_eight_root_keys_are_paged_seven_at_a_time_behind_the_page_key()
    {
        var menu = new SoftKeyMenu<Key>();
        menu.SetRoot(Keys("1", "2", "3", "4", "5", "6", null, "p2a", "p2b"));

        menu.PageCount.Should().Be(2);
        menu.Slots.Take(6).Select(s => s.Key!.Name).Should().Equal("1", "2", "3", "4", "5", "6");
        menu.Slots[6].Kind.Should().Be(SoftKeySlotKind.Empty, "占位空键把第 7 格让给取消");
        menu.Slots[7].Kind.Should().Be(SoftKeySlotKind.NextPage);

        menu.NextPage().Should().BeTrue();
        menu.Slots.Take(2).Select(s => s.Key!.Name).Should().Equal("p2a", "p2b");
        menu.Slots[7].Kind.Should().Be(SoftKeySlotKind.FirstPage, "最后一页是'≡◂'，翻回第一页");

        menu.NextPage().Should().BeTrue();
        menu.PageIndex.Should().Be(0);

        menu.SetRoot(Keys("only"));
        menu.NextPage().Should().BeFalse("只有一页就没有翻页");
    }

    [Fact]
    public void Eight_root_keys_fit_without_paging()
    {
        var menu = new SoftKeyMenu<Key>();
        menu.SetRoot(Keys("1", "2", "3", "4", "5", "6", "7", "8"));

        menu.PageCount.Should().Be(1);
        menu.Slots.Should().OnlyContain(s => s.Kind == SoftKeySlotKind.Key);
    }

    [Fact]
    public void A_commit_pair_takes_slots_seven_and_eight_and_leaves_the_rest()
    {
        var menu = new SoftKeyMenu<Key>();
        menu.SetRoot(Keys("curve 1", "curve 2", "curve 3", "curve 4", "curve 5"));
        var cancel = new Key("discard");
        var confirm = new Key("download");

        var slots = SoftKeyMenu<Key>.WithCommitPair(menu.Slots, cancel, confirm);

        slots.Take(5).Select(s => s.Key!.Name).Should().Equal("curve 1", "curve 2", "curve 3", "curve 4", "curve 5");
        slots[6].Should().Be(new SoftKeySlot<Key>(cancel, SoftKeySlotKind.Cancel));
        slots[7].Should().Be(new SoftKeySlot<Key>(confirm, SoftKeySlotKind.Confirm));

        var plain = menu.Slots;
        SoftKeyMenu<Key>.WithCommitPair(plain, cancel, null).Should().BeSameAs(plain,
            "取消和确认永远成对出现");
    }

    [Fact]
    public void A_commit_pair_also_covers_the_back_key_of_a_sub_menu_while_it_is_pending()
    {
        var menu = new SoftKeyMenu<Key>();
        menu.SetRoot(Keys("position"));
        menu.Open("Vk_Position", Keys("to z"));

        var slots = SoftKeyMenu<Key>.WithCommitPair(menu.Slots, new Key("cancel"), new Key("confirm"));

        slots[7].Kind.Should().Be(SoftKeySlotKind.Confirm);
        menu.Slots[7].IsBack.Should().BeTrue("答完之后'返回'原样回来");
    }

    [Fact]
    public void Too_many_sub_menu_items_are_refused_instead_of_silently_cut_off()
    {
        var menu = new SoftKeyMenu<Key>();
        FluentActions.Invoking(() => menu.Open("Vk_X", Keys("1", "2", "3", "4", "5", "6", "7", "8")))
            .Should().Throw<InvalidOperationException>("第 8 格要留给返回，多了的用选择列表");
    }

    [Fact]
    public void The_horizontal_row_pages_eight_at_a_time()
    {
        var row = new SoftKeyRow<Key>();
        row.Set(Keys("a", "b", null, "d"));

        row.PageCount.Should().Be(1);
        row.Slots.Should().HaveCount(8);
        row.Slots[2].Should().BeNull("最终稿里'空'的那格留成空键");
        row.NextPage().Should().BeFalse();

        row.Set(Enumerable.Range(1, 10).Select(i => new Key(i.ToString())));
        row.PageCount.Should().Be(2);
        row.NextPage().Should().BeTrue();
        row.Slots.Take(2).Select(k => k!.Name).Should().Equal("9", "10");
        row.Slots.Skip(2).Should().OnlyContain(k => k == null);
        row.NextPage().Should().BeTrue();
        row.PageIndex.Should().Be(0);
    }
}
