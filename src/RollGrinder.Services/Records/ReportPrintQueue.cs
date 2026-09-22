using System;
using System.Collections.Generic;

namespace RollGrinder.Services.Records;

/// <summary>
/// "请打一张"的投递口。
///
/// 服务层知道**什么时候该打**（作业下发完、记录收尾完），但不知道怎么打——
/// 打印机、纸张、排版都是界面层的事。所以服务层只把出好的报表丢进这里，
/// 界面层取走去打。两边不互相引用。
///
/// **打印失败不影响磨削。** 这条队列上的事全是"顺带做的"：
/// 没接打印机、没纸、上位机被关掉，机床照样把这支辊磨完（最高原则）。
/// </summary>
public interface IReportPrintQueue
{
    /// <summary>请打一张。</summary>
    void Enqueue(GrindingReport report);

    /// <summary>
    /// 队里有东西了。只是一声招呼，不带内容——取报表一律走
    /// <see cref="Drain"/>，这样"取"是一个原子动作，同一张不会被打两遍。
    /// 可能来自后台线程。
    /// </summary>
    event EventHandler? Enqueued;

    /// <summary>把队里的全部取走。取过就不在队里了。</summary>
    IReadOnlyList<GrindingReport> Drain();
}

/// <inheritdoc cref="IReportPrintQueue"/>
public sealed class ReportPrintQueue : IReportPrintQueue
{
    private readonly object gate = new();
    private readonly List<GrindingReport> pending = new();

    public event EventHandler? Enqueued;

    public void Enqueue(GrindingReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // 先攒下再打招呼：界面还没订上（启动早期）的话，这一张也不能丢——
        // 不然操作工勾了"打印磨前数据"却什么都没出来。订上之后先 Drain 一次就取到了。
        lock (this.gate)
        {
            this.pending.Add(report);
        }

        Enqueued?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<GrindingReport> Drain()
    {
        lock (this.gate)
        {
            GrindingReport[] drained = this.pending.ToArray();
            this.pending.Clear();
            return drained;
        }
    }
}
