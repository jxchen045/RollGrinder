using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RollGrinder.App.Views;

namespace RollGrinder.App;

/// <summary>
/// WPF 应用外壳。宿主由 <see cref="Program"/> 建好后注入，这里只负责显示主窗口。
/// </summary>
public partial class App : Application
{
    private readonly IHost host;

    public App(IHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow window = this.host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }
}
