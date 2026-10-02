using System;
using System.Threading.Tasks;
using RollGrinder.App.Localization;

namespace RollGrinder.App.Interaction;

/// <summary>
/// 各画面与外壳共用的交互出口（最终稿 4.5、7.1）：确认（竖键 7 / 8）与对话行。
/// 一个进程一份（DI 单例）：同一时刻只有一件待确认的事、一条对话行。
/// </summary>
public sealed class ShellInteraction
{
    public ShellInteraction(TimeProvider time, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(time);
        Localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Confirmations = new ConfirmationService(time);
        DialogLine = new DialogLineModel(time);
        Confirmations.Changed += (_, _) => DialogLine.SetQuestion(Confirmations.Pending?.Question);
    }

    /// <summary>待确认的事。</summary>
    public ConfirmationService Confirmations { get; }

    /// <summary>对话行。</summary>
    public DialogLineModel DialogLine { get; }

    /// <summary>取字。</summary>
    public IStringLocalizer Localizer { get; }

    /// <summary>
    /// 先问再做：对话行写出问题，竖键 7 / 8 换成取消 / 确认，5 秒不答自动取消。
    /// </summary>
    public void Ask(
        string question,
        Func<Task> action,
        Action<ConfirmationOutcome>? closed = null,
        string confirmLabelKey = ConfirmationService.DefaultConfirmLabelKey,
        string cancelLabelKey = ConfirmationService.DefaultCancelLabelKey) =>
        Confirmations.Request(question, action, closed, confirmLabelKey, cancelLabelKey);

    /// <summary>同步动作的便捷重载。</summary>
    public void Ask(
        string question,
        Action action,
        Action<ConfirmationOutcome>? closed = null,
        string confirmLabelKey = ConfirmationService.DefaultConfirmLabelKey,
        string cancelLabelKey = ConfirmationService.DefaultCancelLabelKey) =>
        Confirmations.Request(question, action, closed, confirmLabelKey, cancelLabelKey);

    /// <summary>
    /// 选择题（不自动取消）：对话行黄底提问，竖键 8 / 回车 = 稳妥项（<paramref name="safeLabelKey"/>），7 / Esc = 取消；
    /// 有后果的另一项由页面放在竖键 6（界面修订稿 4.2"三选一"）。
    /// </summary>
    public void Choose(string question, string safeLabelKey, Func<Task> safe, Action<ConfirmationOutcome>? closed = null) =>
        Confirmations.Request(question, safe, closed, safeLabelKey, ConfirmationService.DefaultCancelLabelKey, expires: false);

    /// <summary>消息：动作已发出、已保存（3 秒后消失）。</summary>
    public void Say(string text) => DialogLine.Show(text, DialogLineKind.Info);

    /// <summary>按不了的原因。</summary>
    public void Refuse(string text) => DialogLine.Show(text, DialogLineKind.Reason);

    /// <summary>错误：输入超出范围、保存失败。</summary>
    public void Fail(string text) => DialogLine.Show(text, DialogLineKind.Error, TimeSpan.FromSeconds(6));

    /// <summary>常驻说明：当前格的含义、单位、范围，或本画面的操作提示。null 清掉。</summary>
    public void Hint(string? text) => DialogLine.SetHint(text);

    /// <summary>界面节拍：确认超时、临时消息到时。</summary>
    public void Tick()
    {
        Confirmations.Tick();
        DialogLine.Tick();
    }
}
