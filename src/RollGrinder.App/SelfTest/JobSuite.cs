using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;
using RollGrinder.Data;
using RollGrinder.Core.Steps;
using RollGrinder.Services.Jobs;

namespace RollGrinder.App.SelfTest;

/// <summary>
/// 以轧辊为中心的作业流程：作业页输入辊号 → 台账里没有就去轧辊区登记（计划必填，用选择子视图选辊形与程序）
/// → 回到作业页打开一页核对。自检套件和全流程共用。
/// </summary>
internal static class JobFlow
{
    /// <summary>
    /// 保证台账里有这支辊、计划是给定的辊形与程序，然后在作业页打开它的核对。
    /// 第一次跑：作业页"新登记轧辊"→ 登记表；同一个数据目录再跑：辊已在台账，改为"编辑"把计划设回去。
    /// </summary>
    public static async Task RegisterAndOpenAsync(
        SelfTestHarness h, StepContext ctx, string rollId, double bodyLengthMm, double nominalMm, double currentMm, string profileName, string programName)
    {
        JobViewModel job = h.Page<JobViewModel>();
        RollsViewModel rolls = h.Page<RollsViewModel>();
        await h.GoToAsync(PageKey.Job, ctx);
        await job.Loading;
        if (job.IsReview)
        {
            await h.PressVerticalKeyAsync(ctx, "Vk_BackToQueue");
        }

        job.SearchText = rollId;
        await h.PressVerticalKeyAsync(ctx, "Vk_RegisterRoll");
        ctx.Check(h.Shell.CurrentPage.Key == PageKey.Rolls, "'register roll' should open the rolls area, is " + h.Shell.CurrentPage.Key);
        bool formReady = await h.WaitUntilAsync(() => rolls.IsForm && rolls.IsNewRoll, TimeSpan.FromSeconds(10));
        ctx.Check(formReady, "the rolls area should open on an empty register form");
        ctx.Check(rolls.FormRollId == rollId, "the roll id typed on the job page should be pre-filled, is " + rolls.FormRollId);

        bool exists = await h.Services.GetRequiredService<IRollRepository>().GetAsync(rollId, default) is not null;
        if (exists)
        {
            // 已登记过：取消新登记，选中它按"编辑"。
            await h.PressVerticalKeyAsync(ctx, "Vk_Cancel");
            await h.WaitUntilAsync(() => rolls.IsLedger && rolls.Rows.Any(r => r.RollId == rollId), TimeSpan.FromSeconds(10));
            rolls.SelectedRow = rolls.Rows.FirstOrDefault(r => r.RollId == rollId);
            await h.SettleAsync();
            await h.PressVerticalKeyAsync(ctx, "Vk_EditRoll");
            await h.WaitUntilAsync(() => rolls.IsForm, TimeSpan.FromSeconds(10));
            ctx.Note("roll already registered: edited instead");
        }

        rolls.FormBodyLengthText = Format(bodyLengthMm);
        rolls.FormNominalText = Format(nominalMm);
        rolls.FormCurrentText = Format(currentMm);
        await PickAsync(h, ctx, rolls, "Vk_PickProfile", profileName, () => rolls.FormProfileText);
        await PickAsync(h, ctx, rolls, "Vk_PickProgram", programName, () => rolls.FormProgramText);
        h.TryScreenshot("rolls-register-form");
        await h.PressVerticalKeyAsync(ctx, "Vk_Save");
        bool saved = await h.WaitUntilAsync(() => !rolls.IsForm, TimeSpan.FromSeconds(10));
        ctx.Check(saved, "the roll should be saved: " + string.Join(" | ", rolls.FormProblems));

        for (int i = 0; i < 3 && h.Shell.CurrentPage.Key != PageKey.Job; i++)
        {
            await h.BackAsync();
        }

        if (h.Shell.CurrentPage.Key != PageKey.Job)
        {
            await h.GoToAsync(PageKey.Job, ctx);
        }

        await job.Loading;
        if (!job.IsReview)
        {
            job.SearchText = rollId;
            await h.RunAsync(job.SearchEnterCommand);
            await job.Loading;
        }

        bool open = await h.WaitUntilAsync(() => job.IsReview && job.Checks.Count > 0, TimeSpan.FromSeconds(10));
        ctx.Check(open, "the job review should open for " + rollId);
    }

