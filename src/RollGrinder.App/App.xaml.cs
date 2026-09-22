using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RollGrinder.App.Localization;
using RollGrinder.App.Printing;
using RollGrinder.App.Views;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;

namespace RollGrinder.App;

/// <summary>
/// WPF 应用外壳。宿主由 <see cref="Program"/> 建好后注入，这里只负责显示主窗口。
/// </summary>
public partial class App : Application
{
    private readonly IHost host;

    private AutoReportPrinter? printer;

    public App(IHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        this.printer?.Dispose();
        base.OnExit(e);
    }
}
