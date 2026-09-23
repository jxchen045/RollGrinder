using System;
using System.Collections.Generic;
using System.Printing;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;

namespace RollGrinder.App.Printing;

/// <summary>
/// 把服务层丢进打印队列的报表打出来。
///
/// 服务层知道**什么时候该打**（作业下发完、记录收尾完），这里知道**怎么打**：
/// 排版、默认打印机、纸张。中间隔着 <see cref="IReportPrintQueue"/>，两边不互相引用。
///
/// **不弹对话框。** 勾了"打印磨前数据"的意思就是"到时候自己打一张"，
/// 中间跳一个窗口出来等人按确定，就成了磨削流程里的一个阻塞点。
///
/// **打不出来只报一条提示级报警，绝不抛。** 打印是顺带做的：没接打印机、
/// 没纸、打印机离线，机床照样把这支辊磨完（最高原则）。
/// </summary>
public sealed class AutoReportPrinter : IDisposable
{
    /// <summary>打不出来时报出来的资源键。</summary>
    public const string PrintFailedResourceKey = "Alarm_ReportPrintFailed";

    private readonly IReportPrintQueue queue;
    private readonly IStringLocalizer localizer;
    private readonly IAlarmSink alarms;
    private readonly Dispatcher dispatcher;

    private bool disposed;

    public AutoReportPrinter(
        IReportPrintQueue queue, IStringLocalizer localizer, IAlarmSink alarms, Dispatcher dispatcher)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        this.queue.Enqueued += OnEnqueued;

        // 订上之前可能已经攒了几张（启动早期下发的作业），先取一次。
        PrintPending();
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.queue.Enqueued -= OnEnqueued;
    }

    /// <summary>投递可能来自后台线程；排版与打印必须回到界面线程。</summary>
    private void OnEnqueued(object? sender, EventArgs e)
    {
        if (this.dispatcher.CheckAccess())
        {
            PrintPending();
            return;
        }

        this.dispatcher.BeginInvoke(PrintPending);
    }

    private void PrintPending()
    {
        foreach (GrindingReport report in this.queue.Drain())
        {
            Print(report);
        }
    }

    private void Print(GrindingReport report)
    {
        try
        {
            FlowDocument document = ReportDocumentBuilder.Build(report, this.localizer);

            // 不问操作员：直接用系统默认打印机。
            InteractionScope.DocumentOutput.Print(document, this.localizer[report.TitleResourceKey], askOperator: false);
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException)
        {
            // 没接打印机、离线、没纸：说一声就行，不影响这支辊。
            this.alarms.Raise(
                AlarmSeverity.Warning, PrintFailedResourceKey, ex.Message, AlarmCodes.ReportNotPrinted);
        }
    }
}
