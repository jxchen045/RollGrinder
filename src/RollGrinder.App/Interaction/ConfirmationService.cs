using System;
using System.Threading.Tasks;

namespace RollGrinder.App.Interaction;

/// <summary>一件等着人确认的事。</summary>
/// <param name="Question">对话行上的问题：要做什么、影响什么（已经本地化好的整句）。</param>
/// <param name="ConfirmLabelKey">第 8 格确认键的字（资源键），例如"✓ 确认""✓ 确认下发"。</param>
/// <param name="CancelLabelKey">第 7 格取消键的字（资源键）。</param>
/// <param name="RequestedAt">提问的时刻；5 秒不答自动取消。</param>
public sealed record PendingConfirmation(
    string Question,
    string ConfirmLabelKey,
    string CancelLabelKey,
    DateTimeOffset RequestedAt);

/// <summary>一件待确认的事是怎么结束的。</summary>
public enum ConfirmationOutcome
{
    /// <summary>按了确认（竖键 8 或回车）。</summary>
    Confirmed = 0,

    /// <summary>按了取消（竖键 7 或 Esc）。</summary>
    Cancelled = 1,

    /// <summary>5 秒没人答，自动取消。</summary>
    Expired = 2,

    /// <summary>还没答就又发起了另一件事，前一件作废。</summary>
    Superseded = 3,
}

/// <summary>
/// 确认（最终稿 D5、4.5）：对话行提问 + 竖键第 7 格红色"✕ 取消"、第 8 格绿色"✓ 确认"；
/// 5 秒不按自动取消；同一时刻只有一件待确认的事。回车 = 确认，Esc = 取消。
///
/// 取代旧的"再按一次确认"：那种做法确认键和发起键在同一个位置，手一抖连按两下就发出去了（C4）。
///
/// 纯逻辑：时钟由 <see cref="TimeProvider"/> 给，超时由外壳的界面节拍调 <see cref="Tick"/> 检查，
/// 单测里拨表就能验证。
/// </summary>
public sealed class ConfirmationService
{
    /// <summary>不答自动取消的时限。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>默认确认键的资源键。</summary>
    public const string DefaultConfirmLabelKey = "Vk_Confirm";

    /// <summary>默认取消键的资源键。</summary>
    public const string DefaultCancelLabelKey = "Vk_Cancel";

    private readonly TimeProvider time;
    private Func<Task>? onConfirm;
    private Action<ConfirmationOutcome>? onClosed;

    public ConfirmationService(TimeProvider time)
    {
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>待确认的事变了（出现、答完、超时）。</summary>
    public event EventHandler? Changed;

    /// <summary>当前待确认的事；没有为 null。</summary>
    public PendingConfirmation? Pending { get; private set; }

    /// <summary>还剩多少时间自动取消；没有待确认的事时为零。</summary>
    public TimeSpan Remaining => Pending is null
        ? TimeSpan.Zero
        : Max(TimeSpan.Zero, Timeout - (this.time.GetUtcNow() - Pending.RequestedAt));

    /// <summary>
    /// 提一个问题。已有一件没答的，先作废它（<see cref="ConfirmationOutcome.Superseded"/>）——
    /// 同一时刻只问一件事，免得人以为确认的是屏幕上最新那句、实际发出的是前一件。
    /// </summary>
    /// <param name="question">对话行上的整句。</param>
    /// <param name="confirm">确认后做什么。</param>
    /// <param name="closed">不论怎么结束都会通知一次（例如把标红的给定值恢复）。</param>
    /// <param name="confirmLabelKey">第 8 格的字。</param>
    /// <param name="cancelLabelKey">第 7 格的字。</param>
    public void Request(
        string question,
        Func<Task> confirm,
        Action<ConfirmationOutcome>? closed = null,
        string confirmLabelKey = DefaultConfirmLabelKey,
        string cancelLabelKey = DefaultCancelLabelKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(question);
        ArgumentNullException.ThrowIfNull(confirm);

        Close(ConfirmationOutcome.Superseded, raise: false);
        this.onConfirm = confirm;
        this.onClosed = closed;
        Pending = new PendingConfirmation(question, confirmLabelKey, cancelLabelKey, this.time.GetUtcNow());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>同步动作的便捷重载。</summary>
    public void Request(
        string question,
        Action confirm,
        Action<ConfirmationOutcome>? closed = null,
        string confirmLabelKey = DefaultConfirmLabelKey,
        string cancelLabelKey = DefaultCancelLabelKey)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        Request(
            question,
            () =>
            {
                confirm();
                return Task.CompletedTask;
            },
            closed,
            confirmLabelKey,
            cancelLabelKey);
    }

    /// <summary>
    /// 确认：先清掉待确认状态，再执行动作——动作里再提问（下一件事）不会被当成这一件。
    /// 没有待确认的事、或者已经超时，返回 false，什么也不做。
    /// </summary>
    public async Task<bool> ConfirmAsync()
    {
        if (Pending is null || ExpireIfDue())
        {
            return false;
        }

        Func<Task> action = this.onConfirm!;
        Close(ConfirmationOutcome.Confirmed, raise: true);
        await action().ConfigureAwait(true);
        return true;
    }

    /// <summary>取消。没有待确认的事返回 false（Esc 就交给外壳去退一级）。</summary>
    public bool Cancel()
    {
        if (Pending is null)
        {
            return false;
        }

        Close(ConfirmationOutcome.Cancelled, raise: true);
        return true;
    }

    /// <summary>界面节拍：到时没答就自动取消。</summary>
    public void Tick() => ExpireIfDue();

    private bool ExpireIfDue()
    {
        if (Pending is null || this.time.GetUtcNow() - Pending.RequestedAt < Timeout)
        {
            return false;
        }

        Close(ConfirmationOutcome.Expired, raise: true);
        return true;
    }

    private void Close(ConfirmationOutcome outcome, bool raise)
    {
        if (Pending is null)
        {
            return;
        }

        Action<ConfirmationOutcome>? closed = this.onClosed;
        Pending = null;
        this.onConfirm = null;
        this.onClosed = null;
        closed?.Invoke(outcome);
        if (raise)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
