using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RollGrinder.App.Diagnostics;
using RollGrinder.App.Localization;
using RollGrinder.App.Printing;
using RollGrinder.App.Views;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;
using Serilog;

namespace RollGrinder.App;

/// <summary>
/// WPF 应用外壳。宿主由 <see cref="Program"/> 建好后注入，这里只负责显示主窗口。
/// </summary>
public partial class App : Application
{
    private readonly IHost host;

    /// <summary>界面自检（--selftest）：主窗口显示后开跑，跑完以它的返回值作为退出码退出。</summary>
    private readonly Func<ShellWindow, IServiceProvider, Task<int>>? selfTest;

    /// <summary>2 秒内连抛超过 10 次就不再兜住：见 <see cref="ExceptionStormDetector"/>。</summary>
    private readonly ExceptionStormDetector storm = new(maxCount: 10, window: TimeSpan.FromSeconds(2));

    private AutoReportPrinter? printer;

    private IAlarmSink? alarms;

    public App(IHost host, Func<ShellWindow, IServiceProvider, Task<int>>? selfTest = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.selfTest = selfTest;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 界面层统一兜底（架构约束第 10 条）：任何没被页面自己接住的异常都转成报警条目，
        // 并把完整堆栈写进日志——而不是弹一个崩溃框把整个上位机带走。
        this.alarms = this.host.Services.GetRequiredService<IAlarmSink>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        ShellWindow window = this.host.Services.GetRequiredService<ShellWindow>();
        MainWindow = window;

        // 自动打印挂在应用上而不是某一页上：勾了"打印磨后数据"的那张报表，
        // 不该因为操作工当时停在别的页面就打不出来。
        this.printer = new AutoReportPrinter(
            this.host.Services.GetRequiredService<IReportPrintQueue>(),
            this.host.Services.GetRequiredService<IStringLocalizer>(),
            this.host.Services.GetRequiredService<IAlarmSink>(),
            Dispatcher);

        window.Show();

        if (this.selfTest is not null)
        {
            Dispatcher.InvokeAsync(RunSelfTestAsync, DispatcherPriority.ApplicationIdle);
        }
    }

    private async Task RunSelfTestAsync()
    {
        int exitCode;
        try
        {
            exitCode = await this.selfTest!((ShellWindow)MainWindow, this.host.Services).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Self-test runner crashed");
            exitCode = 3;
        }

        Log.Information("Self-test finished with exit code {ExitCode}", exitCode);
        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        this.printer?.Dispose();
        base.OnExit(e);
    }

    /// <summary>界面线程上的异常：记日志、转报警、界面接着用。成了风暴就放手。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (!this.storm.TryAbsorb(DateTimeOffset.UtcNow))
        {
            Log.Fatal(e.Exception, "Unhandled UI exceptions keep recurring; letting the HMI exit instead of spinning");
            return;
        }

        Log.Error(e.Exception, "Unhandled UI exception, converted to an alarm");
        this.alarms?.RaiseException(e.Exception);
        e.Handled = true;
    }

    /// <summary>没人等的后台任务失败了：同样记日志并转报警（切回界面线程登记）。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception, converted to an alarm");
        e.SetObserved();
        Exception exception = e.Exception.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
        Dispatcher.BeginInvoke(() => this.alarms?.RaiseException(exception));
    }

    /// <summary>其他线程上的致命异常：拦不住，只能保证堆栈进了日志。</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "Unhandled exception on a background thread; the HMI is terminating");
        }

        Log.CloseAndFlush();
    }
}
