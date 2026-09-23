using System;

namespace RollGrinder.App.Navigation;

/// <summary>底部功能条最后一个键（导航槽）当前扮演的角色。</summary>
public enum NavigationKeyRole
{
    /// <summary>主页根部：本页没有上一级，这个键用来打开页面菜单。</summary>
    OpenAreaMenu = 0,

    /// <summary>子页面根部：回主页（自动磨削）。</summary>
    BackToHome = 1,

    /// <summary>因任务跳转而来（如工序编程 → 选择辊形）：回发起页。</summary>
    BackToTask = 2,

    /// <summary>页内二级子视图：关掉子视图，回本页根部。</summary>
    CloseSubView = 3,

    /// <summary>软键条正处于页面菜单态：取消，软键条变回本页的功能键。</summary>
    CloseAreaMenu = 4,
}

/// <summary>
/// 导航槽当前的样子。标签永远说明"按下去到哪儿"，不做隐藏模式。
/// </summary>
/// <param name="Role">角色。</param>
/// <param name="LabelResourceKey">标签资源键；带 {0} 的由外壳填入目的页名。</param>
/// <param name="TargetArea">目的区域；打开菜单与关闭子视图时为 null（不换页）。</param>
public sealed record NavigationKeyDescriptor(
    NavigationKeyRole Role,
    string LabelResourceKey,
    PageKey? TargetArea);

/// <summary>
/// 页面切换的状态机。刻意做成纯逻辑（不引用 WPF、不引用本地化），
/// 这样切换规则可以被单元测试直接覆盖。
///
/// 结构：主页（自动磨削）+ 6 个子页面，每页可再打开一层子视图，最深三层。
/// 规则：
/// 1. 区域之间永远是"平的"——从任何区域到任何区域都是一步，不叠历史栈；
/// 2. 导航槽（第 8 键）只退一级，且标签写明退到哪；
/// 3. 只有"任务跳转"（A 页派你去 B 页取个东西）才记一个返回点，且只记一个。
/// </summary>
public sealed class NavigationModel
{
    /// <summary>
    /// 平常的主页。<see cref="INavigator"/> 的默认参数要一个编译期常量，所以它得是 const。
    /// </summary>
    public const PageKey DefaultHomeArea = PageKey.AutoGrinding;

    /// <summary>
    /// 本次运行的主页。开机停在这里，任何地方按"返回主页"也回这里。
    ///
    /// 离线模式下自动磨削页用不了（没有机床可监控），主页改成工序编程——
    /// 否则"返回主页"会把人送到一个只能看不能用的页面上。
    /// </summary>
    public PageKey HomeArea { get; }

    public NavigationModel(PageKey? homeArea = null)
    {
        HomeArea = homeArea ?? DefaultHomeArea;
        CurrentArea = HomeArea;
    }

    /// <summary>当前一级区域。</summary>
    public PageKey CurrentArea { get; private set; }

    /// <summary>当前二级子视图的资源键；null 表示停在一级页根部。</summary>
    public string? CurrentSubViewKey { get; private set; }

    /// <summary>任务返回点；null 表示当前不是被"派"来的。</summary>
    public PageKey? TaskReturnArea { get; private set; }

    /// <summary>区域菜单是否展开。</summary>
    public bool IsAreaMenuOpen { get; private set; }

    /// <summary>当前深度：主页根部 1，一级子页或主页子视图 2，子页的子视图 3。</summary>
    public int Depth => (CurrentArea == HomeArea ? 1 : 2) + (CurrentSubViewKey is null ? 0 : 1);

    /// <summary>切到某个区域。这是"平的"切换：清掉子视图与任务返回点。</summary>
    /// <returns>区域确实变了返回 true。</returns>
    public bool GoToArea(PageKey area)
    {
        IsAreaMenuOpen = false;
        CurrentSubViewKey = null;
        TaskReturnArea = null;

        if (CurrentArea == area)
        {
            return false;
        }

        CurrentArea = area;
        return true;
    }

    /// <summary>
    /// 任务跳转：从 <paramref name="returnTo"/> 派到 <paramref name="target"/>，
    /// 办完由导航槽送回。目的地就是发起页时退化成普通切换。
    /// </summary>
    public bool StartTask(PageKey target, PageKey returnTo)
    {
        if (target == returnTo)
        {
            return GoToArea(target);
        }

        bool changed = GoToArea(target);
        TaskReturnArea = returnTo;
        return changed;
    }

    /// <summary>任务办完，回发起页。没有返回点时回主页。</summary>
    public bool CompleteTask()
    {
        PageKey target = TaskReturnArea ?? HomeArea;
        return GoToArea(target);
    }

    /// <summary>打开本页的二级子视图。</summary>
    public void OpenSubView(string subViewKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(subViewKey);
        IsAreaMenuOpen = false;
        CurrentSubViewKey = subViewKey;
    }

    /// <summary>关掉二级子视图，回本页根部。</summary>
    /// <returns>确实关掉了返回 true。</returns>
    public bool CloseSubView()
    {
        if (CurrentSubViewKey is null)
        {
            return false;
        }

        CurrentSubViewKey = null;
        return true;
    }

    /// <summary>展开区域菜单。</summary>
    public void OpenAreaMenu() => IsAreaMenuOpen = true;

    /// <summary>收起区域菜单。</summary>
    public void CloseAreaMenu() => IsAreaMenuOpen = false;

    /// <summary>
    /// 导航槽当前该显示什么。优先级：菜单态 &gt; 子视图 &gt; 任务返回点 &gt; 回主页 &gt; 打开菜单。
    /// 任何位置都有明确含义，不存在按了没反应的死键。
    ///
    /// 菜单态排第一：页面菜单是把底部软键条原地换成区域键（对齐 Operate 的 MENU SELECT），
    /// 这时第 8 键就是"取消"，按下去软键条变回来，不换页。
    /// </summary>
    public NavigationKeyDescriptor DescribeNavigationKey()
    {
        if (IsAreaMenuOpen)
        {
            return new NavigationKeyDescriptor(NavigationKeyRole.CloseAreaMenu, "Menu_Cancel", null);
        }

        if (CurrentSubViewKey is not null)
        {
            return new NavigationKeyDescriptor(NavigationKeyRole.CloseSubView, "Nav_BackToPageFormat", CurrentArea);
        }

        if (TaskReturnArea is PageKey returnArea)
        {
            return new NavigationKeyDescriptor(NavigationKeyRole.BackToTask, "Nav_BackToPageFormat", returnArea);
        }

        if (CurrentArea != HomeArea)
        {
            return new NavigationKeyDescriptor(NavigationKeyRole.BackToHome, "Nav_BackToHome", HomeArea);
        }

        return new NavigationKeyDescriptor(NavigationKeyRole.OpenAreaMenu, "Nav_AreaMenu", null);
    }
}
