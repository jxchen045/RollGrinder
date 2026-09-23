using System;
using System.Collections.Generic;

namespace RollGrinder.App.Diagnostics;

/// <summary>
/// 判断界面线程上的未处理异常还该不该兜住。
///
/// 平常一个按钮抛了异常，兜住、转成报警、界面接着用，这是对的。
/// 但异常若出在布局或渲染里，兜住之后下一帧还会再抛——兜住反而把界面变成一个
/// 每秒几百次的空转死循环，而这块工控机同时在跑 SINUMERIK Operate。
/// 所以短时间内连续抛太多次就判定为"风暴"，不再兜住，让进程记完日志退出：
/// 上位机退出不影响这支辊磨完（最高原则），卡死空转却会拖累 Operate。
///
/// 纯逻辑，不引用 WPF，便于单测。
/// </summary>
public sealed class ExceptionStormDetector
{
    private readonly int maxCount;
    private readonly TimeSpan window;
    private readonly Queue<DateTimeOffset> recent = new();

    /// <param name="maxCount">窗口内最多兜住几次。</param>
    /// <param name="window">统计窗口。</param>
    public ExceptionStormDetector(int maxCount, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        this.maxCount = maxCount;
        this.window = window;
    }

    /// <summary>记一次异常。</summary>
    /// <returns>还可以兜住返回 true；已成风暴返回 false。</returns>
    public bool TryAbsorb(DateTimeOffset nowUtc)
    {
        while (this.recent.Count > 0 && nowUtc - this.recent.Peek() > this.window)
        {
            this.recent.Dequeue();
        }

        this.recent.Enqueue(nowUtc);
        return this.recent.Count <= this.maxCount;
    }
}
