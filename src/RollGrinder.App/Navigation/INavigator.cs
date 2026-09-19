using System;

namespace RollGrinder.App.Navigation;

/// <summary>
/// 页面跳转。页面只说"去哪儿"，由外壳负责换页——这样页面之间不互相引用。
/// </summary>
public interface INavigator
{
    /// <summary>跳到指定页面。</summary>
    void NavigateTo(PageKey key);

    /// <summary>返回上一页；没有上一页时回自动磨削页。</summary>
    void GoBack();
}

/// <summary>默认实现：只发事件，换页由外壳完成。</summary>
public sealed class Navigator : INavigator
{
    /// <summary>有人请求换页。</summary>
    public event EventHandler<PageKey>? NavigationRequested;

    /// <summary>有人请求返回。</summary>
    public event EventHandler? BackRequested;

    public void NavigateTo(PageKey key) => NavigationRequested?.Invoke(this, key);

    public void GoBack() => BackRequested?.Invoke(this, EventArgs.Empty);
}
