using System;

namespace RollGrinder.App.Navigation;

/// <summary>"返回"（Esc、路径条上的"«"）现在会做什么。</summary>
public enum BackRole
{
    /// <summary>已经在区域的根上，没有可退的。换区域用左栏或区域菜单。</summary>
    None = 0,

    /// <summary>区域菜单开着：收起，横键条变回本画面的功能组。</summary>
    CloseAreaMenu = 1,

    /// <summary>画面里开着子功能（例如自动磨削 › 补偿）：关掉，回画面根部。</summary>
    CloseSubView = 2,

    /// <summary>这一页是被别的页派来办事的（例如作业向导 › 打开辊形）：回发起页。</summary>
    BackToTask = 3,
}

/// <summary>"返回"的样子：做什么，回到哪一页（关子功能时是本页）。</summary>
/// <param name="Role">角色。</param>
/// <param name="Target">目的画面；None 与收起菜单时为 null。</param>
public sealed record BackDescriptor(BackRole Role, PageKey? Target);

/// <summary>
/// 画面切换的状态机（最终稿 4.1、4.4）。刻意做成纯逻辑（不引用 WPF、不引用本地化），规则直接单测。
///
/// 结构：8 个区域，每个区域一个或几个画面（机床区：手动磨削、自动磨削、手动动作页、作业），
/// 画面里还可以开一层子功能（补偿、轧辊台账……）。
/// 规则：
/// 1. 区域之间是平的——左栏一点、区域菜单一选，从哪儿到哪儿都是一步，不叠历史栈；
/// 2. 只有"任务跳转"（A 页派你去 B 页取个东西）才记返回点；
///    A 页自己也是被派来的（工艺程序 → 作业 → 台账登记新辊）时，回到 A 页，A 页原来的返回点还在；
///    只多记这一层，不是历史栈；
/// 3. "返回"只退一级：先收菜单，再关子功能，再回任务发起页；都没有就不动。
/// </summary>
public sealed class NavigationModel
{
    /// <summary>
    /// 平常开机停在哪一页：机床区的手动磨削（NC 在 AUTO 时外壳会换成自动磨削）。
    /// <see cref="INavigator"/> 的默认参数要一个编译期常量，所以它得是 const。
    /// </summary>
    public const PageKey DefaultHomePage = PageKey.ManualGrinding;

    public NavigationModel(PageKey? homePage = null)
    {
        HomePage = homePage ?? DefaultHomePage;
        CurrentPage = HomePage;
    }

    /// <summary>
    /// 本次运行的开机画面，也是任务没有返回点时的落脚处。
    /// 离线模式下机床区用不了，外壳会把它换成第一个离线能用的画面。
    /// </summary>
    public PageKey HomePage { get; }

    /// <summary>当前画面。</summary>
    public PageKey CurrentPage { get; private set; }

    /// <summary>当前区域。</summary>
    public AreaKey CurrentArea => AreaCatalog.AreaOf(CurrentPage);

    /// <summary>当前子功能的资源键；null 表示停在画面根部。</summary>
    public string? CurrentSubViewKey { get; private set; }

    /// <summary>任务返回点；null 表示当前不是被"派"来的。</summary>
    public PageKey? TaskReturnPage { get; private set; }

    /// <summary>
    /// 发起页自己的返回点：任务是从一个"被派来"的页上再派出去的，回到发起页时恢复它。
    /// </summary>
    private PageKey? returnPageOfIssuer;

    /// <summary>区域菜单是否展开（横键条换成 8 个区域）。</summary>
    public bool IsAreaMenuOpen { get; private set; }

    /// <summary>切到某个画面。这是"平的"切换：清掉子功能与任务返回点，收起区域菜单。</summary>
    /// <returns>画面确实变了返回 true。</returns>
    public bool GoTo(PageKey page)
    {
        IsAreaMenuOpen = false;
        CurrentSubViewKey = null;
        TaskReturnPage = null;
        this.returnPageOfIssuer = null;

        if (CurrentPage == page)
        {
            return false;
        }

        CurrentPage = page;
        return true;
    }

    /// <summary>
    /// 任务跳转：从 <paramref name="returnTo"/> 派到 <paramref name="target"/>，
    /// 办完由"返回"送回。目的地就是发起页时退化成普通切换。
    /// </summary>
    public bool StartTask(PageKey target, PageKey returnTo)
    {
        if (target == returnTo)
        {
            return GoTo(target);
        }

        // 发起页就是当前页、且它自己也是被派来的：记下它的返回点，回来时还给它。
        PageKey? issuerReturn = CurrentPage == returnTo ? TaskReturnPage : null;
        bool changed = GoTo(target);
        TaskReturnPage = returnTo;
        this.returnPageOfIssuer = issuerReturn == target ? null : issuerReturn;
        return changed;
    }

    /// <summary>任务办完，回发起页（发起页原来的返回点一并恢复）。没有返回点时回开机画面。</summary>
    public bool CompleteTask()
    {
        PageKey target = TaskReturnPage ?? HomePage;
        PageKey? restored = TaskReturnPage is null ? null : this.returnPageOfIssuer;
        bool changed = GoTo(target);
        TaskReturnPage = restored;
        return changed;
    }

    /// <summary>打开本画面的一个子功能。</summary>
    public void OpenSubView(string subViewKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(subViewKey);
        IsAreaMenuOpen = false;
        CurrentSubViewKey = subViewKey;
    }

    /// <summary>关掉子功能，回画面根部。</summary>
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

    /// <summary>区域方块、F10：开着就收，收着就开。</summary>
    public void ToggleAreaMenu() => IsAreaMenuOpen = !IsAreaMenuOpen;

    /// <summary>
    /// "返回"现在会做什么。优先级：菜单 &gt; 子功能 &gt; 任务返回点。
    /// 区域根部没有"回主页"：区域是平的，换区域是左栏一点。
    /// </summary>
    public BackDescriptor DescribeBack()
    {
        if (IsAreaMenuOpen)
        {
            return new BackDescriptor(BackRole.CloseAreaMenu, null);
        }

        if (CurrentSubViewKey is not null)
        {
            return new BackDescriptor(BackRole.CloseSubView, CurrentPage);
        }

        if (TaskReturnPage is PageKey returnPage)
        {
            return new BackDescriptor(BackRole.BackToTask, returnPage);
        }

        return new BackDescriptor(BackRole.None, null);
    }
}
