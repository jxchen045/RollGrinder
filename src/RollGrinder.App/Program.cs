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
using RollGrinder.App.Interaction;
using RollGrinder.App.Navigation;
using RollGrinder.App.SelfTest;
using RollGrinder.App.ViewModels;
using RollGrinder.App.Views;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data;
using RollGrinder.Services;
using RollGrinder.Services.Calibration;
using RollGrinder.Services.Session;
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
        SelfTestOptions selfTest;
        try
        {
            options = AppOptions.Parse(args, AppContext.BaseDirectory);
            selfTest = SelfTestOptions.Parse(args, AppContext.BaseDirectory);
        }
        catch (ArgumentException ex)
        {
            // 日志尚未建立，只能写标准错误。
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        // 自检两道闸（只许假机床、只许专用数据目录）必须在碰数据目录之前查。
        SelfTestRunner? selfTestRunner = null;
        if (selfTest.Enabled)
        {
            string? refusal = SelfTestOptions.Refuse(options.Gateway, options.DataDirectory);
            if (refusal is not null)
            {
                Console.Error.WriteLine(refusal);
                return 2;
            }

            selfTestRunner = PrepareSelfTest(selfTest, options);
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

            // 库是空的就种一个制造商级账号，且**不带口令**——首次登录时由现场自己设一个。
            // 出厂默认口令不会一直留在机器上。
            host.Services.GetRequiredService<IUserDirectory>()
                .EnsureSeedAccountAsync(CancellationToken.None).GetAwaiter().GetResult();

            // 现场标定值：库里一条都没有也照样起得来，取全套默认值，
            // 但默认值不是机床数字——装机时必须在设置页里逐项标定。
            host.Services.GetRequiredService<ICalibrationService>()
                .LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

            var application = new App(host, selfTestRunner is null ? null : selfTestRunner.RunAsync);
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

    /// <summary>
    /// 自检准备：给数据目录打上专用标记，把文件对话框与打印换成自动应答。
    /// </summary>
    private static SelfTestRunner PrepareSelfTest(SelfTestOptions selfTest, AppOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        File.WriteAllText(
            Path.Combine(options.DataDirectory, SelfTestOptions.DataMarkerFileName),
            "This data directory belongs to the HMI self-test. Never point the production HMI here.");

        string output = selfTest.OutputDirectory ?? Path.Combine(options.DataDirectory, "selftest");
        var interaction = new AutoAnswerInteraction(Path.Combine(output, "files"), Path.Combine(output, "prints"));
        InteractionScope.SetCurrent(interaction, interaction);
        return new SelfTestRunner(selfTest, options, interaction, output);
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
        builder.Services.AddSingleton<Navigator>();
        builder.Services.AddSingleton<INavigator>(provider => provider.GetRequiredService<Navigator>());

        // 六个主界面。顺序不重要，外壳按 PageKey 索引。
        builder.Services.AddSingleton<PageViewModelBase, AutoGrindingViewModel>();
        builder.Services.AddSingleton<PageViewModelBase, ProfileViewModel>();
        builder.Services.AddSingleton<PageViewModelBase, StepsViewModel>();
        builder.Services.AddSingleton<PageViewModelBase, RecordsViewModel>();
        builder.Services.AddSingleton<PageViewModelBase, ManualViewModel>();
        builder.Services.AddSingleton<PageViewModelBase, DiagnosticsViewModel>();
        builder.Services.AddSingleton<PageViewModelBase, SettingsViewModel>();

        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<ShellWindow>();

        return builder.Build();
    }
}
