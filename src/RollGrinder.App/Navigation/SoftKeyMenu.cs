using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.App.Navigation;

/// <summary>软键格里放的是什么（最终稿 4.4）。</summary>
public enum SoftKeySlotKind
{
    /// <summary>页面自己的键。</summary>
    Key = 0,

    /// <summary>空位：画成空键，键位不随状态跳动。</summary>
    Empty = 1,

    /// <summary>"« 返回"：子菜单的第 8 格。</summary>
    Back = 2,

    /// <summary>"≡▸"：竖键多于一页时翻到下一页。</summary>
    NextPage = 3,

    /// <summary>"≡◂"：竖键最后一页翻回第一页。</summary>
    FirstPage = 4,

    /// <summary>"✕ 取消 / 放弃改动"：第 7 格，有待确认或待提交的事时出现。</summary>
    Cancel = 5,

    /// <summary>"✓ 确认 / 保存 / 下发改动"：第 8 格，有待确认或待提交的事时出现。</summary>
    Confirm = 6,
}

/// <summary>竖向软键栏上的一格。</summary>
/// <typeparam name="TKey">键的类型（界面上是功能键视图模型，测试里随便一个类）。</typeparam>
/// <param name="Key">这一格的键；空位、返回、翻页为 null（由外壳画）。</param>
/// <param name="Kind">这一格是什么。</param>
public sealed record SoftKeySlot<TKey>(TKey? Key, SoftKeySlotKind Kind)
    where TKey : class
{
    /// <summary>这一格是"返回"。</summary>
    public bool IsBack => Kind == SoftKeySlotKind.Back;
}

