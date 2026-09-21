using System;

namespace RollGrinder.App.Navigation;

/// <summary>
/// 页面跳转。页面只说"去哪儿"，换页、离开前的确认、导航槽的标签都由外壳负责——
/// 这样页面之间不互相引用，切换规则也只有一处。
/// </summary>
public interface INavigator
{
    /// <summary>平切到某个区域（等同于从区域菜单点它）。</summary>
    void GoToArea(PageKey area);

    /// <summary>
    /// 任务跳转：派到 <paramref name="target"/> 办一件事，办完导航槽送回 <paramref name="returnTo"/>。
    /// 例如工序编程页按"选择辊形"。
    /// </summary>
    void StartTask(PageKey target, PageKey returnTo);

    /// <summary>任务办完，回发起页。</summary>
    void CompleteTask();

    /// <summary>在本页内打开一层子视图（二级）。</summary>
    void OpenSubView(string subViewKey);

    /// <summary>关掉本页的子视图。</summary>
    void CloseSubView();

    /// <summary>展开区域菜单。</summary>
    void OpenAreaMenu();
}

/// <summary>页面提出的一次跳转请求。</summary>
/// <param name="Kind">请求类型。</param>
/// <param name="Target">目的区域（子视图请求时无意义）。</param>
/// <param name="ReturnTo">任务返回点。</param>
/// <param name="SubViewKey">子视图资源键。</param>
public sealed record NavigationRequest(
    NavigationRequestKind Kind,
    PageKey Target = NavigationModel.DefaultHomeArea,
    PageKey? ReturnTo = null,
    string? SubViewKey = null);

/// <summary>跳转请求的类型。</summary>
public enum NavigationRequestKind
{
    /// <summary>平切区域。</summary>
    GoToArea = 0,

    /// <summary>任务跳转。</summary>
    StartTask = 1,

    /// <summary>任务返回。</summary>
    CompleteTask = 2,

    /// <summary>打开子视图。</summary>
    OpenSubView = 3,

    /// <summary>关闭子视图。</summary>
    CloseSubView = 4,

    /// <summary>展开区域菜单。</summary>
    OpenAreaMenu = 5,
}

/// <summary>默认实现：只把请求转成事件，真正换页由外壳完成。</summary>
public sealed class Navigator : INavigator
{
    /// <summary>有人请求跳转。</summary>
    public event EventHandler<NavigationRequest>? Requested;

    public void GoToArea(PageKey area) =>
        Raise(new NavigationRequest(NavigationRequestKind.GoToArea, area));

    public void StartTask(PageKey target, PageKey returnTo) =>
        Raise(new NavigationRequest(NavigationRequestKind.StartTask, target, returnTo));

    public void CompleteTask() =>
        Raise(new NavigationRequest(NavigationRequestKind.CompleteTask));

    public void OpenSubView(string subViewKey) =>
        Raise(new NavigationRequest(NavigationRequestKind.OpenSubView, SubViewKey: subViewKey));

    public void CloseSubView() =>
        Raise(new NavigationRequest(NavigationRequestKind.CloseSubView));

    public void OpenAreaMenu() =>
        Raise(new NavigationRequest(NavigationRequestKind.OpenAreaMenu));

    private void Raise(NavigationRequest request) => Requested?.Invoke(this, request);
}
