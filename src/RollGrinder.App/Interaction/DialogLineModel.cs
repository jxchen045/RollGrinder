using System;

namespace RollGrinder.App.Interaction;

/// <summary>对话行这一刻在说什么（决定底色与字重）。</summary>
public enum DialogLineKind
{
    /// <summary>什么也没说。</summary>
    Idle = 0,

    /// <summary>说明：当前格的含义、单位、范围，或本画面的操作提示。</summary>
    Hint = 1,

    /// <summary>消息：动作已发出、已保存……几秒后自己消失。</summary>
    Info = 2,

    /// <summary>这个键为什么按不了：没权限 / 运行中 / 缺映射 / 前置条件。</summary>
    Reason = 3,

    /// <summary>错误：输入超出范围、保存失败……</summary>
    Error = 4,

    /// <summary>等人确认的问题：淡黄底粗体（最终稿 4.1）。</summary>
    Question = 5,
}

/// <summary>
/// 对话行（最终稿 4.1、7.1 DialogLine）：说明、消息、灰键原因、确认问题的统一出口，
/// 取代悬停提示（触摸屏没有悬停）和各页自己的说明行。
///
/// 优先级：问题 &gt; 临时消息（消息、原因、错误，几秒后消失）&gt; 说明。
/// 纯逻辑，时钟由 <see cref="TimeProvider"/> 给，外壳的界面节拍调 <see cref="Tick"/>。
/// </summary>
public sealed class DialogLineModel
{
    /// <summary>临时消息默认停留多久（最终稿 5.4："已发出"3 秒后消失）。</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);

    private readonly TimeProvider time;
    private string? question;
    private string? transient;
    private DialogLineKind transientKind;
    private DateTimeOffset transientUntil;
    private string? hint;

    public DialogLineModel(TimeProvider time)
    {
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>显示的内容变了。</summary>
    public event EventHandler? Changed;

    /// <summary>现在显示的字。</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>现在显示的是哪一类。</summary>
    public DialogLineKind Kind { get; private set; }

    /// <summary>常驻说明（当前格、当前画面）。null 清掉。</summary>
    public void SetHint(string? text)
    {
        this.hint = string.IsNullOrEmpty(text) ? null : text;
        Update();
    }

    /// <summary>待确认的问题。null 表示答完了。</summary>
    public void SetQuestion(string? text)
    {
        this.question = string.IsNullOrEmpty(text) ? null : text;
        Update();
    }

    /// <summary>显示一条临时消息（消息、原因、错误），到时自己消失。</summary>
    public void Show(string text, DialogLineKind kind = DialogLineKind.Info, TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (kind is DialogLineKind.Idle or DialogLineKind.Question or DialogLineKind.Hint)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Transient messages are Info, Reason or Error.");
        }

        this.transient = text;
        this.transientKind = kind;
        this.transientUntil = this.time.GetUtcNow() + (duration ?? DefaultDuration);
        Update();
    }

    /// <summary>界面节拍：临时消息到时撤掉。</summary>
    public void Tick()
    {
        if (this.transient is not null && this.time.GetUtcNow() >= this.transientUntil)
        {
            this.transient = null;
            Update();
        }
    }

    private void Update()
    {
        (string text, DialogLineKind kind) = this.question is not null
            ? (this.question, DialogLineKind.Question)
            : this.transient is not null
                ? (this.transient, this.transientKind)
                : this.hint is not null
                    ? (this.hint, DialogLineKind.Hint)
                    : (string.Empty, DialogLineKind.Idle);

        if (text == Text && kind == Kind)
        {
            return;
        }

        Text = text;
        Kind = kind;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