/// <summary>
/// 右侧 8 个竖向软键（最终稿 4.4，对齐 SINUMERIK Operate 的竖向软键）。
/// 刻意做成纯逻辑，不引用 WPF，规则可以直接单测。
///
/// 规则：
/// 1. 根层是本画面的操作；不足 8 个补空位；多于 8 个分页，每页 7 个，第 8 格是"≡▸"（最后一页是"≡◂"回第一页）。
///    根层列表里的 null 是占位空键，用来把后面的键推到下一页、或让出第 7 格；
/// 2. 标着"▸"的键打开一层子菜单，子菜单最多 7 项，第 8 格固定是"« 返回"；
/// 3. 子菜单里还能再开子菜单，"返回"一次只退一层；
/// 4. 页面换了根层（换了选中项、切了页）子菜单全部收掉、回第一页；
/// 5. 第 7 / 8 格有待确认或待提交的事时换成"✕ 取消 / ✓ 确认"（<see cref="WithCommitPair"/>），
///    其余 6 格照旧——人还看得见自己刚才按的是哪个键。
/// </summary>
public sealed class SoftKeyMenu<TKey>
    where TKey : class
{
    /// <summary>竖向软键的格数。</summary>
    public const int SlotCount = 8;

    /// <summary>子菜单（以及分页时每页）能放的项数：第 8 格留给"返回"或翻页。</summary>
    public const int SubMenuCapacity = SlotCount - 1;

    private readonly Stack<(string TitleKey, IReadOnlyList<TKey?> Items)> subMenus = new();
    private IReadOnlyList<TKey?> root = Array.Empty<TKey?>();

    /// <summary>当前打开了几层子菜单；0 表示在根层。</summary>
    public int Depth => this.subMenus.Count;

    /// <summary>当前子菜单的标题资源键；根层为 null。</summary>
    public string? TitleKey => this.subMenus.Count == 0 ? null : this.subMenus.Peek().TitleKey;

    /// <summary>根层分几页。</summary>
    public int PageCount => this.root.Count <= SlotCount ? 1 : (this.root.Count + SubMenuCapacity - 1) / SubMenuCapacity;

    /// <summary>根层当前是第几页（0 起）。</summary>
    public int PageIndex { get; private set; }

    /// <summary>当前 8 格的样子。</summary>
    public IReadOnlyList<SoftKeySlot<TKey>> Slots
    {
        get
        {
            var slots = new List<SoftKeySlot<TKey>>(SlotCount);
            if (this.subMenus.Count > 0)
            {
                slots.AddRange(this.subMenus.Peek().Items.Select(Slot));
                Pad(slots, SubMenuCapacity);
                slots.Add(new SoftKeySlot<TKey>(null, SoftKeySlotKind.Back));
                return slots;
            }

            if (PageCount == 1)
            {
                slots.AddRange(this.root.Select(Slot));
                Pad(slots, SlotCount);
                return slots;
            }

            slots.AddRange(this.root.Skip(PageIndex * SubMenuCapacity).Take(SubMenuCapacity).Select(Slot));
            Pad(slots, SubMenuCapacity);
            slots.Add(new SoftKeySlot<TKey>(
                null, PageIndex == PageCount - 1 ? SoftKeySlotKind.FirstPage : SoftKeySlotKind.NextPage));
            return slots;
        }
    }

    /// <summary>换根层（本画面的操作）。子菜单一并收掉，回第一页。</summary>
    public void SetRoot(IEnumerable<TKey?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        this.root = keys.ToArray();
        this.subMenus.Clear();
        PageIndex = 0;
    }

    /// <summary>打开一层子菜单。</summary>
    public void Open(string titleKey, IEnumerable<TKey?> items)
    {
        ArgumentException.ThrowIfNullOrEmpty(titleKey);
        ArgumentNullException.ThrowIfNull(items);
        TKey?[] list = items.ToArray();
        if (list.Length > SubMenuCapacity)
        {
            throw new InvalidOperationException(
                $"A soft-key sub menu holds {SubMenuCapacity} items beside the back key; {list.Length} were given. "
                + "Use a selection list for longer choices.");
        }

        this.subMenus.Push((titleKey, list));
    }

    /// <summary>退一层。在根层时什么都不做，返回 false。</summary>
    public bool Back()
    {
        if (this.subMenus.Count == 0)
        {
            return false;
        }

        this.subMenus.Pop();
        return true;
    }

    /// <summary>收掉全部子菜单，回根层（选好了一项、或者切走了）。页码不动。</summary>
    public void CloseAll() => this.subMenus.Clear();

    /// <summary>"≡▸ / ≡◂"：翻到下一页，最后一页翻回第一页。只有一页时返回 false。</summary>
    public bool NextPage()
    {
        if (this.subMenus.Count > 0 || PageCount == 1)
        {
            return false;
        }

        PageIndex = (PageIndex + 1) % PageCount;
        return true;
    }

    /// <summary>
    /// 第 7 / 8 格换成取消 / 确认（最终稿 4.4）。两个都给才换；只给一个视同没有——
    /// 取消和确认永远成对出现，确认键和发起键不在同一个位置（C4）。
    /// </summary>
    public static IReadOnlyList<SoftKeySlot<TKey>> WithCommitPair(
        IReadOnlyList<SoftKeySlot<TKey>> slots, TKey? cancel, TKey? confirm)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (cancel is null || confirm is null)
        {
            return slots;
        }

        var result = slots.ToList();
        result[SlotCount - 2] = new SoftKeySlot<TKey>(cancel, SoftKeySlotKind.Cancel);
        result[SlotCount - 1] = new SoftKeySlot<TKey>(confirm, SoftKeySlotKind.Confirm);
        return result;
    }

    private static SoftKeySlot<TKey> Slot(TKey? key) =>
        new(key, key is null ? SoftKeySlotKind.Empty : SoftKeySlotKind.Key);

    private static void Pad(List<SoftKeySlot<TKey>> slots, int count)
    {
        while (slots.Count < count)
        {
            slots.Add(new SoftKeySlot<TKey>(null, SoftKeySlotKind.Empty));
        }
    }
}

/// <summary>
/// 横键条（最终稿 4.1）：一页 8 个键，多了分页，右端的"&gt;"翻页并显示页码。
/// 纯逻辑；列表里的 null 是占位空键（最终稿里"空"的那几格）。
/// </summary>
public sealed class SoftKeyRow<TKey>
    where TKey : class
{
    /// <summary>横键一页的格数。</summary>
    public const int SlotCount = 8;

    private IReadOnlyList<TKey?> keys = Array.Empty<TKey?>();

    /// <summary>分几页（至少 1）。</summary>
    public int PageCount => Math.Max(1, (this.keys.Count + SlotCount - 1) / SlotCount);

    /// <summary>当前页（0 起）。</summary>
    public int PageIndex { get; private set; }

    /// <summary>当前页的 8 格；空位为 null。</summary>
    public IReadOnlyList<TKey?> Slots
    {
        get
        {
            var slots = this.keys.Skip(PageIndex * SlotCount).Take(SlotCount).ToList();
            while (slots.Count < SlotCount)
            {
                slots.Add(null);
            }

            return slots;
        }
    }

    /// <summary>换一组键，回第一页。</summary>
    public void Set(IEnumerable<TKey?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        this.keys = row.ToArray();
        PageIndex = 0;
    }

    /// <summary>"&gt;"：翻到下一页，最后一页翻回第一页。只有一页时返回 false。</summary>
    public bool NextPage()
    {
        if (PageCount == 1)
        {
            return false;
        }

        PageIndex = (PageIndex + 1) % PageCount;
        return true;
    }
}
