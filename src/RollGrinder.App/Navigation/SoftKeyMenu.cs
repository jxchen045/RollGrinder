using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.App.Navigation;

/// <summary>竖向软键栏上的一格。</summary>
/// <typeparam name="TKey">键的类型（界面上是功能键视图模型，测试里随便一个类）。</typeparam>
/// <param name="Key">这一格的键；空位为 null。</param>
/// <param name="IsBack">这一格是"返回"：子菜单的第 8 格固定是它，按下去退回上一层。</param>
public sealed record SoftKeySlot<TKey>(TKey? Key, bool IsBack)
    where TKey : class;

/// <summary>
/// 右侧 8 个竖向软键的菜单（修改稿原则 1，对齐 SINUMERIK Operate 的竖向软键）。
/// 刻意做成纯逻辑，不引用 WPF，规则可以直接单测。
///
/// 规则：
/// 1. 根层是页面的编辑动作，最多 8 个，不足补空位；
/// 2. 标着"▸"的键打开一层子菜单（例如"插入段 ▸"→ 各种曲线类型），子菜单最多 7 项，第 8 格固定是"返回"；
/// 3. 子菜单里还能再开子菜单（"插入工序 ▸"→ 类别 → 具体工序），"返回"一次只退一层；
/// 4. 页面换了根层（换了选中项、切了页）子菜单全部收掉，不会留着一个和当前内容对不上的菜单。
/// </summary>
public sealed class SoftKeyMenu<TKey>
    where TKey : class
{
    /// <summary>竖向软键的格数。</summary>
    public const int SlotCount = 8;

    /// <summary>子菜单能放的项数：第 8 格留给"返回"。</summary>
    public const int SubMenuCapacity = SlotCount - 1;

    private readonly Stack<(string TitleKey, IReadOnlyList<TKey> Items)> subMenus = new();
    private IReadOnlyList<TKey> root = Array.Empty<TKey>();

    /// <summary>当前打开了几层子菜单；0 表示在根层。</summary>
    public int Depth => this.subMenus.Count;

    /// <summary>当前子菜单的标题资源键；根层为 null。</summary>
    public string? TitleKey => this.subMenus.Count == 0 ? null : this.subMenus.Peek().TitleKey;

    /// <summary>当前 8 格的样子。</summary>
    public IReadOnlyList<SoftKeySlot<TKey>> Slots
    {
        get
        {
            var slots = new List<SoftKeySlot<TKey>>(SlotCount);
            if (this.subMenus.Count == 0)
            {
                slots.AddRange(this.root.Select(key => new SoftKeySlot<TKey>(key, IsBack: false)));
                while (slots.Count < SlotCount)
                {
                    slots.Add(new SoftKeySlot<TKey>(null, IsBack: false));
                }

                return slots;
            }

            slots.AddRange(this.subMenus.Peek().Items.Select(key => new SoftKeySlot<TKey>(key, IsBack: false)));
            while (slots.Count < SubMenuCapacity)
            {
                slots.Add(new SoftKeySlot<TKey>(null, IsBack: false));
            }

            slots.Add(new SoftKeySlot<TKey>(null, IsBack: true));
            return slots;
        }
    }

    /// <summary>换根层（页面的编辑动作）。子菜单一并收掉。</summary>
    public void SetRoot(IEnumerable<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        TKey[] list = keys.ToArray();
        if (list.Length > SlotCount)
        {
            throw new InvalidOperationException(
                $"A vertical soft-key bar has {SlotCount} slots; {list.Length} keys were given.");
        }

        this.root = list;
        this.subMenus.Clear();
    }

    /// <summary>打开一层子菜单。</summary>
    public void Open(string titleKey, IEnumerable<TKey> items)
    {
        ArgumentException.ThrowIfNullOrEmpty(titleKey);
        ArgumentNullException.ThrowIfNull(items);
        TKey[] list = items.ToArray();
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

    /// <summary>收掉全部子菜单，回根层（选好了一项、或者切走了）。</summary>
    public void CloseAll() => this.subMenus.Clear();
}
