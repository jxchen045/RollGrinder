using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.App.Interaction;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;
using RollGrinder.App.Views;
using RollGrinder.Services.Alarms;

namespace RollGrinder.App.SelfTest;

/// <summary>断言不成立：这一步记 FAIL。</summary>
internal sealed class SelfTestAssertionException : Exception
{
    public SelfTestAssertionException(string message)
        : base(message)
    {
    }
}

/// <summary>前置条件不满足：这一步记 SKIP。</summary>
internal sealed class SelfTestSkipException : Exception
{
    public SelfTestSkipException(string message)
        : base(message)
    {
    }
}

/// <summary>一张截图是哪一类：决定在什么截图策略下会存。</summary>
public enum ShotKind
{
    /// <summary>失败的步骤：总是截。</summary>
    Failure = 0,

    /// <summary>关键画面：每页、子视图、菜单态、标了"截图"的步骤。</summary>
    Key = 1,

    /// <summary>细节：巡检里每按一个键一张。</summary>
    Detail = 2,
}

/// <summary>一条版面问题：说明，以及它在画布上的位置（截局部图用）。</summary>
public sealed record LayoutIssue(string Text, Rect Bounds);

/// <summary>一步的选项。</summary>
/// <param name="Screenshot">这一步做完截一张图（失败时总会截）。</param>
/// <param name="ExpectedAlarms">这一步**预期**会报的报警资源键：它们出现不算失败。"*" 表示任何报警都在预期内。</param>
/// <param name="TimeoutSeconds">超时秒数。</param>
/// <param name="Tolerant">
/// 宽容模式（逐键巡检用）：在空白状态下乱按，业务校验类报警本来就该出现，都算预期内；
/// 只有"未预期的错误"（<see cref="AlarmLog.UnexpectedFailureResourceKey"/>）与界面异常才算失败。
/// </param>
internal sealed record StepOptions(
    bool Screenshot = false,
    IReadOnlyCollection<string>? ExpectedAlarms = null,
    double TimeoutSeconds = 30,
    bool Tolerant = false)
{
    public static StepOptions Default { get; } = new();

    public static StepOptions Shot { get; } = new(Screenshot: true);

    public static StepOptions Expect(params string[] alarmKeys) => new(ExpectedAlarms: alarmKeys);
}

/// <summary>一步里的上下文：写备注、做断言、声明跳过、记警告。</summary>
internal sealed class StepContext
{
    private readonly List<string> notes = new();

    public string Detail => string.Join("; ", this.notes);

    /// <summary>这一步里记下的警告（不判失败，但整步记 WARN）。</summary>
    public List<string> Warnings { get; } = new();

    public void Note(string text) => this.notes.Add(text);

    public void Warn(string text) => Warnings.Add(text);

    public void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new SelfTestAssertionException(message);
        }
    }

    public void Skip(string reason) => throw new SelfTestSkipException(reason);
}

/// <summary>
/// 自检的驾驶台。用例只描述"做什么、应该怎样"，这里统一负责：
/// 超时、断言、捕获界面异常、归类这一步新冒出来的报警、失败自动截图、写记录。
///
/// 操作一律走和人一样的路：功能键走 F1–F8 的入口（<see cref="ShellViewModel.PressFunctionKey"/>），
/// 换页走 Ctrl+n 的入口（区域菜单项的命令）——测到的就是操作员会碰到的那条路径。
/// </summary>
internal sealed partial class SelfTestHarness
{
    private readonly SelfTestRecorder recorder;
    private readonly IAlarmLog alarmLog;
    private readonly string screenshotDirectory;
    private readonly SelfTestOptions options;

    /// <summary>已存的截图：像素指纹 → 相对路径。画面没变的那一步指向同一张，不另存。</summary>
    private readonly Dictionary<string, string> shotsByFingerprint = new(StringComparer.Ordinal);
    private readonly HashSet<string> shotIssues = new(StringComparer.Ordinal);
    private int issueShots;
    private readonly List<Exception> uiExceptions = new();
    private long alarmMark;

    public SelfTestHarness(
        ShellWindow window,
        ShellViewModel shell,
        IServiceProvider services,
        SelfTestRecorder recorder,
        AutoAnswerInteraction interaction,
        string outputDirectory,
        SelfTestOptions? options = null)
    {
        this.options = options ?? SelfTestOptions.Disabled;
        Window = window;
        Shell = shell;
        Services = services;
        this.recorder = recorder;
        Interaction = interaction;
        OutputDirectory = outputDirectory;
        this.alarmLog = services.GetRequiredService<IAlarmLog>();
        this.screenshotDirectory = Path.Combine(outputDirectory, "screenshots");
        Directory.CreateDirectory(this.screenshotDirectory);

        // App 的兜底会把异常转成报警并吞掉；这里另外记一份，归到当前这一步头上。
        Application.Current.DispatcherUnhandledException += (_, e) => this.uiExceptions.Add(e.Exception);
        this.alarmMark = CurrentMaxAlarmId();
    }

    public ShellWindow Window { get; }

    public ShellViewModel Shell { get; }

    public IServiceProvider Services { get; }

    public AutoAnswerInteraction Interaction { get; }

    public string OutputDirectory { get; }

    /// <summary>当前套件名，写进每一步。</summary>
    public string Suite { get; set; } = string.Empty;

    /// <summary>取某一页的视图模型。</summary>
    public T Page<T>()
        where T : PageViewModelBase =>
        Services.GetServices<PageViewModelBase>().OfType<T>().Single();

