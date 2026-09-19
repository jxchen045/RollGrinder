using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Services.Alarms;

namespace RollGrinder.App.ViewModels;

/// <summary>顶栏上的一项上下文信息，例如"轧辊编号 BX-20916"。</summary>
/// <param name="LabelResourceKey">标签资源键。</param>
/// <param name="Value">取值（现场数据，不翻译）。</param>
/// <param name="IsMonospaced">是否用等宽字体显示（编号、位置值用）。</param>
public sealed record ContextItem(string LabelResourceKey, string Value, bool IsMonospaced = false);

/// <summary>
/// 一个主界面。外壳负责画顶栏与底部功能条，页面负责给出标题、上下文与 8 个功能键。
/// </summary>
public abstract partial class PageViewModelBase : ViewModelBase
{
    protected PageViewModelBase(IAlarmSink alarms, IStringLocalizer localizer, INavigator navigator)
        : base(alarms)
    {
        Localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
    }

    protected IStringLocalizer Localizer { get; }

    protected INavigator Navigator { get; }

    /// <summary>页面标识。</summary>
    public abstract PageKey Key { get; }

    /// <summary>页面标题的资源键。</summary>
    public abstract string TitleResourceKey { get; }

    /// <summary>页面标题。</summary>
    public string Title => Localizer[TitleResourceKey];

    /// <summary>顶栏上显示的上下文。</summary>
    public ObservableCollection<ContextItem> ContextItems { get; } = new();

    /// <summary>底部功能条的 8 个键，最后一个恒为"返回"。</summary>
    public ObservableCollection<FunctionKeyViewModel> FunctionKeys { get; } = new();

    /// <summary>切到本页时调用。</summary>
    public virtual void OnActivated()
    {
    }

    /// <summary>界面节拍（5–10 Hz）。只有当前页会收到。</summary>
    public virtual void OnTick(DateTimeOffset nowUtc)
    {
    }

    /// <summary>登记功能键。第 8 个由基类补上"返回"。</summary>
    protected void SetFunctionKeys(IEnumerable<FunctionKeyViewModel> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        FunctionKeys.Clear();
        foreach (FunctionKeyViewModel key in keys)
        {
            FunctionKeys.Add(key);
        }

        FunctionKeys.Add(FunctionKeyViewModel.Placeholder("Fn_Back", Localizer, () => Navigator.GoBack()));
    }

    /// <summary>尚未接通的动作：按下去登记一条提示级报警，而不是假装成功。</summary>
    protected void NotImplementedYet(string labelResourceKey) =>
        Alarms.Raise(AlarmSeverity.Information, "Alarm_ActionNotWiredYet", Localizer[labelResourceKey]);
}
