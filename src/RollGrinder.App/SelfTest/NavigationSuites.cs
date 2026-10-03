using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.SelfTest;

/// <summary>
/// 导航与交互（界面最终稿 4.x）：左栏一键直达每个区域并渲染；区域菜单（F10 / 区域方块）开、选、收；
/// 路径条"« 返回"的三种角色（收菜单、收子视图、任务跳转返回）；脏页离开确认；
/// "问一句再做"的确认 / 取消 / 超时；灰键按下去在对话行说原因；数字键盘；黄色帮助。
/// </summary>
internal sealed class NavigationSuite : ISelfTestSuite
{
    public string Name => "Navigation";

    public async Task RunAsync(SelfTestHarness h)
    {
        ShellViewModel shell = h.Shell;
        PageKey home = shell.CurrentPage.Key;

        foreach (QuickBarItemViewModel item in shell.QuickBarItems.ToList())
        {
            string step = "Quick_" + item.ShortcutNumber.ToString(CultureInfo.InvariantCulture) + "_" + item.Entry.Id;
            if (!item.IsAvailable)
            {
                await h.StepAsync("QuickBar", step, async ctx =>
                {
                    PageKey before = shell.CurrentPage.Key;
                    shell.PressQuickBar(item.ShortcutNumber - 1);
                    await h.SettleAsync();
                    ctx.Check(shell.CurrentPage.Key == before, "an unavailable entry must not navigate");
                    ctx.Check(h.DialogLineText.Length > 0, "an unavailable entry must say why on the dialog line");
                    ctx.Note("unavailable: " + item.UnavailableReason);
                });
                continue;
            }

            await h.StepAsync("QuickBar", step, async ctx =>
            {
                shell.PressQuickBar(item.ShortcutNumber - 1);
                await h.SettleAsync();
                if (shell.IsLeaveConfirmOpen)
                {
                    await h.RunAsync(shell.DiscardAndLeaveCommand);
                }

                ctx.Check(shell.CurrentPage.Area == item.Entry.Area, "expected area " + item.Entry.Area + ", got " + shell.CurrentPage.Area);
                ctx.Check(item.IsCurrent, "the pressed entry should be marked current");
                ctx.Check(shell.PathText.Length > 0, "path bar should not be empty");
                ctx.Check(shell.CurrentPage.FunctionKeys.Count <= 16, "horizontal keys should fit in two pages");
                IReadOnlyList<string> missing = h.FindMissingResources();
                ctx.Check(missing.Count == 0, "missing resources on page: " + string.Join(", ", missing));
                ctx.Note("path=" + shell.PathText);
                WarnClipped(h, ctx);
            }, StepOptions.Shot);
        }

        await h.StepAsync("AreaMenu", "OpenWithAreaTile", async ctx =>
        {
            await h.GoToAsync(home, ctx);
            await h.RunAsync(shell.ToggleAreaMenuCommand);
            ctx.Check(shell.IsAreaMenuOpen, "the area tile / F10 should open the area menu");
            ctx.Check(!shell.IsOverlayOpen, "the area menu must not be a blocking overlay");
            int areaKeys = shell.HorizontalKeys.Count(k => k.Kind is FunctionKeyKind.AreaMenu or FunctionKeyKind.AreaMenuCurrent);
            ctx.Check(areaKeys is >= 1 and <= 8, "one horizontal key per area expected, got " + areaKeys);
            ctx.Check(shell.HorizontalKeys.Count(k => k.Kind == FunctionKeyKind.AreaMenuCurrent) == 1, "exactly one area key should be marked current");
        }, StepOptions.Shot);

        await h.StepAsync("AreaMenu", "ToggleCloses", async ctx =>
        {
            await h.RunAsync(shell.ToggleAreaMenuCommand);
            ctx.Check(!shell.IsAreaMenuOpen, "a second press should close the menu");
            ctx.Check(shell.CurrentPage.Key == home, "closing must not change page");
            ctx.Check(shell.HorizontalKeys.All(k => k.Kind is not (FunctionKeyKind.AreaMenu or FunctionKeyKind.AreaMenuCurrent)), "page keys should be back");
        });

        await h.StepAsync("AreaMenu", "EscapeCloses", async ctx =>
        {
            await h.RunAsync(shell.ToggleAreaMenuCommand);
            shell.PressEscape();
            await h.SettleAsync();
            ctx.Check(!shell.IsAreaMenuOpen, "Esc should close the menu");
            ctx.Check(shell.CurrentPage.Key == home, "Esc on the menu must not also go back a level");
        });

        await h.StepAsync("AreaMenu", "PickRecordsWithAreaKey", async ctx =>
        {
            await h.RunAsync(shell.ToggleAreaMenuCommand);
            FunctionKeyViewModel? records = shell.HorizontalKeys.FirstOrDefault(k => k.LabelResourceKey == AreaCatalog.TitleKey(AreaKey.Records));
            ctx.Check(records is not null, "the area menu should list Records");
            await h.PressAsync(records!);
            ctx.Check(shell.CurrentPage.Area == AreaKey.Records, "the Records area key should open the records area");
            ctx.Check(!shell.IsAreaMenuOpen, "the menu should close after picking");
        });

        await h.StepAsync("AreaMenu", "CommissioningNeedsManufacturer", async ctx =>
        {
            await h.RunAsync(shell.ToggleAreaMenuCommand);
            FunctionKeyViewModel? commissioning = shell.HorizontalKeys.FirstOrDefault(k => k.LabelResourceKey == AreaCatalog.TitleKey(AreaKey.Commissioning));
            ctx.Check(commissioning is not null, "the area menu should list Commissioning (it is menu-only)");
            ctx.Check(shell.QuickBarItems.All(item => item.Entry.Area != AreaKey.Commissioning), "Commissioning must not be on the quick bar");
            ctx.Note("commissioning usable=" + commissioning!.IsUsable);
            await h.RunAsync(shell.CloseAreaMenuCommand);
        });

        // 任务跳转：自动磨削 →「作业」→ 作业页，"« 返回"送回。离线时自动页进不去就跳过。
        await h.StepAsync("TaskJump", "RecordsFromAutoAndBack", async ctx =>
        {
            await h.GoToAsync(PageKey.AutoGrinding, ctx);
            if (shell.CurrentPage.Key != PageKey.AutoGrinding)
            {
                ctx.Skip("the auto page is not reachable (offline)");
            }

            await h.PressKeyAsync(ctx, "Fn_GrindingRecords");
            ctx.Check(shell.CurrentPage.Key == PageKey.Records, "'grinding records' should open the records page");
            ctx.Check(shell.BackText.Length > 0, "the path bar should offer '« back'");
            await h.BackAsync();
            ctx.Check(shell.CurrentPage.Key == PageKey.AutoGrinding, "'« back' should return to the auto page");
        }, StepOptions.Shot);

        await h.StepAsync("SubView", "OpenAndCloseWithBack", async ctx =>
        {
            await h.GoToAsync(PageKey.Records, ctx);
            await h.PressVerticalKeyAsync(ctx, "Vk_QueryAsk");
            ctx.Check(shell.CurrentPage.ActiveSubViewKey is not null, "'query…' should open a sub view");
            ctx.Check(shell.BackText.Length > 0, "the path bar should offer '« back' inside a sub view");
            h.TryScreenshot("subview-records-query");
            await h.BackAsync();
            ctx.Check(shell.CurrentPage.ActiveSubViewKey is null, "'« back' should close the sub view");
            ctx.Check(shell.CurrentPage.Key == PageKey.Records, "closing a sub view must not leave the page");
        });

        await h.StepAsync("LeaveConfirm", "DirtyPageAsksBeforeLeaving", async ctx =>
        {
            await h.GoToAsync(PageKey.Profile, ctx);
            ProfileViewModel profile = h.Page<ProfileViewModel>();
            await h.RunAsync(profile.InsertSegmentCommand);
            ctx.Check(profile.IsDirty, "adding a segment should mark the profile page dirty");
            h.Services.GetRequiredService<INavigator>().GoTo(PageKey.Records);
            await h.SettleAsync();
            ctx.Check(shell.IsLeaveConfirmOpen, "leaving a dirty page should ask first");
            ctx.Check(h.IsShownOnScreen("LeaveConfirmOverlay"), "the leave-confirm dialog must actually be visible on screen");
            h.TryScreenshot("leave-confirm");
            await h.RunAsync(shell.CancelLeaveCommand);
            ctx.Check(shell.CurrentPage.Key == PageKey.Profile && profile.IsDirty, "'keep editing' should stay with the edits");
        });

        await h.StepAsync("LeaveConfirm", "DiscardReallyReverts", async ctx =>
        {
            ProfileViewModel profile = h.Page<ProfileViewModel>();
            int segmentsBefore = profile.Segments.Count;
            h.Services.GetRequiredService<INavigator>().GoTo(PageKey.Records);
            await h.SettleAsync();
            await h.RunAsync(shell.DiscardAndLeaveCommand);
            ctx.Check(shell.CurrentPage.Key == PageKey.Records, "discard should leave");
            ctx.Check(!profile.IsDirty, "discard should clear the dirty flag");
            ctx.Check(profile.Segments.Count == segmentsBefore - 1, Invariant($"discard should really remove the new segment ({segmentsBefore} -> {profile.Segments.Count})"));
        });

        await h.StepAsync("Confirmation", "CancelExpireAndConfirm", async ctx =>
        {
            ShellInteraction interaction = h.ShellInteraction;
            int ran = 0;
            interaction.Ask("self-test question", () => ran++);
            await h.SettleAsync();
            ctx.Check(h.HasPendingConfirmation, "Ask should leave a pending confirmation");
            ctx.Check(shell.VerticalKeys.Count == PageViewModelBase.VerticalKeyCount, "the vertical bar must keep 8 slots");
            ctx.Check(shell.VerticalKeys[6].Kind == FunctionKeyKind.Cancel && shell.VerticalKeys[7].Kind == FunctionKeyKind.Confirm,
                "vertical keys 7 / 8 should become cancel / confirm");
            ctx.Check(h.DialogLineText == "self-test question", "the dialog line should show the question");
            shell.PressEscape();
            await h.SettleAsync();
            ctx.Check(!h.HasPendingConfirmation && ran == 0, "Esc should cancel without running");

            interaction.Ask("self-test question", () => ran++);
            bool expired = await h.WaitUntilAsync(() => !h.HasPendingConfirmation, ConfirmationService.Timeout + System.TimeSpan.FromSeconds(3));
            ctx.Check(expired && ran == 0, "an unanswered question should expire without running");

            interaction.Ask("self-test question", () => ran++);
            ctx.Check(shell.PressEnter(), "Enter should confirm a pending question");
            await h.SettleAsync();
            ctx.Check(ran == 1, "confirm should run the action exactly once");
        });

        await h.StepAsync("SoftKeys", "UnavailableKeySaysWhy", async ctx =>
        {
            await h.GoToAsync(PageKey.ProgramLibrary, ctx);
            LibraryViewModel library = SelfTestNames.Library(h, SelfTestNames.ProgramsGroup);
            library.SelectedEntry = null;
            await h.SettleAsync();
            int open = h.IndexOfVerticalKey("Vk_Open");
            ctx.Check(open >= 0, "'open' should be on the vertical bar");
            ctx.Check(!h.IsVerticalKeyUsable(open), "'open' with nothing selected should be unavailable");
            shell.PressVerticalKey(open);
            await h.SettleAsync();
            ctx.Check(h.DialogLineText.Length > 0, "pressing an unavailable key must say why on the dialog line");
            ctx.Note("reason=" + h.DialogLineText);
        });

        await h.StepAsync("Keypad", "RangeCheckedAndCommitted", async ctx =>
        {
            string? accepted = null;
            shell.Keypad.Open("self-test", "10", 0, 100, 1, text =>
            {
                accepted = text;
                return true;
            });
            ctx.Check(shell.Keypad.IsOpen, "the keypad should open");
            shell.Keypad.Text = "250";
            ctx.Check(!shell.Keypad.Enter() && accepted is null, "an out-of-range value must be refused");
            shell.Keypad.Text = "42.5";
            ctx.Check(shell.Keypad.Enter() && accepted == "42.5", "an in-range value should be committed");
            ctx.Check(!shell.Keypad.IsOpen, "the keypad should close after committing");
            await h.SettleAsync();
        });

        await h.StepAsync("Help", "OpenAndClose", async ctx =>
        {
            await h.GoToAsync(PageKey.Steps, ctx);
            await h.RunAsync(shell.ToggleHelpCommand);
            ctx.Check(shell.Help.IsOpen, "'i help' should open the yellow help");
            ctx.Check(shell.Help.TopicTitle.Length > 0 && !shell.Help.TopicTitle.StartsWith('!'), "help should show a topic");
            ctx.Check(ReferenceEquals(shell.VerticalKeys, shell.Help.Keys), "the vertical bar should switch to help keys");
            h.TryScreenshot("help-steps");
            shell.PressEscape();
            await h.SettleAsync();
            ctx.Check(!shell.Help.IsOpen, "Esc should close help");
        });

        await h.GoToAsync(home);
    }

