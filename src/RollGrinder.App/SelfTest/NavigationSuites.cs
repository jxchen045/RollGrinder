using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;

namespace RollGrinder.App.SelfTest;

/// <summary>
/// 导航：每个区域一键直达并渲染、页面菜单（软键条变身）的开/选/收、导航槽的四种角色、
/// 任务跳转与返回（"补偿设置"那条路）、子视图、脏页离开确认、Esc。
/// </summary>
internal sealed class NavigationSuite : ISelfTestSuite
{
    public string Name => "Navigation";

    public async Task RunAsync(SelfTestHarness h)
    {
        ShellViewModel shell = h.Shell;
        IStringLocalizer localizer = h.Services.GetRequiredService<IStringLocalizer>();
        PageKey home = shell.CurrentPage.Key;
        List<AreaMenuItemViewModel> available = shell.AreaMenuItems.Where(i => i.IsAvailable).ToList();

        foreach (AreaMenuItemViewModel item in shell.AreaMenuItems)
        {
            string step = "CtrlN_" + item.ShortcutNumber.ToString(CultureInfo.InvariantCulture) + "_" + item.Key;
            if (!item.IsAvailable)
            {
                await h.StepAsync("DirectJump", step, ctx =>
                {
                    ctx.Check(!item.Command.CanExecute(null), "an unavailable area must refuse Ctrl+n");
                    ctx.Note("unavailable offline, refused as designed");
                    return Task.CompletedTask;
                });
                continue;
            }

            await h.StepAsync("DirectJump", step, async ctx =>
            {
                await h.GoToAsync(item.Key, ctx);
                ctx.Check(shell.CurrentPage.Key == item.Key, "expected page " + item.Key + ", got " + shell.CurrentPage.Key);
                string expectedShortcut = localizer.Format("Nav_ShortcutFormat", item.ShortcutNumber);
                ctx.Check(shell.CurrentAreaShortcutText == expectedShortcut, "shortcut badge should read " + expectedShortcut);
                ctx.Check(shell.BreadcrumbText.Length > 0, "breadcrumb should not be empty");
                IReadOnlyList<string> missing = h.FindMissingResources();
                ctx.Check(missing.Count == 0, "missing resources on page: " + string.Join(", ", missing));
                ctx.Note("breadcrumb=" + shell.BreadcrumbText);
                WarnClipped(h, ctx);
            }, StepOptions.Shot);
        }

        await h.StepAsync("AreaMenu", "OpenFromHomeWithF8", async ctx =>
        {
            await h.GoToAsync(home, ctx);
            ctx.Check(h.NavigationKeyLabel == "Nav_AreaMenu", "on the home root F8 should read 'page menu', got " + h.NavigationKeyLabel);
            await h.PressNavigationKeyAsync();
            ctx.Check(shell.IsAreaMenuOpen, "F8 on home should open the page menu");
            ctx.Check(!shell.IsOverlayOpen, "the page menu must not be a blocking overlay");
            ctx.Check(h.NavigationKeyLabel == "Menu_Cancel", "in menu state F8 should read 'cancel'");
            int areaKeys = shell.FunctionKeys.Count(k => k.Kind is FunctionKeyKind.AreaMenu or FunctionKeyKind.AreaMenuCurrent);
            ctx.Check(areaKeys == shell.AreaMenuItems.Count, "one soft key per area expected, got " + areaKeys);
            ctx.Check(shell.FunctionKeys.Count(k => k.Kind == FunctionKeyKind.AreaMenuCurrent) == 1, "exactly one key should be marked current");
            ctx.Check(shell.FunctionKeys.Take(areaKeys).All(k => !string.IsNullOrEmpty(k.ShortcutText)), "every area key should show its Ctrl+n");
        }, StepOptions.Shot);

        await h.StepAsync("AreaMenu", "CancelWithF8", async ctx =>
        {
            await h.PressNavigationKeyAsync();
            ctx.Check(!shell.IsAreaMenuOpen, "F8 in menu state should close the menu");
            ctx.Check(shell.CurrentPage.Key == home, "cancel must not change page");
            ctx.Check(shell.FunctionKeys.All(k => k.Kind is not (FunctionKeyKind.AreaMenu or FunctionKeyKind.AreaMenuCurrent)), "page keys should be back");
        });

        await h.StepAsync("AreaMenu", "ToggleWithMenuButton", async ctx =>
        {
            await h.RunAsync(shell.ToggleAreaMenuCommand);
            ctx.Check(shell.IsAreaMenuOpen, "menu button should open the menu");
            await h.RunAsync(shell.ToggleAreaMenuCommand);
            ctx.Check(!shell.IsAreaMenuOpen, "second press should close it");
        });

        await h.StepAsync("AreaMenu", "EscapeCloses", async ctx =>
        {
            await h.RunAsync(shell.OpenAreaMenuCommand);
            shell.PressEscape();
            await h.SettleAsync();
            ctx.Check(!shell.IsAreaMenuOpen, "Esc should close the menu");
            ctx.Check(shell.CurrentPage.Key == home, "Esc on the menu must not also go back a level");
        });

        AreaMenuItemViewModel? target = available.FirstOrDefault(i => i.Key != home);
        if (target is not null)
        {
            await h.StepAsync("AreaMenu", "PickAreaWithFunctionKey", async ctx =>
            {
                await h.RunAsync(shell.OpenAreaMenuCommand);
                await h.PressKeyAsync(target.ShortcutNumber - 1);
                ctx.Check(shell.CurrentPage.Key == target.Key, "F" + target.ShortcutNumber + " in menu state should open " + target.Key);
                ctx.Check(!shell.IsAreaMenuOpen, "menu should close after picking");
                ctx.Check(h.NavigationKeyLabel == "Nav_BackToHome", "on a sub page F8 should read 'back to home'");
            });

            await h.StepAsync("NavigationKey", "BackToHome", async ctx =>
            {
                await h.PressNavigationKeyAsync();
                ctx.Check(shell.CurrentPage.Key == home, "F8 on a sub page should return home");
            });

            await h.StepAsync("NavigationKey", "EscapeGoesBack", async ctx =>
            {
                await h.GoToAsync(target.Key, ctx);
                shell.PressEscape();
                await h.SettleAsync();
                ctx.Check(shell.CurrentPage.Key == home, "Esc on a sub page root should go home");
            });
        }

        // 任务跳转：自动磨削 →「补偿设置」→ 工序编程，导航槽送回。离线时自动页进不去，改走记录页的子视图。
        if (available.Any(i => i.Key == PageKey.AutoGrinding))
        {
            await h.StepAsync("TaskJump", "CompensationSettingsAndBack", async ctx =>
            {
                await h.GoToAsync(PageKey.AutoGrinding, ctx);
                await h.PressKeyAsync(ctx, "Fn_CompensationSettings");
                ctx.Check(shell.CurrentPage.Key == PageKey.Steps, "compensation settings should open the steps page");
                ctx.Check(h.NavigationKeyLabel == "Nav_BackToPageFormat", "F8 should read 'back to <origin>'");
                ctx.Note("nav key=" + shell.FunctionKeys[PageViewModelBase.PageFunctionKeyCount].Label);
                await h.PressNavigationKeyAsync();
                ctx.Check(shell.CurrentPage.Key == PageKey.AutoGrinding, "F8 should return to the auto page");
            }, StepOptions.Shot);
        }

        PageKey subViewPage = available.Any(i => i.Key == PageKey.Diagnostics) ? PageKey.Diagnostics : PageKey.Records;
        string subViewKey = subViewPage == PageKey.Diagnostics ? "Fn_TagMonitor" : "Fn_RollLedger";
        await h.StepAsync("SubView", "OpenAndCloseWithF8", async ctx =>
        {
            await h.GoToAsync(subViewPage, ctx);
            await h.PressKeyAsync(ctx, subViewKey);
            ctx.Check(shell.CurrentPage.ActiveSubViewKey is not null, subViewKey + " should open a sub view");
            ctx.Check(h.NavigationKeyLabel == "Nav_BackToPageFormat", "F8 should read 'back to <page>' inside a sub view");
            h.TryScreenshot("subview-" + subViewPage);
            await h.PressNavigationKeyAsync();
            ctx.Check(shell.CurrentPage.ActiveSubViewKey is null, "F8 should close the sub view");
            ctx.Check(shell.CurrentPage.Key == subViewPage, "closing a sub view must not leave the page");
        });

        await h.StepAsync("LeaveConfirm", "DirtyPageAsksBeforeLeaving", async ctx =>
        {
            await h.GoToAsync(PageKey.Profile, ctx);
            ProfileViewModel profile = h.Page<ProfileViewModel>();
            await h.PressKeyAsync(ctx, "Fn_NewSegment");
            ctx.Check(profile.IsDirty, "adding a segment should mark the profile page dirty");
            shell.AreaMenuItems.First(i => i.Key == PageKey.Records).Command.Execute(null);
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
            shell.AreaMenuItems.First(i => i.Key == PageKey.Records).Command.Execute(null);
            await h.SettleAsync();
            await h.RunAsync(shell.DiscardAndLeaveCommand);
            ctx.Check(shell.CurrentPage.Key == PageKey.Records, "discard should leave");
            ctx.Check(!profile.IsDirty, "discard should clear the dirty flag");
            ctx.Check(profile.Segments.Count == segmentsBefore - 1, Invariant($"discard should really remove the new segment ({segmentsBefore} -> {profile.Segments.Count})"));
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

        IReadOnlyList<string> contrast = h.FindLowContrast();
        if (contrast.Count > 0)
        {
            ctx.Warn(contrast.Count + " low-contrast: " + string.Join(" | ", contrast.Take(25)));
        }
    }

    private static string Invariant(System.FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 逐页逐键巡检：每一页在默认状态下把 7 个功能键挨个按一遍（危险键另有专门用例）。
/// 要求：不崩、没有界面异常、没有"未预期的错误"；业务校验类报警算预期内。
/// 按下去开了子视图就截图再用 F8 关掉；跳到别的页就记下来再回来。
/// </summary>
internal sealed class PageSweepSuite : ISelfTestSuite
{
    /// <summary>按一下就会让机床动、或会清掉报警的键：交给专门的用例按两下、在对的时机按。</summary>
    private static readonly HashSet<string> Excluded = new(System.StringComparer.Ordinal)
    {
        "Fn_Start", "Fn_SkipStep", "Fn_EndEarly", "Fn_ConfirmAgain", "Fn_HmiReset", "Fn_DownloadNc", "Fn_Empty",
    };

    public string Name => "PageSweep";

    public async Task RunAsync(SelfTestHarness h)
    {
        ShellViewModel shell = h.Shell;
        foreach (AreaMenuItemViewModel area in shell.AreaMenuItems.Where(i => i.IsAvailable).ToList())
        {
            await h.GoToAsync(area.Key);
            await h.RecoverAsync();

            for (int index = 0; index < PageViewModelBase.PageFunctionKeyCount; index++)
            {
                if (shell.CurrentPage.Key != area.Key)
                {
                    await h.GoToAsync(area.Key);
                }

                FunctionKeyViewModel key = shell.FunctionKeys[index];
                string label = key.LabelResourceKey;
                if (Excluded.Contains(label))
                {
                    continue;
                }

                int keyIndex = index;
                await h.StepAsync(area.Key.ToString(), Invariant($"F{keyIndex + 1}_{label}"), async ctx =>
                {
                    if (!key.IsEnabled)
                    {
                        ctx.Skip("key is disabled in the default state");
                    }

                    await h.PressKeyAsync(keyIndex);
                    ctx.Note(Describe(h, area.Key));

                    if (shell.CurrentPage.ActiveSubViewKey is not null || shell.IsLeaveConfirmOpen
                        || shell.CurrentPage.Key != area.Key || PageOverlayOpen(h))
                    {
                        h.TryScreenshot(Invariant($"sweep-{area.Key}-F{keyIndex + 1}-{label}"));
                    }

                    await ReturnToAsync(h, area.Key, ctx);
                    ctx.Check(shell.CurrentPage.Key == area.Key, "could not return to " + area.Key);
                }, new StepOptions(Tolerant: true));
            }

            // 页面被按"脏"了就放弃，保证下一页从干净状态开始。
            if (shell.CurrentPage.IsDirty)
            {
                shell.CurrentPage.DiscardChanges();
            }
        }
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

        if (PageOverlayOpen(h))
        {
            parts.Add("page panel open");
        }

        if (shell.CurrentPage.IsDirty)
        {
            parts.Add("page dirty");
        }

        return parts.Count == 0 ? "no navigation" : string.Join(", ", parts);
    }

    private static bool PageOverlayOpen(SelfTestHarness h) =>
        h.Page<StepsViewModel>().IsProgramLibraryOpen
        || h.Page<StepsViewModel>().IsProfileLibraryOpen
        || h.Page<ProfileViewModel>().IsLibraryOpen;

    private static async Task ReturnToAsync(SelfTestHarness h, PageKey origin, StepContext ctx)
    {
        ShellViewModel shell = h.Shell;
        if (shell.IsLeaveConfirmOpen)
        {
            await h.RunAsync(shell.CancelLeaveCommand);
        }

        if (shell.CurrentPage.Key != origin && h.NavigationKeyLabel == "Nav_BackToPageFormat")
        {
            // 任务跳转：用导航槽回去，顺带验证它真的回到发起页。
            await h.PressNavigationKeyAsync();
            ctx.Note("returned with F8");
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
    private static readonly Dictionary<PageKey, string[]> SubViewKeys = new()
    {
        [PageKey.Records] = new[] { "Fn_RollLedger" },
        [PageKey.Diagnostics] = new[] { "Fn_TagMonitor", "Fn_MachineConfig", "Fn_TagMapping", "Fn_AuditLog" },
        [PageKey.Settings] = new[] { "Fn_NewWheel" },
    };

    public string Name => "Render";

    public async Task RunAsync(SelfTestHarness h)
    {
        ShellViewModel shell = h.Shell;
        foreach (AreaMenuItemViewModel area in shell.AreaMenuItems.Where(i => i.IsAvailable).ToList())
        {
            await h.StepAsync(area.Key.ToString(), "PageRoot", async ctx =>
            {
                await h.GoToAsync(area.Key, ctx);
                CheckTexts(h, ctx);
            }, StepOptions.Shot);

            await h.StepAsync(area.Key.ToString(), "PageMenuState", async ctx =>
            {
                await h.RunAsync(shell.OpenAreaMenuCommand);
                CheckTexts(h, ctx);
                h.TryScreenshot("render-menu-" + area.Key);
                await h.RunAsync(shell.CloseAreaMenuCommand);
            });

            if (!SubViewKeys.TryGetValue(area.Key, out string[]? keys))
            {
                continue;
            }

            foreach (string key in keys)
            {
                await h.StepAsync(area.Key.ToString(), "SubView_" + key, async ctx =>
                {
                    await h.PressKeyAsync(ctx, key);
                    ctx.Check(shell.CurrentPage.ActiveSubViewKey is not null, key + " should open a sub view");
                    CheckTexts(h, ctx);
                    h.TryScreenshot("render-" + area.Key + "-" + key);
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
