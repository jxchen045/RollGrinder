using System;

namespace RollGrinder.App.Interaction;

/// <summary>
/// 长按 0.6 秒 = 打开 / 编辑这一行（最终稿 F8）。
///
/// 手指按下后不动（移动不超过 <see cref="MoveTolerance"/>）满 0.6 秒就算长按，只触发一次；
/// 中途移动（在扫动翻页）或抬起都作废。戴手套的手指会抖，容差给到 12 px。
/// 纯逻辑：时刻与坐标由界面层喂进来，单测直接验证。
/// Windows 自带的"长按当右键"由窗口关掉（Stylus.IsPressAndHoldEnabled=False），两者不打架。
/// </summary>
public sealed class LongPressDetector
{
    /// <summary>按多久算长按。</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(0.6);

    /// <summary>按住期间允许的抖动（DIP）。</summary>
    public const double MoveTolerance = 12.0;

    private DateTimeOffset downAt;
    private double downX;
    private double downY;
    private bool armed;

    /// <summary>这一次按下已经触发过长按：抬起时不要再当成普通点击。</summary>
    public bool Fired { get; private set; }

    /// <summary>按下。</summary>
    public void Down(double x, double y, DateTimeOffset at)
    {
        this.downAt = at;
        this.downX = x;
        this.downY = y;
        this.armed = true;
        Fired = false;
    }

    /// <summary>移动：超出容差就作废（人在扫动）。</summary>
    public void Move(double x, double y)
    {
        if (this.armed && Math.Sqrt(Math.Pow(x - this.downX, 2) + Math.Pow(y - this.downY, 2)) > MoveTolerance)
        {
            this.armed = false;
        }
    }

    /// <summary>抬起或被打断。</summary>
    public void Up() => this.armed = false;

    /// <summary>计时检查：满 0.6 秒且没动过，返回 true（一次按下只返回一次）。</summary>
    public bool Poll(DateTimeOffset now)
    {
        if (!this.armed || now - this.downAt < Threshold)
        {
            return false;
        }

        this.armed = false;
        Fired = true;
        return true;
    }
}
