using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RollGrinder.App.Localization;
using RollGrinder.App.ViewModels;
using RollGrinder.App.Views;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data;
using RollGrinder.Services;
using Serilog;

namespace RollGrinder.App;

/// <summary>
/// 组合根：解析命令行、准备目录与配置、装配 DI、启动宿主与界面。
/// 这是整个进程里唯一按网关种类分支的位置（分支本身在 Composition 内）。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppOptions options;
        try
        {
            options = AppOptions.Parse(args, AppContext.BaseDirectory);
        }
        catch (ArgumentException ex)
        {
            // 日志尚未建立，只能写标准错误。
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        IStringLocalizer localizer = new ResxStringLocalizer();
        LocalizationScope.SetCurrent(localizer);

        try
        {
            IReadOnlyList<string> createdConfigFiles = ConfigBootstrapper
                .EnsureConfigurationAsync(options, SampleDirectory(), CancellationToken.None)
                .GetAwaiter().GetResult();

            ConfigureLogging(options);
            foreach (string created in createdConfigFiles)
            {
                Log.Information("Created {ConfigFile} from its sample template", created);
            }

            Log.Information(
                "Starting Roll Grinder HMI, gateway={Gateway}, config={ConfigDirectory}, data={DataDirectory}",
                options.Gateway,
                options.ConfigDirectory,
                options.DataDirectory);

            var configProvider = new JsonMachineConfigProvider(options);
            MachineDescription machine = configProvider.GetMachineAsync(CancellationToken.None).GetAwaiter().GetResult();
            ITagMap tagMap = configProvider.GetTagMapAsync(CancellationToken.None).GetAwaiter().GetResult();
            HmiSettings hmiSettings = JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None).GetAwaiter().GetResult();
            ApplyCulture(hmiSettings);

            var database = new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName));
            int schemaVersion = database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
            Log.Information("Database {DatabaseFile} is at schema version {SchemaVersion}", database.DatabaseFilePath, schemaVersion);

            using IHost host = BuildHost(options, machine, tagMap, hmiSettings, localizer);
            host.Start();

            var application = new App(host);
            application.InitializeComponent();
            int exitCode = application.Run();

            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Roll Grinder HMI failed to start");
            MessageBox.Show(
                localizer.Format("Startup_FailedMessage", ex.Message),
                localizer["Startup_FailedCaption"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static string SampleDirectory() => Path.Combine(AppContext.BaseDirectory, "config");

    private static void ConfigureLogging(IAppOptions options)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                Path.Combine(options.LogDirectory, "rollgrinder-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
    }

    private static void ApplyCulture(HmiSettings settings)
    {
        var culture = CultureInfo.GetCultureInfo(settings.Culture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private static IHost BuildHost(
        AppOptions options,
        MachineDescription machine,
        ITagMap tagMap,
        HmiSettings hmiSettings,
        IStringLocalizer localizer)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();

        builder.Services.AddMachineAccess(options, machine, tagMap);
        builder.Services.AddDomainRegistries();
        builder.Services.AddDataStore(options);
        builder.Services.AddApplicationServices(hmiSettings);
        builder.Services.AddSingleton(localizer);
        builder.Services.AddSingleton<MonitorViewModel>();
        builder.Services.AddSingleton<JobEditorViewModel>();
        builder.Services.AddSingleton<MeasurementViewModel>();
        builder.Services.AddSingleton<RecordsViewModel>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<ShellWindow>();

        return builder.Build();
    }
}