    /// <summary>选择子视图里按名字选一条（不在"适合的"里就切到"显示全部"）。</summary>
    public static async Task PickAsync(SelfTestHarness h, StepContext ctx, RollsViewModel rolls, string key, string name, Func<string> shown)
    {
        await h.PressVerticalKeyAsync(ctx, key);
        await h.WaitUntilAsync(() => rolls.IsPicking, TimeSpan.FromSeconds(10));
        PlanPickItemViewModel? item = rolls.Picker.Items.FirstOrDefault(i => i.Name == name);
        if (item is null && h.IndexOfVerticalKey("Vk_ShowAll") >= 0)
        {
            await h.PressVerticalKeyAsync(ctx, "Vk_ShowAll");
            await h.WaitUntilAsync(() => rolls.Picker.Items.Any(i => i.Name == name), TimeSpan.FromSeconds(5));
            item = rolls.Picker.Items.FirstOrDefault(i => i.Name == name);
        }

        if (item is null)
        {
            rolls.TryDismissPrompt();
            ctx.Skip("'" + name + "' is not in the library");
        }

        rolls.Picker.SelectedItem = item;
        await h.PressVerticalKeyAsync(ctx, "Vk_PickThis");
        bool chosen = await h.WaitUntilAsync(() => shown().StartsWith(name, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        ctx.Check(chosen, "the form should show the chosen '" + name + "', shows " + shown());
    }

    /// <summary>核对没过时第一条拦住的原因（写进自检记录）。</summary>
    public static string FirstBlock(JobViewModel job) =>
        job.Checks.FirstOrDefault(c => c.IsBlock) is { } block ? block.ItemText + ": " + block.MessageText : "none";

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
}

/// <summary>作业页：待磨清单、尾号查找、当场登记（计划必填）、一页核对、改磨削量、换方案仅本次（选原因）、回到当前方案、离线下发被拒。</summary>
internal sealed class JobSuite : ISelfTestSuite
{
    public string Name => "Job";

    public async Task RunAsync(SelfTestHarness h)
    {
        JobViewModel job = h.Page<JobViewModel>();
        bool offline = h.Services.GetRequiredService<RollGrinder.Contracts.IAppOptions>().IsOffline;

        StepStatus queue = await h.StepAsync("Queue", "ShowsQueue", async ctx =>
        {
            await h.GoToAsync(PageKey.Job, ctx);
            await job.Loading;
            ctx.Check(job.IsQueue, "the job page opens on the queue");
            ctx.Check(h.IndexOfVerticalKey("Vk_ToJob") >= 0 || h.IndexOfVerticalKey("Vk_ResumeJob") >= 0 || h.IndexOfVerticalKey("Vk_RegrindJob") >= 0,
                "'to job' should be on the bar");
            ctx.Note(job.QueueTitle);
        }, StepOptions.Shot);

        if (queue != StepStatus.Pass && queue != StepStatus.Warn)
        {
            h.Note("could not reach the job page: the job suite is skipped");
            return;
        }

        await h.StepAsync("Queue", "TailNumberFilters", async ctx =>
        {
            job.SearchText = "NO-SUCH-ROLL-XYZ";
            await h.SettleAsync();
            ctx.Check(job.Queue.Count == 0, "an unknown roll id should match nothing");
            job.SearchText = string.Empty;
            await h.SettleAsync();
        });

        await h.StepAsync("Review", "RegisterRollWithPlan", async ctx =>
        {
            // 辊身与辊形模板一样长：登记时从模板复制出方案 1（不伸缩；长度不同只在端部非工作区里截取 / 延伸）。
            RollProfileSummary? profile = (await h.Services.GetRequiredService<IRollProfileRepository>().ListAsync(200, default))
                .FirstOrDefault(p => p.Name == SelfTestNames.ProfileA);
            double body = profile?.BodyLengthMm ?? 2000;
            await JobFlow.RegisterAndOpenAsync(h, ctx, SelfTestNames.JobRollId, body, 600, 596, SelfTestNames.ProfileA, SelfTestNames.ProgramA);
            ctx.Note(job.ReviewTitle + " · " + job.CheckSummaryText);
        }, StepOptions.Shot);

        await h.StepAsync("Review", "ChecklistPasses", ctx =>
        {
            if (!job.IsReview)
            {
                ctx.Skip("no review is open (the registration step before did not get there)");
            }

            ctx.Check(job.IsReview, "should be on the review");
            ctx.Check(job.Checks.Any(c => c.ItemText == job.Localizer["CheckItem_Length"]), "the length check (centre alignment) should be listed");
            ctx.Check(job.CanDownload, "the job should pass, first block: " + JobFlow.FirstBlock(job));
            ctx.Check(job.BuildJob() is { } built && built.ProgramName == SelfTestNames.ProgramA && built.PlanNo is not null && built.Alignment is not null,
                "the built job should carry the program, the plan number and the length alignment");
            ctx.Check(job.ReviewCurve.Count > 1, "the review should draw the target profile");
            return Task.CompletedTask;
        });

        await h.StepAsync("Review", "RunOptionsOnlyWhatApplies", ctx =>
        {
            if (!job.IsReview)
            {
                ctx.Skip("no review is open (the registration step before did not get there)");
            }

            // 本次取舍只列用得上的：挂工序的必须注明是第几道；下发的十个开关里，没列出来的都是关。
            foreach (ProgramOptionRowViewModel option in job.ProgramOptions.Where(o => ProgramOptionPlanner.IsStepLinked(o.Descriptor.Key)))
            {
                ctx.Check(option.Note.Length > 0, option.Descriptor.Key + " is tied to a step and should say which one");
            }

            GrindingJob? built = job.BuildJob();
            ctx.Check(built is not null, "the job should build");
            foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All.Where(o => job.ProgramOptions.All(row => row.Descriptor.Key != o.Key)))
            {
                ctx.Check(!built!.IsProgramOptionEnabled(option.Key), option.Key + " is not listed, so it must go out off");
            }

            ctx.Note("listed: " + string.Join(",", job.ProgramOptions.Select(o => o.Descriptor.Key + (o.IsOn ? "+" : "-"))));
            return Task.CompletedTask;
        });

        await h.StepAsync("Review", "StockEditsRecheck", async ctx =>
        {
            if (!job.IsReview)
            {
                ctx.Skip("no review is open (the registration step before did not get there)");
            }

            string before = job.StockText;
            job.StockText = "abc";
            await h.SettleAsync();
            int download = h.IndexOfVerticalKey("Vk_ConfirmDownload");
            ctx.Check(!job.CanDownload && download >= 0 && !h.IsVerticalKeyUsable(download),
                "a stock that is not a number blocks the download (the key stays, greyed)");
            job.StockText = before;
            await h.SettleAsync();
            ctx.Check(job.CanDownload, "restoring the stock passes again, first block: " + JobFlow.FirstBlock(job));
        });

        await h.StepAsync("Review", "ChangePlanThisTimeNeedsReason", async ctx =>
        {
            if (!job.IsReview)
            {
                ctx.Skip("no review is open (the registration step before did not get there)");
            }

            // 这支辊再有一个方案（复制当前方案），换方案时才有得选。
            IGrindingPlanService plans = h.Services.GetRequiredService<IGrindingPlanService>();
            if (await plans.CurrentAsync(SelfTestNames.JobRollId, default) is { } current
                && (await plans.ListAsync(SelfTestNames.JobRollId, default)).Count < 2)
            {
                await plans.CopyAsync(current.PlanId, null, setCurrent: false, "selftest", default);
            }

            await h.PressVerticalKeyAsync(ctx, "Vk_PlanMenu");
            await h.PressVerticalKeyAsync(ctx, "Vk_SwitchPlan");
            await h.WaitUntilAsync(() => job.IsPicking, TimeSpan.FromSeconds(10));
            PlanPickItemViewModel? other = job.Picker.Items.FirstOrDefault(i => !i.VersionText.StartsWith("★", StringComparison.Ordinal));
            if (other is null)
            {
                job.TryDismissPrompt();
                ctx.Skip("this roll has no second plan");
            }

            job.Picker.SelectedItem = other;
            await h.PressVerticalKeyAsync(ctx, "Vk_PickThis");
            ctx.Check(h.HasPendingConfirmation, "a plan other than the current one should ask 'this time only / set as current'");
            ctx.Check(h.IndexOfVerticalKey("Vk_MakePlan") >= 0, "'set as current plan' should sit on key 6");
            h.TryScreenshot("job-deviation-choice");
            await h.ConfirmAsync(ctx);
            bool applied = await h.WaitUntilAsync(() => job.Deviation == JobDeviation.ThisTimeOnly, TimeSpan.FromSeconds(5));
            ctx.Check(applied, "'this time only' should be applied");
            ctx.Check(!job.CanDownload, "this time only without a reason blocks the download");
            if (h.IndexOfVerticalKey("Reason_Trial") < 0)
            {
                await h.PressVerticalKeyAsync(ctx, "Vk_PlanMenu");
                await h.PressVerticalKeyAsync(ctx, "Vk_Reason");
            }

            await h.PressVerticalKeyAsync(ctx, "Reason_Trial");
            await h.SettleAsync();
            ctx.Check(job.DeviationReason == "Reason_Trial", "the reason should be recorded");
            ctx.Note(job.CheckSummaryText + ", first block: " + JobFlow.FirstBlock(job));
        }, StepOptions.Shot);

        await h.StepAsync("Review", "RestorePlan", async ctx =>
        {
            if (job.Deviation == JobDeviation.None)
            {
                ctx.Skip("nothing to restore");
            }

            await h.PressVerticalKeyAsync(ctx, "Vk_PlanMenu");
            await h.PressVerticalKeyAsync(ctx, "Vk_RestorePlan");
            bool restored = await h.WaitUntilAsync(() => job.Deviation == JobDeviation.None, TimeSpan.FromSeconds(5));
            ctx.Check(restored, "'back to current plan' should go back to the roll's current plan");
            ctx.Check(job.CanDownload, "the plan passes again, first block: " + JobFlow.FirstBlock(job));
        });

        await h.StepAsync("Review", "AdjustThisTimeInsideRange", async ctx =>
        {
            if (!job.IsReview)
            {
                ctx.Skip("no review is open");
            }

            // 两层参数（设计稿 v4 5.2.5）：超范围的不收，范围内的记成本次调整，核对页列出来；全部恢复回到方案值。
            await h.PressVerticalKeyAsync(ctx, "Vk_AdjustThisTime");
            bool open = await h.WaitUntilAsync(() => job.IsAdjusting && job.AdjustRows.Count > 0, TimeSpan.FromSeconds(5));
            ctx.Check(open, "the adjust subview should open with adjustable parameters");
            AdjustRowViewModel? row = job.AdjustRows.FirstOrDefault(r => r.Parameter.Rule.Mode == AdjustMode.Percent) ?? job.AdjustRows.FirstOrDefault();
            if (row is null)
            {
                job.TryDismissPrompt();
                ctx.Skip("nothing is adjustable in this plan");
            }

            job.SelectedAdjustRow = row;
            row!.ValueText = (row.Parameter.PlanValue * 10.0 + 1000.0).ToString(CultureInfo.CurrentCulture);
            await h.SettleAsync();
            int use = h.IndexOfVerticalKey("Vk_UseThisTime");
            ctx.Check(row.IsRefused && use >= 0 && !h.IsVerticalKeyUsable(use), "an out-of-range value is refused and '✓ use this time' is greyed");
            h.TryScreenshot("job-adjust-refused");

            row.Restore();
            row.Nudge(row.Parameter.Rule.Mode == AdjustMode.DecreaseOnly ? -1 : 1);
            await h.SettleAsync();
            ctx.Check(!row.IsRefused && row.IsChanged, "one step inside the range is accepted: " + row.ValueText + " allowed " + row.AllowedText);
            await h.PressVerticalKeyAsync(ctx, "Vk_UseThisTime");
            bool applied = await h.WaitUntilAsync(() => !job.IsAdjusting, TimeSpan.FromSeconds(5));
            ctx.Check(applied, "'✓ use this time' should close the subview");
            ctx.Check(job.BuildJob() is { Adjustments.Count: 1 }, "the built job should carry one adjustment");
            ctx.Check(job.Checks.Any(c => c.ItemText == job.Localizer["CheckItem_Adjust"]), "the review should list the adjustment");
            ctx.Check(job.CanDownload, "an in-range adjustment does not block, first block: " + JobFlow.FirstBlock(job));
            h.TryScreenshot("job-adjusted");

            // 调过以后竖键标 Δ。
            await h.PressVerticalKeyAsync(ctx, "Vk_AdjustThisTimeChanged");
            await h.WaitUntilAsync(() => job.IsAdjusting, TimeSpan.FromSeconds(5));
            foreach (AdjustRowViewModel each in job.AdjustRows)
            {
                each.Restore();
            }

            await h.SettleAsync();
            await h.PressVerticalKeyAsync(ctx, "Vk_UseThisTime");
            await h.WaitUntilAsync(() => !job.IsAdjusting, TimeSpan.FromSeconds(5));
            ctx.Check(job.BuildJob() is { Adjustments.Count: 0 }, "restoring every value leaves no adjustment");
        }, StepOptions.Shot);

        await h.StepAsync("Review", "PlanListOfThisRoll", async ctx =>
        {
            if (!job.IsReview)
            {
                ctx.Skip("no review is open");
            }

            // 核对页"方案列表"：方案区列这支辊的方案（★ 当前在前），返回回到核对。
            await h.PressVerticalKeyAsync(ctx, "Vk_PlanMenu");
            await h.PressVerticalKeyAsync(ctx, "Vk_PlanList");
            PlansViewModel plans = h.Page<PlansViewModel>();
            bool shown = await h.WaitUntilAsync(
                () => h.Shell.CurrentPage.Key == PageKey.Plans && plans.Rows.Count >= 1, TimeSpan.FromSeconds(10));
            ctx.Check(shown, "the plan list of this roll should open");
            ctx.Check(plans.RollId == SelfTestNames.JobRollId, "the plan list should be for the roll on the review");
            ctx.Check(plans.Rows.FirstOrDefault()?.IsCurrent == true, "the current plan should be listed first");
            h.TryScreenshot("plans-list");
            await h.BackAsync();
            bool back = await h.WaitUntilAsync(() => h.Shell.CurrentPage.Key == PageKey.Job && job.IsReview, TimeSpan.FromSeconds(10));
            ctx.Check(back, "back should return to the review");
        }, StepOptions.Shot);

        if (offline)
        {
            await h.StepAsync("Download", "RefusedWithoutMachine", async ctx =>
            {
                if (!job.IsReview)
                {
                    ctx.Skip("no review is open");
                }

                await h.PressVerticalKeyAsync(ctx, "Vk_ConfirmDownload");
                await h.SettleAsync(500);
                ctx.Check(h.Shell.CurrentPage.Key == PageKey.Job && job.IsReview, "a refused download stays on the review");
            }, StepOptions.Expect("*"));
        }

        await h.StepAsync("Review", "BackToQueue", async ctx =>
        {
            if (!job.IsReview)
            {
                ctx.Skip("no review is open");
            }

            await h.PressVerticalKeyAsync(ctx, "Vk_BackToQueue");
            bool back = await h.WaitUntilAsync(() => job.IsQueue, TimeSpan.FromSeconds(5));
            ctx.Check(back, "'back to queue' should close the review");
        });

        await h.StepAsync("Plan", "ProfileAndProcessSaveTogether", async ctx =>
        {
            // 设计稿 v4 P2：辊形与工艺共用一份草稿——改了辊形切到工艺页不问保存，在工艺页一次存下两样。
            // 用一个复制出来、没用过的方案，存完删掉，不动这支辊的当前方案。
            IGrindingPlanService plans = h.Services.GetRequiredService<IGrindingPlanService>();
            if (await plans.CurrentAsync(SelfTestNames.JobRollId, default) is not { } current)
            {
                ctx.Skip("the self-test roll has no plan");
                return;
            }

            GrindingPlanRecord copy = await plans.CopyAsync(current.PlanId, "selftest-draft", setCurrent: false, "selftest", default);
            try
            {
                ProfileViewModel profile = h.Page<ProfileViewModel>();
                StepsViewModel steps = h.Page<StepsViewModel>();
                h.Services.GetRequiredService<JobDraft>().PlanToEdit = copy.PlanId;
                await h.GoToAsync(PageKey.Profile, ctx);
                ctx.Check(await h.WaitUntilAsync(() => profile.PlanId == copy.PlanId, TimeSpan.FromSeconds(10)), "the profile page should open the plan");
                string profileName = profile.ProfileName + "-T";
                profile.ProfileName = profileName;
                ctx.Check(profile.IsDirty, "renaming the profile is an edit");

                await h.PressKeyAsync(ctx, "Fn_PlanProgram");
                bool moved = await h.WaitUntilAsync(
                    () => h.Shell.CurrentPage.Key == PageKey.Steps && steps.PlanId == copy.PlanId, TimeSpan.FromSeconds(10));
                ctx.Check(moved && !h.Shell.IsLeaveConfirmOpen, "going to the process page of the same plan keeps the draft without asking");
                ctx.Check(steps.HasUnsavedWork, "the process page knows the profile is not saved yet");
                string programName = steps.ProgramName + "-T";
                steps.ProgramName = programName;

                bool saved = await steps.SaveAsync(default);
                ctx.Check(saved, "an unused plan saves in place");
                PlanContent? stored = await plans.GetContentAsync(copy.PlanId, default);
                ctx.Check(stored?.Profile.Name == profileName && stored.Program.Name == programName, "one save stores the profile and the process");
                ctx.Check(!profile.IsDirty && !steps.IsDirty, "both pages are clean after the save");
                h.TryScreenshot("plan-shared-draft");
            }
            finally
            {
                // 收尾：两页放回按模板编辑（后面的套件按模板新建程序 / 辊形），删掉复制的方案。
                ProfileViewModel profilePage = h.Page<ProfileViewModel>();
                StepsViewModel stepsPage = h.Page<StepsViewModel>();
                profilePage.DiscardChanges();
                stepsPage.DiscardChanges();
                JobDraft draft = h.Services.GetRequiredService<JobDraft>();
                draft.ProgramToOpen = JobDraft.NewEntry;
                await h.GoToAsync(PageKey.Steps, ctx);
                draft.ProfileToOpen = JobDraft.NewEntry;
                await h.GoToAsync(PageKey.Profile, ctx);
                await h.GoToAsync(PageKey.Job, ctx);
                await plans.DeleteAsync(copy.PlanId, "selftest", default);
            }
        }, StepOptions.Shot);

        await h.StepAsync("Plan", "CopyFromAnotherRoll", async ctx =>
        {
            // 设计稿 v4 5.5.5：方案列表"复制来 ▸"，选其他辊的方案，右边长度对齐与叠图，✓ 复制到本辊（不设为当前）。
            IGrindingPlanService plans = h.Services.GetRequiredService<IGrindingPlanService>();
            if (await plans.CurrentAsync(SelfTestNames.JobRollId, default) is null)
            {
                ctx.Skip("the self-test roll has no plan");
                return;
            }

            int before = (await plans.ListAsync(SelfTestNames.JobRollId, default)).Count;
            h.Services.GetRequiredService<JobDraft>().PlanRollId = SelfTestNames.JobRollId;
            await h.GoToAsync(PageKey.Plans, ctx);
            PlansViewModel page = h.Page<PlansViewModel>();
            await page.Loading;
            await h.PressVerticalKeyAsync(ctx, "Vk_CopyFrom");
            bool opened = await h.WaitUntilAsync(() => page.IsPicking, TimeSpan.FromSeconds(10));
            ctx.Check(opened, "'copy from' should open the source picker");
            if (page.Picker.Items.Count == 0)
            {
                page.TryDismissPrompt();
                ctx.Skip("no other roll has a plan");
                return;
            }

            await h.WaitUntilAsync(() => page.Picker.CopyResult is not null, TimeSpan.FromSeconds(10));
            ctx.Check(page.Picker.ProgramRows.Count >= 3, "the preview should list the length alignment");
            h.TryScreenshot("plans-copy-preview");
            if (!page.Picker.CanCopy)
            {
                page.TryDismissPrompt();
                ctx.Skip("the first source cannot be copied to this roll: " + page.Picker.CopyResult?.ProblemKey);
                return;
            }

            await h.PressVerticalKeyAsync(ctx, "Vk_CopyToRoll");
            bool copied = await h.WaitUntilAsync(() => !page.IsPicking && page.Rows.Count == before + 1, TimeSpan.FromSeconds(10));
            ctx.Check(copied, "the copy should add one plan to this roll");
            PlanRowViewModel? added = page.Rows.FirstOrDefault(row => !row.IsCurrent && row.Overview.Plan.SourceKind == PlanSourceKind.OtherRollPlan);
            ctx.Check(added is not null, "the new plan remembers it came from another roll and is not current");
            if (added is not null)
            {
                await plans.DeleteAsync(added.PlanId, "selftest", default);
            }

            await h.GoToAsync(PageKey.Job, ctx);
        }, StepOptions.Shot);

        await h.StepAsync("Plan", "CompareWithCurrent", async ctx =>
        {
            // 设计稿 v4 5.5.1 竖键"对比"：选中的方案与当前方案逐项比、辊形叠图；只看不同 / 全部；8 返回。
            IGrindingPlanService plans = h.Services.GetRequiredService<IGrindingPlanService>();
            if (await plans.CurrentAsync(SelfTestNames.JobRollId, default) is not { } current)
            {
                ctx.Skip("the self-test roll has no plan");
                return;
            }

            GrindingPlanRecord copy = await plans.CopyAsync(current.PlanId, "selftest-compare", setCurrent: false, "selftest", default);
            try
            {
                h.Services.GetRequiredService<JobDraft>().PlanRollId = SelfTestNames.JobRollId;
                await h.GoToAsync(PageKey.Plans, ctx);
                PlansViewModel page = h.Page<PlansViewModel>();
                await page.Loading;
                await h.WaitUntilAsync(() => page.Rows.Any(row => row.PlanId == copy.PlanId), TimeSpan.FromSeconds(10));
                page.SelectedRow = page.Rows.FirstOrDefault(row => row.PlanId == copy.PlanId);
                ctx.Check(page.SelectedRow is not null && page.CompareBaseline(page.SelectedRow)?.IsCurrent == true,
                    "a plan that is not current compares with the current one");

                await h.PressVerticalKeyAsync(ctx, "Vk_Compare");
                bool opened = await h.WaitUntilAsync(() => page.IsCompareOpen, TimeSpan.FromSeconds(10));
                ctx.Check(opened, "'compare' should open the comparison");
                ctx.Check(page.Compare.OnlyDifferences && page.Compare.Rows.All(row => row.Differs), "the comparison starts with the differences only");
                ctx.Check(page.Compare.HasCurve, "both profiles are overlaid");
                await h.PressVerticalKeyAsync(ctx, "Vk_CompareAll");
                ctx.Check(await h.WaitUntilAsync(() => page.Compare.Rows.Count > 0, TimeSpan.FromSeconds(5)), "'all items' lists every item");
                h.TryScreenshot("plans-compare");
                await h.PressVerticalKeyAsync(ctx, "Vk_Back");
                ctx.Check(await h.WaitUntilAsync(() => !page.IsCompareOpen, TimeSpan.FromSeconds(5)), "back closes the comparison");
            }
            finally
            {
                await plans.DeleteAsync(copy.PlanId, "selftest", default);
                await h.GoToAsync(PageKey.Job, ctx);
            }
        }, StepOptions.Shot);

        await h.StepAsync("Template", "CompareWithPreviousVersion", async ctx =>
        {
            // 设计稿 v4 5.6.1 横键"对比"：模板现在这一版与上一版。
            await h.GoToAsync(PageKey.ProfileLibrary, ctx);
            LibraryViewModel library = h.Services.GetServices<PageViewModelBase>().OfType<LibraryViewModel>().First(page => page.IsProfiles);
            await library.Loading;
            LibraryEntryViewModel? withHistory = null;
            foreach (LibraryEntryViewModel entry in library.Entries.ToArray())
            {
                library.SelectedEntry = entry;
                if (await h.WaitUntilAsync(() => library.Versions.Count > 0, TimeSpan.FromSeconds(2)))
                {
                    withHistory = entry;
                    break;
                }
            }

            if (withHistory is null)
            {
                ctx.Skip("no profile template has an earlier version");
                return;
            }

            await h.PressKeyAsync(ctx, "Fn_Compare");
            bool opened = await h.WaitUntilAsync(() => library.IsCompareOpen, TimeSpan.FromSeconds(10));
            ctx.Check(opened, "'compare' should open the comparison of the two versions");
            h.TryScreenshot("template-compare");
            await h.PressVerticalKeyAsync(ctx, "Vk_Back");
            ctx.Check(await h.WaitUntilAsync(() => !library.IsCompareOpen, TimeSpan.FromSeconds(5)), "back closes the comparison");
        }, StepOptions.Shot);

        await h.StepAsync("Template", "MandateViewOpensAndCancels", async ctx =>
        {
            // 设计稿 v4 5.6.2：模板库"必须采用…"——有复制自旧版的辊就列逐支决定，没有就在对话行说；取消不留东西。
            await h.GoToAsync(PageKey.ProfileLibrary, ctx);
            LibraryViewModel library = h.Services.GetServices<PageViewModelBase>().OfType<LibraryViewModel>().First(page => page.IsProfiles);
            await library.Loading;
            if (library.SelectedEntry is null)
            {
                ctx.Skip("the profile template library is empty");
                return;
            }

            await h.PressVerticalKeyAsync(ctx, "Vk_Mandate");
            await h.SettleAsync(500);
            if (library.IsMandating)
            {
                ctx.Check(library.MandateRows.Count > 0, "the decision list should list the rolls");
                h.TryScreenshot("template-mandate");
                await h.PressVerticalKeyAsync(ctx, "Vk_Cancel");
                ctx.Check(await h.WaitUntilAsync(() => !library.IsMandating, TimeSpan.FromSeconds(5)), "cancel closes the decision list");
            }
            else
            {
                ctx.Note("no roll copied from an older version: nothing to adopt");
            }
        });
    }
}