    /// <summary>执行一步并记录。</summary>
    public async Task<StepStatus> StepAsync(
        string testCase, string step, Func<StepContext, Task> action, StepOptions? options = null)
    {
        options ??= StepOptions.Default;
        int sequence = this.recorder.NextSequence();
        var context = new StepContext();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        StepStatus status = StepStatus.Pass;
        var failures = new List<string>();
        string? exceptionText = null;
        this.uiExceptions.Clear();

        try
        {
            Task work = action(context);
            Task finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(options.TimeoutSeconds))).ConfigureAwait(true);
            if (finished != work)
            {
                status = StepStatus.Fail;
                failures.Add(Invariant($"timed out after {options.TimeoutSeconds:0} s"));
            }
            else
            {
                await work.ConfigureAwait(true);
            }

            await SettleAsync().ConfigureAwait(true);
        }
        catch (SelfTestSkipException skip)
        {
            status = StepStatus.Skip;
            failures.Add(skip.Message);
        }
        catch (SelfTestAssertionException assertion)
        {
            status = StepStatus.Fail;
            failures.Add(assertion.Message);
        }
        catch (Exception ex)
        {
            status = StepStatus.Fail;
            failures.Add(ex.GetType().Name + ": " + ex.Message);
            exceptionText = ex.ToString();
        }

        watch.Stop();

        // 这一步里冒出来的报警：错误级且不在预期内 → FAIL；提示级不在预期内 → WARN。
        var alarmTexts = new List<string>();
        foreach (AlarmEntry alarm in NewAlarms())
        {
            bool expected = (options.ExpectedAlarms is { } keys
                    && (keys.Contains("*") || keys.Contains(alarm.MessageResourceKey)))
                || (options.Tolerant && alarm.MessageResourceKey != AlarmLog.UnexpectedFailureResourceKey);
            alarmTexts.Add(Invariant($"{alarm.Severity}:{alarm.MessageResourceKey}{(string.IsNullOrEmpty(alarm.Detail) ? string.Empty : "(" + alarm.Detail + ")")}{(expected ? " [expected]" : string.Empty)}"));
            if (expected || status == StepStatus.Skip)
            {
                continue;
            }

            if (alarm.Severity == AlarmSeverity.Error)
            {
                status = StepStatus.Fail;
                failures.Add("unexpected error alarm " + alarm.MessageResourceKey);
            }
            else if (alarm.Severity == AlarmSeverity.Warning && status == StepStatus.Pass)
            {
                status = StepStatus.Warn;
            }
        }

        if (context.Warnings.Count > 0)
        {
            failures.Add("WARN " + string.Join("; ", context.Warnings));
            if (status == StepStatus.Pass)
            {
                status = StepStatus.Warn;
            }
        }

        if (this.uiExceptions.Count > 0)
        {
            status = StepStatus.Fail;
            failures.Add(Invariant($"{this.uiExceptions.Count} unhandled UI exception(s)"));
            exceptionText = string.Join(Environment.NewLine + "---" + Environment.NewLine, this.uiExceptions.Select(e => e.ToString()))
                + (exceptionText is null ? string.Empty : Environment.NewLine + "---" + Environment.NewLine + exceptionText);
        }

        string? screenshot = null;
        if (options.Screenshot || status == StepStatus.Fail)
        {
            screenshot = TryScreenshot(
                Invariant($"{sequence:D4}-{Suite}-{testCase}-{step}"),
                status == StepStatus.Fail ? ShotKind.Failure : ShotKind.Key);
        }

        string detail = string.Join("; ", new[] { context.Detail }.Concat(failures).Where(s => !string.IsNullOrEmpty(s)));
        this.recorder.Record(new StepResult(
            sequence, Suite, testCase, step, status, watch.ElapsedMilliseconds, detail, alarmTexts, exceptionText, screenshot, startedAt));
        return status;
    }

    /// <summary>写一行备注。</summary>
    public void Note(string text) => this.recorder.Note(text);

    /// <summary>让界面把排队的活干完：绑定、布局、渲染，再给定时刷新一两拍。</summary>
    public async Task SettleAsync(int extraMilliseconds = 150)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Window.UpdateLayout();
        if (extraMilliseconds > 0)
        {
            await Task.Delay(extraMilliseconds).ConfigureAwait(true);
        }

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>等某个条件成立。</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, int pollMilliseconds = 200)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(pollMilliseconds).ConfigureAwait(true);
        }

        return condition();
    }

    /// <summary>外壳的对话行与待确认（最终稿 D5）。</summary>
    public ShellInteraction ShellInteraction => Services.GetRequiredService<ShellInteraction>();

    /// <summary>按一个软键（与点屏同一条路：灰键不执行、在对话行说原因）。异步命令会等它跑完。</summary>
    public async Task PressAsync(FunctionKeyViewModel key, TimeSpan? timeout = null)
    {
        Shell.PressKeyCommand.Execute(key);
        if (key.Command is IAsyncRelayCommand asyncCommand)
        {
            await WaitUntilAsync(() => !asyncCommand.IsRunning, timeout ?? TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        }

        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>按第 n 个横键（0 起，等同 F(n+1)），按当前页的全部功能组算（跨横键分页）。</summary>
    public Task PressKeyAsync(int index, TimeSpan? timeout = null) =>
        PressAsync(Shell.CurrentPage.FunctionKeys[index], timeout);

    /// <summary>右侧竖键里某个键的位置；没有返回 -1。</summary>
    public int IndexOfVerticalKey(string labelResourceKey)
    {
        IReadOnlyList<FunctionKeyViewModel> keys = Shell.VerticalKeys;
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i].LabelResourceKey == labelResourceKey)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>这个竖键现在按得下去吗（与外壳的判断一致）。</summary>
    public bool IsVerticalKeyUsable(int index)
    {
        IReadOnlyList<FunctionKeyViewModel> keys = Shell.VerticalKeys;
        return index >= 0 && index < keys.Count && keys[index].IsUsable;
    }

    /// <summary>按标签资源键找到竖键并按下（等同 Shift+F(n+1)）；找不到或是灰的就断言失败。异步命令会等它跑完。</summary>
    public async Task PressVerticalKeyAsync(StepContext context, string labelResourceKey, TimeSpan? timeout = null)
    {
        int index = IndexOfVerticalKey(labelResourceKey);
        context.Check(index >= 0, "vertical key " + labelResourceKey + " is not on the bar");
        context.Check(IsVerticalKeyUsable(index), "vertical key " + labelResourceKey + " is unavailable: " + Shell.VerticalKeys[index].ReasonText);
        await PressAsync(Shell.VerticalKeys[index], timeout).ConfigureAwait(true);
    }

    /// <summary>按标签资源键找到横键并按下；找不到或是灰的就断言失败。</summary>
    public async Task PressKeyAsync(StepContext context, string labelResourceKey, TimeSpan? timeout = null)
    {
        int index = IndexOfKey(labelResourceKey);
        context.Check(index >= 0, "function key " + labelResourceKey + " is not on the bar");
        context.Check(IsKeyUsable(index), "function key " + labelResourceKey + " is unavailable: " + Shell.CurrentPage.FunctionKeys[index].ReasonText);
        await PressKeyAsync(index, timeout).ConfigureAwait(true);
    }

    /// <summary>
    /// 这个横键现在按得下去吗——和外壳按键时的判断一样（锁、权限、急停、阻断原因、命令可执行）。
    /// </summary>
    public bool IsKeyUsable(int index) =>
        index >= 0 && index < Shell.CurrentPage.FunctionKeys.Count && Shell.CurrentPage.FunctionKeys[index].IsUsable;

    /// <summary>当前页横键（功能组）里某个键的位置；没有返回 -1。</summary>
    public int IndexOfKey(string labelResourceKey)
    {
        var keys = Shell.CurrentPage.FunctionKeys;
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i].LabelResourceKey == labelResourceKey)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>有没有一件等着确认的事（对话行在问、竖键 7 / 8 是取消 / 确认）。</summary>
    public bool HasPendingConfirmation => ShellInteraction.Confirmations.Pending is not null;

    /// <summary>对话行正在问的那句话。</summary>
    public string PendingQuestion => ShellInteraction.Confirmations.Pending?.Question ?? string.Empty;

    /// <summary>按"✓ 确认"（竖键 8 / 回车）：断言确实有事在等确认，并等它做完。</summary>
    public async Task ConfirmAsync(StepContext context)
    {
        context.Check(HasPendingConfirmation, "expected a pending confirmation");
        await ShellInteraction.Confirmations.ConfirmAsync().ConfigureAwait(true);
        await SettleAsync(300).ConfigureAwait(true);
    }

    /// <summary>按"✕ 取消"（竖键 7 / Esc）。</summary>
    public async Task CancelConfirmationAsync()
    {
        ShellInteraction.Confirmations.Cancel();
        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>对话行现在写的字。</summary>
    public string DialogLineText => ShellInteraction.DialogLine.Text;

    /// <summary>"« 返回"（路径条左端 / Esc 的最后一级）。</summary>
    public async Task BackAsync()
    {
        Shell.BackCommand.Execute(null);
        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>用执行异步命令的方式调一个页面命令，并等它跑完。</summary>
    public async Task RunAsync(System.Windows.Input.ICommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(parameter).ConfigureAwait(true);
        }
        else
        {
            command.Execute(parameter);
        }

        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 切到某个画面（可带功能组），走导航器——和左栏、区域菜单、页面里的跳转同一条路。
    /// 遇到"未保存"确认框就放弃修改离开，并在备注里记一笔。
    /// </summary>
    public async Task GoToAsync(PageKey page, StepContext? context = null, string? groupKey = null)
    {
        Services.GetRequiredService<INavigator>().GoTo(page, groupKey);
        await SettleAsync().ConfigureAwait(true);

        if (Shell.IsLeaveConfirmOpen)
        {
            context?.Note("leave-confirm shown, discarded");
            await RunAsync(Shell.DiscardAndLeaveCommand).ConfigureAwait(true);
        }
    }

    /// <summary>把界面收拾回"页面根部、没有浮层"的干净状态，让下一个用例不受上一个影响。</summary>
    public async Task RecoverAsync()
    {
        ShellInteraction.Confirmations.Cancel();
        if (Shell.IsLeaveConfirmOpen)
        {
            Shell.CancelLeaveCommand.Execute(null);
        }

        if (Shell.IsUserAdminOpen)
        {
            Shell.CloseUserAdminCommand.Execute(null);
        }

        Shell.IsUserMenuOpen = false;
        Shell.Keypad.Close();
        Shell.Help.Close();
        Shell.CloseAreaMenuCommand.Execute(null);

        // 起名字的框（另存为、复制、重命名）：按"取消"收掉，不存。
        foreach (PageViewModelBase page in Services.GetServices<PageViewModelBase>())
        {
            page.TryDismissPrompt();
        }

        // 竖键停在子菜单里：收回根层，下一个用例从页面的根层开始。
        Shell.CurrentPage.ResetVerticalMenu();

        for (int i = 0; i < 3 && Shell.CurrentPage.ActiveSubViewKey is not null; i++)
        {
            await BackAsync().ConfigureAwait(true);
        }

        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 按界面上的按钮文字找到它并"点一下"——走 UI 自动化的 Invoke 通道，和鼠标点击触发同一个 Click。
    /// 用于那些不在功能条上、只在页面里的按钮（例如报表预览上的"打印"）。
    /// </summary>
    /// <returns>找到并点了返回 true。</returns>
    public async Task<bool> ClickButtonAsync(string content)
    {
        Button? button = FindVisuals<Button>(Window)
            .FirstOrDefault(b => b.IsVisible && b.IsEnabled && b.Content is string text && text == content);
        if (button is null)
        {
            return false;
        }

        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        await SettleAsync(300).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// 扫一遍当前可见的文字，找缺失的资源（本地化器把缺的键显示成 !Key!）。
    /// </summary>
    public IReadOnlyList<string> FindMissingResources()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (TextBlock block in FindVisuals<TextBlock>(Window).Where(b => b.IsVisible))
        {
            foreach (Match match in MissingResourcePattern().Matches(block.Text ?? string.Empty))
            {
                missing.Add(match.Groups[1].Value);
            }
        }

        return missing.ToList();
    }

    /// <summary>
    /// 找出文字被截断的按钮。两种截法都查：
    /// 1. 按钮自己太窄：文字需要的宽度大于按钮里那个 TextBlock 实际分到的宽度；
    /// 2. 按钮被父容器裁掉：横向 StackPanel 里排不下，超出了外层容器的边界。
    /// 滚动区里的内容本来就会超出可视范围，不算。
    /// </summary>
    public IReadOnlyList<string> FindClippedButtons()
    {
        var clipped = new List<string>();
        foreach (Button button in FindVisuals<Button>(Window).Where(b => b.IsVisible && b.ActualWidth > 0))
        {
            foreach (TextBlock text in FindVisuals<TextBlock>(button).Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)))
            {
                // 允许换行的字（竖向软键）：换成两行不算截断，只要最长的一个词放得下。
                double needed = text.TextWrapping == TextWrapping.NoWrap ? MeasureText(text) : MeasureLongestWord(text);
                if (needed > text.ActualWidth + 1.5)
                {
                    clipped.Add(Invariant($"'{text.Text}' needs {needed:0} px, has {text.ActualWidth:0}"));
                }
            }

            if (OverflowsContainer(button) is { } overflow)
            {
                clipped.Add(Invariant($"'{ButtonLabel(button)}' cut off by its container ({overflow})"));
            }
        }

        // 按钮以外的字：不换行、也没设省略号，却装不下——那就是被硬截断了（设了省略号的是有意为之）。
        foreach (TextBlock text in FindVisuals<TextBlock>(Window).Where(t => t.IsVisible && t.ActualWidth > 0
                     && !string.IsNullOrEmpty(t.Text) && t.TextWrapping == TextWrapping.NoWrap && t.TextTrimming == TextTrimming.None))
        {
            if (FindAncestor<Button>(text) is not null || FindAncestor<ComboBox>(text) is not null || FindAncestor<DataGrid>(text) is not null)
            {
                continue;
            }

            double needed = MeasureText(text);
            if (needed > text.ActualWidth + 1.5)
            {
                clipped.Add(Invariant($"text '{Shorten(text.Text)}' needs {needed:0} px, has {text.ActualWidth:0}"));
            }
        }

        return clipped.Distinct().ToList();
    }

    private static double MeasureText(TextBlock text) => MeasureText(text, text.Text);

    /// <summary>换行只在空格处断（不换行空格连着的算一个词），最长的那个词就是最少要的宽度。</summary>
    private static double MeasureLongestWord(TextBlock text) =>
        text.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => MeasureText(text, word)).DefaultIfEmpty(0.0).Max();

    private static double MeasureText(TextBlock text, string content) =>
        Format(text, content).WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;

    private static FormattedText Format(TextBlock text, string content)
    {
        return new FormattedText(
            content,
            CultureInfo.CurrentUICulture,
            text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
    }

    /// <summary>元素有没有超出某一级容器（或窗口）的边界；超出了返回"哪边、多少像素"。滚动区里的不算。</summary>
    private string? OverflowsContainer(FrameworkElement element)
    {
        Rect bounds = element.TransformToAncestor(Window).TransformBounds(new Rect(element.RenderSize));
        DependencyObject? current = VisualTreeHelper.GetParent(element);
        while (current is not null && current != Window)
        {
            if (current is ScrollContentPresenter or ScrollViewer)
            {
                return null;
            }

            if (current is Border or Grid or DockPanel && current is FrameworkElement container && container.ActualWidth > 0)
            {
                Rect box = container.TransformToAncestor(Window).TransformBounds(new Rect(container.RenderSize));
                string? side = bounds.Right - box.Right > 2 ? Invariant($"right {bounds.Right - box.Right:0} px")
                    : box.Left - bounds.Left > 2 ? Invariant($"left {box.Left - bounds.Left:0} px")
                    : bounds.Bottom - box.Bottom > 2 ? Invariant($"bottom {bounds.Bottom - box.Bottom:0} px")
                    : box.Top - bounds.Top > 2 ? Invariant($"top {box.Top - bounds.Top:0} px")
                    : null;
                if (side is not null)
                {
                    return side;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// 某个具名界面元素此刻是否**真的**显示在屏幕上（它和它的所有上级都可见、且有尺寸）。
    /// 视图模型说"浮层开着"不算数——登录框曾被错嵌进一个平时隐藏的容器里，命令层全过、屏幕上却什么都没有。
    /// </summary>
    public bool IsShownOnScreen(string elementName) =>
        Window.FindName(elementName) is FrameworkElement element
        && element.IsVisible
        && element.ActualWidth > 0
        && element.ActualHeight > 0;

    /// <summary>
    /// 找出没跟屏幕缩放走的曲线图：图表库按物理像素作画，缩放倍数没设上时，
    /// 高分屏上所有刻度字、线条都只有设计尺寸的几分之一。
    /// </summary>
    public IReadOnlyList<string> FindUnscaledPlots()
    {
        var issues = new List<string>();
        foreach (ScottPlot.WPF.WpfPlot plot in FindVisuals<ScottPlot.WPF.WpfPlot>(Window).Where(p => p.IsVisible && p.ActualWidth > 0))
        {
            double scale = VisualTreeHelper.GetDpi(plot).DpiScaleX;
            if (Math.Abs(plot.Plot.ScaleFactor - scale) > 0.01)
            {
                issues.Add(Invariant($"{plot.Name}: ScaleFactor {plot.Plot.ScaleFactor:0.##} but display scale {scale:0.##}"));
            }
        }

        return issues;
    }

    /// <summary>
    /// 找出按钮上"接不住鼠标"的地方：在按钮里取中心和四角内缩 3 px 共 5 个点做命中测试，
    /// 命中落到了按钮的上级元素（而不是按钮自己或它里面的东西），说明这一点是空洞。
    ///
    /// 空洞正是"鼠标停在按钮上闪烁"的成因：鼠标在空洞上时 IsMouseOver 在真/假之间来回翻，
    /// 悬停色一帧有一帧没有。被别的元素（浮层、提示）盖住的点不算——那是另一个元素接住了鼠标。
    /// </summary>
    public IReadOnlyList<string> FindHitTestHoles()
    {
        var issues = new List<string>();
        foreach (Button button in FindVisuals<Button>(Window).Where(b => b.IsVisible && b.IsEnabled && b.ActualWidth > 8 && b.ActualHeight > 8))
        {
            double w = button.ActualWidth;
            double h = button.ActualHeight;
            Point[] probes =
            {
                new(w / 2, h / 2), new(3, 3), new(w - 3, 3), new(3, h - 3), new(w - 3, h - 3),
            };

            foreach (Point probe in probes)
            {
                Point inWindow = button.TranslatePoint(probe, Window);
                if (Window.InputHitTest(inWindow) is not DependencyObject hit)
                {
                    continue;
                }

                bool inside = ReferenceEquals(hit, button) || (hit is Visual v && v.IsDescendantOf(button));
                bool fellThrough = !inside && button.IsDescendantOf(hit as Visual ?? Window);
                if (fellThrough)
                {
                    issues.Add(ButtonLabel(button) + " @" + probe.X.ToString("0", CultureInfo.InvariantCulture)
                        + "," + probe.Y.ToString("0", CultureInfo.InvariantCulture));
                    break;
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// 版面体检之一：被布局裁掉的元素。元素要的尺寸比分到的格子大时，WPF 把多出来的部分裁掉
    /// （<see cref="LayoutInformation.GetLayoutClip"/> 不为空且比元素小）——砂轮简图下半截、挤扁的输入框都是这样。
    /// 只报最外面那一层：上级已经被裁了，里面的不重复报。
    /// </summary>
    public IReadOnlyList<LayoutIssue> FindLayoutClipped()
    {
        var found = new List<(FrameworkElement Element, LayoutIssue Issue)>();
        foreach (FrameworkElement element in FindVisuals<FrameworkElement>(CanvasRoot)
                     .Where(e => e.IsVisible && e.ActualWidth >= 4 && e.ActualHeight >= 4 && !IsInsideOpaqueControl(e)))
        {
            if (LayoutInformation.GetLayoutClip(element) is not { } clip)
            {
                continue;
            }

            Rect kept = clip.IsEmpty() ? Rect.Empty : clip.Bounds;
            double lostWidth = kept.IsEmpty ? element.ActualWidth : element.ActualWidth - kept.Width;
            double lostHeight = kept.IsEmpty ? element.ActualHeight : element.ActualHeight - kept.Height;
            if (lostWidth <= 2 && lostHeight <= 2)
            {
                continue;
            }

            if (CanvasBounds(element) is { } bounds)
            {
                found.Add((element, new LayoutIssue(
                    Invariant($"{Describe(element)} cut by its slot: {element.ActualWidth:0}x{element.ActualHeight:0}, shows {Math.Max(0, element.ActualWidth - lostWidth):0}x{Math.Max(0, element.ActualHeight - lostHeight):0}"),
                    bounds)));
            }
        }

        return found.Where(f => !found.Any(o => !ReferenceEquals(o.Element, f.Element) && f.Element.IsDescendantOf(o.Element)))
            .Select(f => f.Issue).ToList();
    }

    /// <summary>
    /// 版面体检之二：控件被别的东西盖住了一部分。在控件里取 9 个点做命中测试，
    /// 有的点落在它自己身上、有的点落在不相干的元素上，就是半遮半露（整个被盖住的算有意隐藏，不报）。
    /// 滚动区外面的点不算（列表滚到一半的那一行本来就只露半截）。
    /// </summary>
    public IReadOnlyList<LayoutIssue> FindPartlyCovered()
    {
        var issues = new List<LayoutIssue>();
        foreach (Control control in FindVisuals<Control>(CanvasRoot).Where(c =>
                     c is ButtonBase or TextBoxBase or ListBoxItem or ComboBox
                     && c.IsVisible && c.IsEnabled && c.ActualWidth > 12 && c.ActualHeight > 12 && !IsInsideOpaqueControl(c)))
        {
            Rect? visible = VisibleRect(control);
            if (visible is not { } view || CanvasBounds(control) is not { } bounds)
            {
                continue;
            }

            double w = control.ActualWidth;
            double h = control.ActualHeight;
            int inside = 0;
            DependencyObject? coverer = null;
            foreach (Point probe in new Point[]
                     {
                         new(w / 2, h / 2), new(4, 4), new(w - 4, 4), new(4, h - 4), new(w - 4, h - 4),
                         new(w / 2, 4), new(w / 2, h - 4), new(4, h / 2), new(w - 4, h / 2),
                     })
            {
                Point onCanvas = control.TranslatePoint(probe, CanvasRoot);
                if (!view.Contains(onCanvas) || Window.InputHitTest(control.TranslatePoint(probe, Window)) is not DependencyObject hit)
                {
                    continue;
                }

                if (ReferenceEquals(hit, control) || (hit is Visual v && v.IsDescendantOf(control)))
                {
                    inside++;
                }
                else if (hit is Visual other && !control.IsDescendantOf(other))
                {
                    coverer ??= hit;
                }
            }

            if (inside > 0 && coverer is not null)
            {
                issues.Add(new LayoutIssue(Describe(control) + " partly covered by " + Describe(NearestElement(coverer)), bounds));
            }
        }

        return issues;
    }

    /// <summary>
    /// 版面体检之三：一大块面板盖在另一块上面，自己却没有底色——缝隙里透出下面那一页的控件
    /// （选辊形 / 选程序子视图两张卡片之间的那道缝就是这样）。只看同一个 Grid 里的两块大面板，模板内部的不看。
    /// </summary>
    public IReadOnlyList<LayoutIssue> FindSeeThroughOverlays()
    {
        var issues = new List<LayoutIssue>();
        foreach (Grid grid in FindVisuals<Grid>(CanvasRoot).Where(g => g.IsVisible && g.TemplatedParent is null))
        {
            List<(FrameworkElement Element, Rect Bounds)> panes = grid.Children.OfType<FrameworkElement>()
                .Where(c => c.IsVisible && c.IsHitTestVisible && c.ActualWidth >= 200 && c.ActualHeight >= 120)
                .Select(c => (Element: c, Bounds: CanvasBounds(c)))
                .Where(p => p.Bounds is not null)
                .Select(p => (p.Element, p.Bounds!.Value))
                .ToList();
            for (int top = 1; top < panes.Count; top++)
            {
                if (HasOpaqueBackground(panes[top].Element))
                {
                    continue;
                }

                for (int under = 0; under < top; under++)
                {
                    Rect overlap = Rect.Intersect(panes[top].Bounds, panes[under].Bounds);
                    Rect below = panes[under].Bounds;
                    if (!overlap.IsEmpty && overlap.Width * overlap.Height >= 0.8 * below.Width * below.Height)
                    {
                        issues.Add(new LayoutIssue(
                            Describe(panes[top].Element) + " lies over " + Describe(panes[under].Element) + " without a background (the page below shows through)",
                            panes[top].Bounds));
                        break;
                    }
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// 版面体检之四：软键大小、对齐。同一排（上沿对齐）的软键应一样宽、一样高；
    /// 同一列（左沿对齐）的软键应一样宽。差 4 px 以上就报（横键条右端的"›"曾比别的键窄一截）。
    /// </summary>
    public IReadOnlyList<LayoutIssue> FindMisalignedKeys()
    {
        if (Window.TryFindResource("SoftKeyButton") is not Style softKey)
        {
            return Array.Empty<LayoutIssue>();
        }

        var keys = FindVisuals<Button>(CanvasRoot)
            .Where(b => b.IsVisible && b.ActualWidth > 0 && IsBasedOn(b.Style, softKey))
            .Select(b => (Button: b, Bounds: CanvasBounds(b)))
            .Where(k => k.Bounds is not null)
            .Select(k => (k.Button, Bounds: k.Bounds!.Value))
            .ToList();
        var issues = new List<LayoutIssue>();

        void Compare(IEnumerable<IGrouping<long, (Button Button, Rect Bounds)>> lines, string what, bool checkHeight)
        {
            foreach (var line in lines.Where(g => g.Count() >= 3))
            {
                double width = Median(line.Select(k => k.Bounds.Width));
                double height = Median(line.Select(k => k.Bounds.Height));
                foreach ((Button button, Rect bounds) in line)
                {
                    if (Math.Abs(bounds.Width - width) > 4 || (checkHeight && Math.Abs(bounds.Height - height) > 4))
                    {
                        issues.Add(new LayoutIssue(
                            Invariant($"soft key '{ButtonLabel(button)}' is {bounds.Width:0}x{bounds.Height:0}, others in its {what} {width:0}x{height:0}"),
                            bounds));
                    }
                }
            }
        }

        Compare(keys.GroupBy(k => (long)Math.Round(k.Bounds.Top / 4)), "row", checkHeight: true);
        Compare(keys.GroupBy(k => (long)Math.Round(k.Bounds.Left / 4)), "column", checkHeight: false);
        return issues.DistinctBy(i => i.Text).ToList();
    }

    /// <summary>
    /// 给一条版面问题截一张局部小图（问题四周留 60 px，红框圈出），同一条问题整轮只截一次，最多 <see cref="MaxIssueShots"/> 张。
    /// </summary>
    /// <returns>存下的相对路径；没截返回 null。</returns>
    public string? TryIssueShot(string name, LayoutIssue issue)
    {
        if (!this.shotIssues.Add(issue.Text) || this.issueShots >= MaxIssueShots)
        {
            return null;
        }

        try
        {
            Rect region = issue.Bounds;
            region.Inflate(60, 60);
            System.Windows.Media.Imaging.BitmapSource? bitmap = Controls.WindowCapture.RenderRegion(Window, region, this.options.ShotScale, issue.Bounds);
            if (bitmap is null)
            {
                return null;
            }

            string fileName = Sanitize(Invariant($"layout-{++this.issueShots:000}-{name}")) + ".jpg";
            Controls.WindowCapture.Write(bitmap, Path.Combine(this.screenshotDirectory, fileName), this.options.JpegQuality);
            return "screenshots/" + fileName;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            this.recorder.Note("issue screenshot failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>版面问题局部图每轮最多几张。</summary>
    public const int MaxIssueShots = 40;

    private FrameworkElement CanvasRoot => Window.FindName("Canvas") as FrameworkElement ?? (FrameworkElement)Window.Content;

    /// <summary>元素在画布上的位置（画布坐标，与截图一致）；不在画布里（弹出层）返回 null。</summary>
    private Rect? CanvasBounds(FrameworkElement element)
    {
        try
        {
            return element.IsDescendantOf(CanvasRoot)
                ? element.TransformToAncestor(CanvasRoot).TransformBounds(new Rect(element.RenderSize))
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>元素此刻露在外面的那块（被滚动区、裁边的上级截过以后），画布坐标。</summary>
    private Rect? VisibleRect(FrameworkElement element)
    {
        if (CanvasBounds(element) is not { } visible)
        {
            return null;
        }

        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current is not null && current != CanvasRoot; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement container && (container is ScrollContentPresenter || container.ClipToBounds)
                && CanvasBounds(container) is { } box)
            {
                visible.Intersect(box);
                if (visible.IsEmpty)
                {
                    return null;
                }
            }
        }

        return visible;
    }

    /// <summary>表格、图表、下拉框、文本框、滚动条的内部零件不查（那是控件自己的事，查了全是误报）。</summary>
    private static bool IsInsideOpaqueControl(DependencyObject element)
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is DataGrid or ComboBox or TextBoxBase or ScrollBar
                or ScottPlot.WPF.WpfPlot)
            {
                return true;
            }
        }

        return element is ScottPlot.WPF.WpfPlot || FindAncestor<ScottPlot.WPF.WpfPlot>(element) is not null;
    }

    private static bool HasOpaqueBackground(FrameworkElement element)
    {
        Brush? background = element switch
        {
            Panel panel => panel.Background,
            Border border => border.Background,
            Control control => control.Background,
            _ => null,
        };
        if (background is null && element is UserControl { Content: FrameworkElement content })
        {
            return HasOpaqueBackground(content);
        }

        return background is SolidColorBrush { Color.A: > 0 } || (background is not null and not SolidColorBrush);
    }

    private static bool IsBasedOn(Style? style, Style target)
    {
        for (Style? current = style; current is not null; current = current.BasedOn)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
        }

        return false;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static FrameworkElement? NearestElement(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement fe)
            {
                return fe;
            }
        }

        return null;
    }

    /// <summary>"哪一页：什么控件 '写着什么'"，报告里一眼认得出。</summary>
    private static string Describe(FrameworkElement? element)
    {
        if (element is null)
        {
            return "?";
        }

        string text = element switch
        {
            TextBlock block => block.Text,
            Button button => ButtonLabel(button),
            TextBox box => box.Text,
            _ => FindVisuals<TextBlock>(element).Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty,
        };
        string kind = string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.GetType().Name + "#" + element.Name;
        UserControl? view = element as UserControl ?? FindAncestor<UserControl>(element);
        string where = view is null ? "Shell" : view.GetType().Name;
        return string.IsNullOrWhiteSpace(text) ? where + ": " + kind : where + ": " + kind + " '" + Shorten(text) + "'";
    }

    /// <summary>
    /// 找出对比度不够的字与按钮状态（WCAG：正文 4.5:1，大字与禁用 3:1）。
    /// 按钮查常态 / 悬停 / 按下（或禁用），字查它相对实际背景的对比度——
    /// 半透明的底色、上级元素的透明度都先合成再算，看到的就是屏幕上的样子。
    /// </summary>
    public IReadOnlyList<string> FindLowContrast()
    {
        var issues = new List<string>();
        foreach (Button button in FindVisuals<Button>(Window).Where(b => b.IsVisible && b.ActualWidth > 0))
        {
            string label = ButtonLabel(button);
            if (!button.IsEnabled)
            {
                CheckPair(issues, label, "disabled", button.TryFindResource("Brush.DisabledText") as Brush,
                    button.TryFindResource("Brush.DisabledFill") as Brush, Controls.ContrastMath.LargeTextOrGraphics);
                continue;
            }

            CheckPair(issues, label, "normal", button.Foreground, button.Background, Controls.ContrastMath.NormalText);
            CheckPair(issues, label, "pressed", button.Foreground, Controls.ButtonStates.GetPressedBackground(button), Controls.ContrastMath.NormalText);
        }

        foreach (TextBlock text in FindVisuals<TextBlock>(Window).Where(t => t.IsVisible && t.ActualWidth > 0 && !string.IsNullOrWhiteSpace(t.Text)))
        {
            if (text.Foreground is not SolidColorBrush foreground || EffectiveBackground(text) is not Color background)
            {
                continue;
            }

            double opacity = CumulativeOpacity(text) * foreground.Opacity;
            if (opacity < 0.05)
            {
                // 完全透明 = 此刻不显示（如日期框已有日期时的占位提示，由控件的视觉状态隐藏）。
                // 看不见的字谈不上对比度；不跳过的话会被算成 1:1 误报。
                continue;
            }

            Color fg = foreground.Color;
            (byte r, byte g, byte b) = Controls.ContrastMath.Blend(
                (byte)Math.Round(fg.A * opacity), fg.R, fg.G, fg.B, background.R, background.G, background.B);
            double ratio = Controls.ContrastMath.Ratio(r, g, b, background.R, background.G, background.B);
            double required = text.IsEnabled
                ? Controls.ContrastMath.RequiredFor(text.FontSize, text.FontWeight.ToOpenTypeWeight() >= 600)
                : Controls.ContrastMath.LargeTextOrGraphics;
            if (ratio < required)
            {
                issues.Add(Invariant($"text '{Shorten(text.Text)}' {ratio:0.0}:1 < {required}:1 (#{r:X2}{g:X2}{b:X2} on #{background.R:X2}{background.G:X2}{background.B:X2})"));
            }
        }

        return issues.Distinct().ToList();
    }

    private static void CheckPair(List<string> issues, string label, string state, Brush? foreground, Brush? background, double required)
    {
        if (foreground is not SolidColorBrush fg || background is not SolidColorBrush bg || bg.Color.A < 255)
        {
            return;
        }

        double ratio = Controls.ContrastMath.Ratio(fg.Color.R, fg.Color.G, fg.Color.B, bg.Color.R, bg.Color.G, bg.Color.B);
        if (ratio < required)
        {
            issues.Add(Invariant($"button '{label}' {state} {ratio:0.0}:1 < {required}:1"));
        }
    }

    /// <summary>字背后实际的底色：往上找第一个有底色的元素，半透明的层层叠上去。</summary>
    private Color? EffectiveBackground(DependencyObject element)
    {
        var layers = new List<Color>();
        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            Brush? brush = current switch
            {
                Panel panel => panel.Background,
                Border border => border.Background,
                Control control => control.Background,
                _ => null,
            };

            if (brush is SolidColorBrush solid && solid.Color.A > 0 && solid.Opacity > 0)
            {
                Color color = solid.Color;
                layers.Add(color);
                if (color.A == 255 && solid.Opacity >= 1.0)
                {
                    break;
                }
            }
        }

        if (layers.Count == 0)
        {
            return (Window.Background as SolidColorBrush)?.Color;
        }

        Color result = layers[^1].A == 255 ? layers[^1] : (Window.Background as SolidColorBrush)?.Color ?? Colors.White;
        for (int i = layers.Count - (layers[^1].A == 255 ? 2 : 1); i >= 0; i--)
        {
            Color top = layers[i];
            (byte r, byte g, byte b) = Controls.ContrastMath.Blend(top.A, top.R, top.G, top.B, result.R, result.G, result.B);
            result = Color.FromRgb(r, g, b);
        }

        return result;
    }

    private static double CumulativeOpacity(DependencyObject element)
    {
        double opacity = 1.0;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui)
            {
                opacity *= ui.Opacity;
            }
        }

        return opacity;
    }

    private static string ButtonLabel(Button button) =>
        button.Content as string
        ?? FindVisuals<TextBlock>(button).Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
        ?? button.Name;

    private static string Shorten(string text) => text.Length > 24 ? text[..24] + "…" : text;

    /// <summary>可视树里某一类元素（深度优先）。</summary>
    public static IEnumerable<T> FindVisuals<T>(DependencyObject root)
        where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject current = stack.Pop();
            if (current is T match)
            {
                yield return match;
            }

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = count - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
    }

    [GeneratedRegex(@"!([A-Za-z][A-Za-z0-9_]*)!")]
    private static partial Regex MissingResourcePattern();

    /// <summary>截屏。失败不影响测试结论，只在备注里说明。</summary>
    public string? TryScreenshot(string name, ShotKind kind = ShotKind.Key)
    {
        // 策略：失败的一定截；关键画面看 --selftest-shots；巡检里每个键那种细节图只在 all 时截。
        bool wanted = kind switch
        {
            ShotKind.Failure => true,
            ShotKind.Key => this.options.Shots >= ScreenshotPolicy.Key,
            _ => this.options.Shots >= ScreenshotPolicy.All,
        };
        if (!wanted || (kind != ShotKind.Failure && this.SavedShots >= this.options.MaxShots))
        {
            if (wanted)
            {
                SkippedShots++;
            }

            return null;
        }

        try
        {
            System.Windows.Media.Imaging.BitmapSource? bitmap = Controls.WindowCapture.Render(Window, this.options.ShotScale);
            if (bitmap is null)
            {
                return null;
            }

            string fingerprint = Controls.WindowCapture.Fingerprint(bitmap);
            if (this.shotsByFingerprint.TryGetValue(fingerprint, out string? same))
            {
                DuplicateShots++;
                return same;
            }

            string fileName = Sanitize(name) + ".jpg";
            Controls.WindowCapture.Write(bitmap, Path.Combine(this.screenshotDirectory, fileName), this.options.JpegQuality);
            string relative = "screenshots/" + fileName;
            this.shotsByFingerprint[fingerprint] = relative;
            SavedShots++;
            return relative;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            this.recorder.Note("screenshot failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>存下的截图张数。</summary>
    public int SavedShots { get; private set; }

    /// <summary>画面与已存的一样、没另存的张数。</summary>
    public int DuplicateShots { get; private set; }

    /// <summary>到了上限没存的张数。</summary>
    public int SkippedShots { get; private set; }

    private IReadOnlyList<AlarmEntry> NewAlarms()
    {
        IReadOnlyList<AlarmEntry> fresh = this.alarmLog.Snapshot()
            .Where(a => a.Id > this.alarmMark)
            .OrderBy(a => a.Id)
            .ToList();
        this.alarmMark = Math.Max(this.alarmMark, CurrentMaxAlarmId());
        return fresh;
    }

    private long CurrentMaxAlarmId()
    {
        IReadOnlyList<AlarmEntry> all = this.alarmLog.Snapshot();
        return all.Count == 0 ? this.alarmMark : Math.Max(this.alarmMark, all.Max(a => a.Id));
    }

    private static string Sanitize(string text)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            builder.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        }

        return builder.Length > 100 ? builder.ToString(0, 100) : builder.ToString();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