    /// <summary>
    /// 界面质量检查：按钮文字被截断、内容被容器裁掉、字或按钮状态对比度不够。
    /// 不判失败（不影响功能），但记 WARN 并逐条列出。
    /// </summary>
    internal static void WarnClipped(SelfTestHarness h, StepContext ctx)
    {
        IReadOnlyList<string> clipped = h.FindClippedButtons();
        if (clipped.Count > 0)
        {
            ctx.Warn("clipped: " + string.Join(" | ", clipped));
        }

        IReadOnlyList<string> holes = h.FindHitTestHoles();
        if (holes.Count > 0)
        {
            ctx.Warn(holes.Count + " hit-test holes (hover flicker): " + string.Join(" | ", holes.Take(25)));
        }

        IReadOnlyList<string> unscaled = h.FindUnscaledPlots();
        if (unscaled.Count > 0)
        {
            ctx.Warn("charts not following display scale (text too small): " + string.Join(" | ", unscaled));
        }

        IReadOnlyList<string> contrast = h.FindLowContrast();
        if (contrast.Count > 0)
        {
            ctx.Warn(contrast.Count + " low-contrast: " + string.Join(" | ", contrast.Take(25)));
        }
    }

    private static string Invariant(System.FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 逐页逐键巡检：每一页在默认状态下把横键（功能组）和根层竖键挨个按一遍（危险键另有专门用例）。
/// 要求：不崩、没有界面异常、没有"未预期的错误"；业务校验类报警算预期内。
/// 按下去问了一句就取消；开了子视图就截图再"« 返回"；跳到别的页就记下来再回来。
/// </summary>
internal sealed class PageSweepSuite : ISelfTestSuite
{
    /// <summary>
    /// 不在巡检里按的键：会让机床动的都先问一句（巡检会取消），这里只剩按下去就直接写机床的开关类，
    /// 以及文件对话框（自检里没人去点）。
    /// </summary>
    private static readonly HashSet<string> Excluded = new(System.StringComparer.Ordinal)
    {
        "Fn_Empty", "Vk_Coolant", "Fn_Pause", "Vk_ExportUsb", "Vk_ImportUsb", "Vk_ExportAll", "Vk_Print", "Vk_ExportExcel",
        "Vk_Backup", "Vk_ExportSnapshot", "Vk_TestRead", "Fn_ExportExcel", "Fn_ExportFile", "Fn_ImportFile", "Fn_ExportAll", "Fn_ExportLedger", "Fn_ImportLedger", "Vk_ExportTemplate",
    };

    public string Name => "PageSweep";

    public async Task RunAsync(SelfTestHarness h)
    {
        ShellViewModel shell = h.Shell;
        foreach (PageKey page in AvailablePages(h))
        {
            await h.GoToAsync(page);
            await h.RecoverAsync();

            int horizontalCount = shell.CurrentPage.FunctionKeys.Count;
            for (int index = 0; index < horizontalCount; index++)
            {
                await SweepAsync(h, page, "H", () => index < shell.CurrentPage.FunctionKeys.Count ? shell.CurrentPage.FunctionKeys[index] : null, index);
            }

            await h.GoToAsync(page);
            await h.RecoverAsync();
            int verticalCount = shell.CurrentPage.VerticalKeys.Count;
            for (int index = 0; index < verticalCount; index++)
            {
                await SweepAsync(h, page, "V", () => index < shell.VerticalKeys.Count ? shell.VerticalKeys[index] : null, index);
            }

            // 页面被按"脏"了就放弃，保证下一页从干净状态开始。
            if (shell.CurrentPage.IsDirty)
            {
                shell.CurrentPage.DiscardChanges();
            }
        }
    }

    /// <summary>自检这台机能进的画面（离线时进不去的不算），每个画面一次。</summary>
    internal static IReadOnlyList<PageKey> AvailablePages(SelfTestHarness h) =>
        h.Services.GetServices<PageViewModelBase>()
            .Where(page => !h.Shell.IsOffline || page.WorksOffline)
            .Where(page => page.Key != PageKey.Commissioning || RollGrinder.Services.Session.UserSessionPermissionExtensions.Can(h.Services.GetRequiredService<RollGrinder.Services.Session.IUserSession>(), RollGrinder.Services.Session.Permission.EditMachineConfig))
            .Select(page => page.Key)
            .OrderBy(key => key)
            .ToList();

    private static async Task SweepAsync(SelfTestHarness h, PageKey page, string bar, System.Func<FunctionKeyViewModel?> keyAt, int index)
    {
        ShellViewModel shell = h.Shell;
        if (shell.CurrentPage.Key != page)
        {
            await h.GoToAsync(page);
        }

        FunctionKeyViewModel? key = keyAt();
        if (key is null || key.IsPlaceholder || key.Kind is FunctionKeyKind.Navigation or FunctionKeyKind.Cancel or FunctionKeyKind.Confirm
            || Excluded.Contains(key.LabelResourceKey))
        {
            return;
        }

        string label = key.LabelResourceKey;
        await h.StepAsync(page.ToString(), Invariant($"{bar}{index + 1}_{label}"), async ctx =>
        {
            if (!key.IsUsable)
            {
                ctx.Skip("key is unavailable in the default state: " + key.ReasonText);
            }

            await h.PressAsync(key);
            if (h.HasPendingConfirmation)
            {
                ctx.Note("asked: " + h.PendingQuestion);
                await h.CancelConfirmationAsync();
            }

            ctx.Note(Describe(h, page));

            if (shell.CurrentPage.ActiveSubViewKey is not null || shell.IsLeaveConfirmOpen
                || shell.CurrentPage.Key != page || shell.CurrentPage.HasModalPrompt)
            {
                h.TryScreenshot(Invariant($"sweep-{page}-{bar}{index + 1}-{label}"), ShotKind.Detail);
            }

            await ReturnToAsync(h, page, ctx);
            ctx.Check(shell.CurrentPage.Key == page, "could not return to " + page);
        }, new StepOptions(Tolerant: true));
    }

    private static string Describe(SelfTestHarness h, PageKey origin)
    {
        ShellViewModel shell = h.Shell;
        var parts = new List<string>();
        if (shell.CurrentPage.Key != origin)
        {
            parts.Add("navigated to " + shell.CurrentPage.Key);
        }

        if (shell.CurrentPage.ActiveSubViewKey is { } subView)
        {
            parts.Add("sub view " + subView);
        }

        if (shell.IsLeaveConfirmOpen)
        {
            parts.Add("leave-confirm");
        }

        if (shell.CurrentPage.HasModalPrompt)
        {
            parts.Add("prompt open");
        }

        if (shell.CurrentPage.IsDirty)
        {
            parts.Add("page dirty");
        }

        return parts.Count == 0 ? "no navigation" : string.Join(", ", parts);
    }

    private static async Task ReturnToAsync(SelfTestHarness h, PageKey origin, StepContext ctx)
    {
        ShellViewModel shell = h.Shell;
        if (shell.IsLeaveConfirmOpen)
        {
            await h.RunAsync(shell.CancelLeaveCommand);
        }

        if (shell.CurrentPage.Key != origin && shell.BackText.Length > 0 && shell.CurrentPage.ActiveSubViewKey is null)
        {
            // 任务跳转：用"« 返回"回去，顺带验证它真的回到发起页。
            await h.BackAsync();
            ctx.Note("returned with « back");
        }

        await h.RecoverAsync();
        if (shell.CurrentPage.Key != origin)
        {
            await h.GoToAsync(origin, ctx);
        }
    }

    private static string Invariant(System.FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 渲染巡检（换语言、换分辨率时用）：每一页、每一个子视图、页面菜单态都渲染一遍、截图、扫缺失文案。
/// 不做任何会改数据的动作。
/// </summary>
internal sealed class RenderSuite : ISelfTestSuite
{
    /// <summary>每个画面要渲染的功能组（横键）与子视图入口（竖键）。</summary>
    private static readonly Dictionary<PageKey, string[]> Groups = new()
    {
        [PageKey.Parameters] = new[] { ParametersViewModel.WheelGroup, ParametersViewModel.CalibrationGroup, ParametersViewModel.AuditGroup },
        [PageKey.Diagnostics] = new[]
        {
            DiagnosticsViewModel.AlarmsGroup, DiagnosticsViewModel.TagMonitorGroup, DiagnosticsViewModel.AuditGroup,
            DiagnosticsViewModel.RunLogGroup, DiagnosticsViewModel.ConnectionGroup, DiagnosticsViewModel.BackupGroup,
        },
        [PageKey.Commissioning] = new[] { CommissioningViewModel.MachineConfigGroup, CommissioningViewModel.TagMappingGroup, CommissioningViewModel.SystemGroup },
        [PageKey.Manual] = MachineAreaKeys.Order.Where(o => MachineAreaKeys.PageOf(o.Group) == PageKey.Manual).Select(o => o.Group).ToArray(),
    };

    private static readonly Dictionary<PageKey, string[]> SubViewKeys = new()
    {
        [PageKey.Records] = new[] { "Vk_QueryAsk", "Fn_Overview" },
        [PageKey.Rolls] = new[] { "Vk_RegisterRoll", "Vk_MultiSelect" },
        [PageKey.Parameters] = new[] { "Vk_ChangeWheel" },
        [PageKey.AutoGrinding] = new[] { "Fn_Compensation", "Fn_ProgramBlock" },
    };

    public string Name => "Render";

    public async Task RunAsync(SelfTestHarness h)
    {
        ShellViewModel shell = h.Shell;
        foreach (PageKey page in PageSweepSuite.AvailablePages(h))
        {
            await h.StepAsync(page.ToString(), "PageRoot", async ctx =>
            {
                await h.GoToAsync(page, ctx);
                CheckTexts(h, ctx);
            }, StepOptions.Shot);

            await h.StepAsync(page.ToString(), "AreaMenuState", async ctx =>
            {
                await h.RunAsync(shell.ToggleAreaMenuCommand);
                CheckTexts(h, ctx);
                h.TryScreenshot("render-menu-" + page);
                await h.RunAsync(shell.CloseAreaMenuCommand);
            });

            await h.StepAsync(page.ToString(), "HelpState", async ctx =>
            {
                await h.RunAsync(shell.ToggleHelpCommand);
                CheckTexts(h, ctx);
                h.TryScreenshot("render-help-" + page);
                shell.Help.Close();
                await h.SettleAsync();
            });

            foreach (string group in Groups.TryGetValue(page, out string[]? groups) ? groups : System.Array.Empty<string>())
            {
                await h.StepAsync(page.ToString(), "Group_" + group, async ctx =>
                {
                    await h.GoToAsync(page, ctx, group);
                    CheckTexts(h, ctx);
                    h.TryScreenshot("render-" + page + "-" + group);
                }, new StepOptions(Tolerant: true));
            }

            foreach (string key in SubViewKeys.TryGetValue(page, out string[]? keys) ? keys : System.Array.Empty<string>())
            {
                await h.StepAsync(page.ToString(), "SubView_" + key, async ctx =>
                {
                    await h.GoToAsync(page, ctx);
                    if (h.IndexOfKey(key) >= 0)
                    {
                        await h.PressKeyAsync(ctx, key);
                    }
                    else
                    {
                        await h.PressVerticalKeyAsync(ctx, key);
                    }

                    ctx.Check(shell.CurrentPage.ActiveSubViewKey is not null, key + " should open a sub view");
                    CheckTexts(h, ctx);
                    h.TryScreenshot("render-" + page + "-" + key);
                    await h.RecoverAsync();
                }, new StepOptions(Tolerant: true));
            }
        }
    }

    private static void CheckTexts(SelfTestHarness h, StepContext ctx)
    {
        IReadOnlyList<string> missing = h.FindMissingResources();
        ctx.Check(missing.Count == 0, "missing resources: " + string.Join(", ", missing));
        NavigationSuite.WarnClipped(h, ctx);
    }
}
