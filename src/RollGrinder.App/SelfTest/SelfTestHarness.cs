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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly List<Exception> uiExceptions = new();
    private long alarmMark;

    public SelfTestHarness(
        ShellWindow window,
        ShellViewModel shell,
        IServiceProvider services,
        SelfTestRecorder recorder,
        AutoAnswerInteraction interaction,
        string outputDirectory)
    {
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
            screenshot = TryScreenshot(Invariant($"{sequence:D4}-{Suite}-{testCase}-{step}"));
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

    /// <summary>按第 n 个功能键（0 起，等同 F(n+1)）。异步命令会等它跑完。</summary>
    public async Task PressKeyAsync(int index, TimeSpan? timeout = null)
    {
        FunctionKeyViewModel key = Shell.FunctionKeys[index];
        Shell.PressFunctionKey(index);
        if (key.Command is IAsyncRelayCommand asyncCommand)
        {
            await WaitUntilAsync(() => !asyncCommand.IsRunning, timeout ?? TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        }

        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>按标签资源键找到功能键并按下；找不到或是灰的就断言失败。</summary>
    public async Task PressKeyAsync(StepContext context, string labelResourceKey, TimeSpan? timeout = null)
    {
        int index = IndexOfKey(labelResourceKey);
        context.Check(index >= 0, "function key " + labelResourceKey + " is not on the bar");
        context.Check(Shell.FunctionKeys[index].IsEnabled, "function key " + labelResourceKey + " is disabled");
        await PressKeyAsync(index, timeout).ConfigureAwait(true);
    }

    /// <summary>功能条上某个键的位置；没有返回 -1。</summary>
    public int IndexOfKey(string labelResourceKey)
    {
        for (int i = 0; i < Shell.FunctionKeys.Count; i++)
        {
            if (Shell.FunctionKeys[i].LabelResourceKey == labelResourceKey)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>导航槽（第 8 键）当前的标签资源键。</summary>
    public string NavigationKeyLabel => Shell.FunctionKeys[PageViewModelBase.PageFunctionKeyCount].LabelResourceKey;

    /// <summary>按导航槽（F8）。</summary>
    public Task PressNavigationKeyAsync() => PressKeyAsync(PageViewModelBase.PageFunctionKeyCount);

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
    /// 切到某个区域，走 Ctrl+n 的入口。遇到"未保存"确认框就放弃修改离开，并在备注里记一笔。
    /// </summary>
    public async Task GoToAsync(PageKey area, StepContext? context = null)
    {
        AreaMenuItemViewModel item = Shell.AreaMenuItems.First(i => i.Key == area);
        item.Command.Execute(null);
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
        if (Shell.IsLeaveConfirmOpen)
        {
            Shell.CancelLeaveCommand.Execute(null);
        }

        if (Shell.IsUserAdminOpen)
        {
            Shell.CloseUserAdminCommand.Execute(null);
        }

        if (Shell.IsAreaMenuOpen)
        {
            Shell.CloseAreaMenuCommand.Execute(null);
        }

        StepsViewModel steps = Page<StepsViewModel>();
        if (steps.IsProgramLibraryOpen)
        {
            steps.CloseProgramLibraryCommand.Execute(null);
        }

        if (steps.IsProfileLibraryOpen)
        {
            steps.CloseProfileLibraryCommand.Execute(null);
        }

        ProfileViewModel profile = Page<ProfileViewModel>();
        if (profile.IsLibraryOpen)
        {
            profile.CloseLibraryCommand.Execute(null);
        }

        for (int i = 0; i < 3 && Shell.CurrentPage.ActiveSubViewKey is not null; i++)
        {
            await PressNavigationKeyAsync().ConfigureAwait(true);
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
                double needed = MeasureText(text);
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

    private static double MeasureText(TextBlock text)
    {
        var formatted = new FormattedText(
            text.Text,
            CultureInfo.CurrentUICulture,
            text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
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
            CheckPair(issues, label, "hover", button.Foreground, Controls.ButtonStates.GetHoverBackground(button), Controls.ContrastMath.NormalText);
            CheckPair(issues, label, "pressed", button.Foreground, Controls.ButtonStates.GetPressedBackground(button), Controls.ContrastMath.NormalText);
        }

        foreach (TextBlock text in FindVisuals<TextBlock>(Window).Where(t => t.IsVisible && t.ActualWidth > 0 && !string.IsNullOrWhiteSpace(t.Text)))
        {
            if (text.Foreground is not SolidColorBrush foreground || EffectiveBackground(text) is not Color background)
            {
                continue;
            }

            double opacity = CumulativeOpacity(text) * foreground.Opacity;
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
    public string? TryScreenshot(string name)
    {
        try
        {
            if (Window.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1)
            {
                return null;
            }

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);

            // 先铺窗口底色再画内容。RenderTargetBitmap 只画元素本身，窗口背景不在里面，
            // 透明的缝隙存成 JPEG 会变成黑块——第二轮截图里那些"黑条"就是这么来的，屏幕上并没有。
            var backdrop = new DrawingVisual();
            using (DrawingContext dc = backdrop.RenderOpen())
            {
                dc.DrawRectangle(Window.Background ?? Brushes.White, null, new Rect(0, 0, bitmap.Width, bitmap.Height));
            }

            bitmap.Render(backdrop);
            bitmap.Render(root);

            var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string fileName = Sanitize(name) + ".jpg";
            using FileStream stream = File.Create(Path.Combine(this.screenshotDirectory, fileName));
            encoder.Save(stream);
            return "screenshots/" + fileName;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            this.recorder.Note("screenshot failed: " + ex.Message);
            return null;
        }
    }

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
