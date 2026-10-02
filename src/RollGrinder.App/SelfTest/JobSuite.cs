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

/// <summary>作业页：待磨清单、尾号查找、当场登记（计划必填）、一页核对、改磨削量、换辊形仅本次（选原因）、回到计划、离线下发被拒。</summary>
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
            // 辊身比辊形设计长度短 1%：在 2% 以内，中间段伸缩、两端锥度不动。
            RollProfileSummary? profile = (await h.Services.GetRequiredService<IRollProfileRepository>().ListAsync(200, default))
                .FirstOrDefault(p => p.Name == SelfTestNames.ProfileA);
            double body = profile is null ? 2000 : Math.Round(profile.BodyLengthMm * 0.99);
            await JobFlow.RegisterAndOpenAsync(h, ctx, SelfTestNames.JobRollId, body, 600, 596, SelfTestNames.ProfileA, SelfTestNames.ProgramA);
            ctx.Note(job.ReviewTitle + " · " + job.CheckSummaryText);
        }, StepOptions.Shot);

        await h.StepAsync("Review", "ChecklistPasses", ctx =>
        {
            ctx.Check(job.IsReview, "should be on the review");
            ctx.Check(job.Checks.Any(c => c.ItemText == job.Localizer["CheckItem_Length"]), "the length check (2% rule) should be listed");
            ctx.Check(job.CanDownload, "the job should pass, first block: " + JobFlow.FirstBlock(job));
            ctx.Check(job.BuildJob() is { } built && built.ProgramName == SelfTestNames.ProgramA && built.ProfileVersion is not null,
                "the built job should carry the program and the profile version");
            ctx.Check(job.ReviewCurve.Count > 1, "the review should draw the target profile");
            return Task.CompletedTask;
        });

        await h.StepAsync("Review", "StockEditsRecheck", async ctx =>
        {
            string before = job.StockText;
            job.StockText = "abc";
            await h.SettleAsync();
            ctx.Check(!job.CanDownload && h.IndexOfVerticalKey("Vk_ConfirmDownload") < 0, "a stock that is not a number blocks the download");
            job.StockText = before;
            await h.SettleAsync();
            ctx.Check(job.CanDownload, "restoring the stock passes again, first block: " + JobFlow.FirstBlock(job));
        });

        await h.StepAsync("Review", "ChangeProfileThisTimeNeedsReason", async ctx =>
        {
            await h.PressVerticalKeyAsync(ctx, "Vk_ChangeProfile");
            await h.WaitUntilAsync(() => job.IsPicking, TimeSpan.FromSeconds(10));
            PlanPickItemViewModel? other = job.Picker.Items.FirstOrDefault(i => i.Name != SelfTestNames.ProfileA);
            if (other is null)
            {
                job.TryDismissPrompt();
                ctx.Skip("no second profile fits this roll");
            }

            job.Picker.SelectedItem = other;
            await h.PressVerticalKeyAsync(ctx, "Vk_PickThis");
            ctx.Check(h.HasPendingConfirmation, "a profile other than the plan should ask 'this time only / make plan'");
            ctx.Check(h.IndexOfVerticalKey("Vk_MakePlan") >= 0, "'make plan' should sit on key 6");
            h.TryScreenshot("job-deviation-choice");
            await h.ConfirmAsync(ctx);
            bool applied = await h.WaitUntilAsync(() => job.Deviation == JobDeviation.ThisTimeOnly, TimeSpan.FromSeconds(5));
            ctx.Check(applied, "'this time only' should be applied");
            ctx.Check(!job.CanDownload, "this time only without a reason blocks the download");
            if (h.IndexOfVerticalKey("Reason_Trial") < 0)
            {
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

            await h.PressVerticalKeyAsync(ctx, "Vk_RestorePlan");
            bool restored = await h.WaitUntilAsync(() => job.Deviation == JobDeviation.None, TimeSpan.FromSeconds(5));
            ctx.Check(restored, "'restore plan' should go back to the ledger plan");
            ctx.Check(job.CanDownload, "the plan passes again, first block: " + JobFlow.FirstBlock(job));
        });

        if (offline)
        {
            await h.StepAsync("Download", "RefusedWithoutMachine", async ctx =>
            {
                await h.PressVerticalKeyAsync(ctx, "Vk_ConfirmDownload");
                await h.SettleAsync(500);
                ctx.Check(h.Shell.CurrentPage.Key == PageKey.Job && job.IsReview, "a refused download stays on the review");
            }, StepOptions.Expect("*"));
        }

        await h.StepAsync("Review", "BackToQueue", async ctx =>
        {
            await h.PressVerticalKeyAsync(ctx, "Vk_BackToQueue");
            bool back = await h.WaitUntilAsync(() => job.IsQueue, TimeSpan.FromSeconds(5));
            ctx.Check(back, "'back to queue' should close the review");
        });
    }
}
