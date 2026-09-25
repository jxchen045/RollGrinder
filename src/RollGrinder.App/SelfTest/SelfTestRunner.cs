using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RollGrinder.App.ViewModels;
using RollGrinder.App.Views;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.App.SelfTest;

/// <summary>一组相关的用例。</summary>
internal interface ISelfTestSuite
{
    /// <summary>套件名（英文标识，写进日志）。</summary>
    string Name { get; }

    /// <summary>跑一遍。每一步都经 <see cref="SelfTestHarness.StepAsync"/> 记录，这里不必自己兜异常。</summary>
    Task RunAsync(SelfTestHarness harness);
}

/// <summary>
/// 自检总调度：按范围与网关排出套件清单，逐个跑，收尾写汇总，返回进程退出码。
/// 单个套件崩了只记一条 FAIL，接着跑下一个——一次运行尽量把问题都暴露出来。
/// </summary>
internal sealed class SelfTestRunner
{
    /// <summary>整轮的上限：超过就放弃，免得挂死的自检一直占着机器。</summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(20);

    private readonly SelfTestOptions options;
    private readonly IAppOptions appOptions;
    private readonly AutoAnswerInteraction interaction;
    private readonly string outputDirectory;

    public SelfTestRunner(SelfTestOptions options, IAppOptions appOptions, AutoAnswerInteraction interaction, string outputDirectory)
    {
        this.options = options;
        this.appOptions = appOptions;
        this.interaction = interaction;
        this.outputDirectory = outputDirectory;
    }

    public async Task<int> RunAsync(ShellWindow window, IServiceProvider services)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        using var recorder = new SelfTestRecorder(this.outputDirectory, this.options.Label, CollectEnvironment(window), startedAt);
        var harness = new SelfTestHarness(
            window, services.GetService(typeof(ShellViewModel)) as ShellViewModel ?? throw new InvalidOperationException("ShellViewModel missing"),
            services, recorder, this.interaction, this.outputDirectory);

        // 等主窗口把用户名列表拉进来（Loaded 里异步做的）。
        await harness.WaitUntilAsync(() => harness.Shell.KnownUserNames.Count > 0, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        await harness.SettleAsync(500).ConfigureAwait(true);

        // 主窗口被外部关掉时当场写汇总：进程随即退出，下面的循环不会再有机会收尾。
        void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e) =>
            recorder.AbortOnExternalClose(DateTimeOffset.UtcNow);
        window.Closing += OnWindowClosing;

        string? abortReason = null;
        var watch = Stopwatch.StartNew();
        foreach (ISelfTestSuite suite in PlanSuites())
        {
            harness.Suite = suite.Name;
            recorder.Note("suite " + suite.Name);
            try
            {
                await suite.RunAsync(harness).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // 用例本身写坏了（不是被测软件的问题）也照实记下，接着跑。
                await harness.StepAsync("Suite", "Crashed", _ => throw new SelfTestAssertionException(ex.ToString())).ConfigureAwait(true);
            }

            await harness.RecoverAsync().ConfigureAwait(true);
            if (watch.Elapsed > MaxDuration)
            {
                abortReason = string.Create(CultureInfo.InvariantCulture, $"exceeded {MaxDuration.TotalMinutes:0} minutes");
                break;
            }
        }

        foreach (string produced in this.interaction.Produced)
        {
            recorder.Note("produced " + produced);
        }

        SelfTestSummary summary = recorder.Complete(DateTimeOffset.UtcNow, abortReason);
        window.Closing -= OnWindowClosing;
        return SelfTestRecorder.ExitCodeFor(summary);
    }

    /// <summary>按范围与网关排出要跑的套件。顺序有意义：先登录，全流程在记录页之前。</summary>
    private IEnumerable<ISelfTestSuite> PlanSuites()
    {
        bool offline = this.appOptions.IsOffline;
        yield return new SessionSuite(signInOnly: this.options.Scope == SelfTestScope.Render);

        if (this.options.Scope == SelfTestScope.Render)
        {
            yield return new RenderSuite();
            yield break;
        }

        yield return new NavigationSuite();
        yield return new PageSweepSuite();
        yield return new ProfileSuite();
        yield return new StepsSuite();
        yield return new SettingsSuite();
        if (!offline)
        {
            yield return new ManualSuite();
            yield return new FullFlowSuite();
        }

        yield return new RecordsSuite();
        if (!offline)
        {
            yield return new DiagnosticsSuite();
        }

        yield return new SessionTailSuite();
    }

    private IReadOnlyDictionary<string, string> CollectEnvironment(Window window)
    {
        Assembly assembly = typeof(SelfTestRunner).Assembly;
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        return new Dictionary<string, string>
        {
            ["app.version"] = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "?",
            ["os"] = RuntimeInformation.OSDescription,
            ["os.arch"] = RuntimeInformation.OSArchitecture.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["cpu.count"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            ["culture.ui"] = CultureInfo.CurrentUICulture.Name,
            ["screen.primary"] = Invariant($"{SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0} DIP"),
            ["screen.dpiScale"] = Invariant($"{dpi.DpiScaleX:0.##}"),
            ["window"] = Invariant($"{window.ActualWidth:0}x{window.ActualHeight:0} DIP, {window.WindowState}"),
            ["touch.devices"] = Tablet.TabletDevices.Count.ToString(CultureInfo.InvariantCulture),
            ["gateway"] = this.appOptions.Gateway.ToString(),
            ["sim.speed"] = this.appOptions.SimulationSpeed.ToString(CultureInfo.InvariantCulture),
            ["offline"] = this.appOptions.IsOffline.ToString(),
            ["scope"] = this.options.Scope.ToString(),
            ["dir.data"] = this.appOptions.DataDirectory,
            ["dir.config"] = this.appOptions.ConfigDirectory,
            ["commit"] = Environment.GetEnvironmentVariable("ROLLGRINDER_COMMIT") ?? "?",
        };
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
