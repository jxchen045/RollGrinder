using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;
using RollGrinder.Core.Profiles;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.App.SelfTest;

/// <summary>
/// 作业页一步步走：辊（台账里选，或当场去台账登记）→ 辊形（长度对不上要选拉伸 / 居中）→ 工艺程序 → 开关 → 核对。
/// 自检套件和全流程共用。
/// </summary>
internal static class JobWizard
{
    /// <summary>从工艺程序页按"用于作业"进作业页：程序跟着带过去。</summary>
    public static async Task EnterFromProgramAsync(SelfTestHarness h, StepContext ctx, string programName)
    {
        StepsViewModel steps = h.Page<StepsViewModel>();
        JobViewModel job = h.Page<JobViewModel>();
        await h.GoToAsync(PageKey.Steps, ctx);
        if (steps.ProgramName != programName || steps.ProgramId is null || steps.IsDirty)
        {
            await h.RunAsync(steps.OpenProgramLibraryCommand);
            steps.SelectedProgramEntry = steps.ProgramLibraryEntries.FirstOrDefault(p => p.Name == programName);
            ctx.Check(steps.SelectedProgramEntry is not null, "program '" + programName + "' should be in the library");
            await h.RunAsync(steps.LoadProgramCommand);
        }

        await h.PressKeyAsync(ctx, "Fn_UseForJob");
        ctx.Check(h.Shell.CurrentPage.Key == PageKey.Job, "'use for job' should open the job page, is " + h.Shell.CurrentPage.Key);
        await job.Loading;
        ctx.Check(job.SelectedProgram?.Name == programName, "the program should be carried into the job");
    }

    /// <summary>在作业页按"新登记轧辊"，去台账填表、存，按导航槽回作业页；回来时这支辊应已选上。</summary>
    public static async Task RegisterRollAsync(
        SelfTestHarness h, StepContext ctx, string rollId, double bodyLengthMm, double diameterMm, double currentDiameterMm)
    {
        JobViewModel job = h.Page<JobViewModel>();
        RecordsViewModel records = h.Page<RecordsViewModel>();
        await h.PressKeyAsync(ctx, "Fn_RegisterRoll");
        ctx.Check(h.Shell.CurrentPage.Key == PageKey.Records, "'register roll' should open the roll ledger");
        bool formReady = await h.WaitUntilAsync(
            () => records.ActiveSubViewKey == RecordsViewModel.LedgerSubView && records.IsNewLedgerRoll, TimeSpan.FromSeconds(10));
        ctx.Check(formReady, "the ledger should open on an empty form");

        records.LedgerRollId = rollId;
        records.SetLedgerKindCommand.Execute(RollKind.WorkRoll);
        records.LedgerBodyLengthText = Format(bodyLengthMm);
        records.LedgerDiameterText = Format(diameterMm);
        records.LedgerCurrentDiameterText = Format(currentDiameterMm);
        await h.RunAsync(records.SaveLedgerRollCommand);
        bool alreadyThere = records.LedgerProblems.Count > 0;
        if (alreadyThere)
        {
            // 同一个数据目录上次自检已登记过这支辊：直接回去选它。
            ctx.Note("roll already registered: " + string.Join(" | ", records.LedgerProblems));
        }

        h.TryScreenshot("job-register-roll");
        for (int i = 0; i < 3 && h.Shell.CurrentPage.Key != PageKey.Job; i++)
        {
            await h.PressNavigationKeyAsync();
            if (h.Shell.IsLeaveConfirmOpen)
            {
                await h.RunAsync(h.Shell.DiscardAndLeaveCommand);
            }
        }

        ctx.Check(h.Shell.CurrentPage.Key == PageKey.Job, "the navigation key should lead back to the job page");
        await job.Loading;
        if (alreadyThere)
        {
            job.SelectedRoll = job.Rolls.FirstOrDefault(r => r.RollId == rollId);
            await h.SettleAsync(300);
        }

        ctx.Check(job.SelectedRoll?.RollId == rollId, "the registered roll should be selected, is " + job.SelectedRoll?.RollId);
    }

    /// <summary>辊形那一步：选库里这条辊形；长度对不上就选"拉伸"。返回是否对不上。</summary>
    public static async Task<bool> ChooseProfileAsync(SelfTestHarness h, StepContext ctx, string profileName)
    {
        JobViewModel job = h.Page<JobViewModel>();
        ctx.Check(job.ActiveStep == JobViewModel.ProfileStep, "should be on the profile step, is " + job.ActiveStep);
        job.SelectedProfile = job.Profiles.FirstOrDefault(p => p.Name == profileName);
        if (job.SelectedProfile is null)
        {
            ctx.Skip("profile '" + profileName + "' is not in the library");
        }

        bool loaded = await h.WaitUntilAsync(() => job.LengthCheckText.Length > 0, TimeSpan.FromSeconds(10));
        ctx.Check(loaded, "the length check should be shown");
        ctx.Note("length: " + job.LengthCheckText);
        if (job.LengthMismatch)
        {
            ctx.Check(!IsKeyEnabled(h, "Fn_NextStep"), "with a length mismatch 'next' waits for a choice");
            await h.RunAsync(job.ChooseFitCommand, ProfileFitMode.Stretch);
            ctx.Check(job.IsStretchChosen, "stretch should be chosen");
        }

        ctx.Check(IsKeyEnabled(h, "Fn_NextStep"), "'next' should be enabled once the profile is settled");
        return job.LengthMismatch;
    }

    /// <summary>从当前一步按"下一步"走到核对页。</summary>
    public static async Task NextUntilReviewAsync(SelfTestHarness h, StepContext ctx)
    {
        JobViewModel job = h.Page<JobViewModel>();
        while (job.ActiveStep < JobViewModel.ReviewStep)
        {
            int before = job.ActiveStep;
            await h.PressKeyAsync(ctx, "Fn_NextStep");
            ctx.Check(job.ActiveStep == before + 1, "'next' should move from step " + before + ", is " + job.ActiveStep);
            if (job.ActiveStep == before)
            {
                return;
            }
        }
    }

    public static bool IsKeyEnabled(SelfTestHarness h, string key)
    {
        int index = h.IndexOfKey(key);
        return index >= 0 && h.Shell.FunctionKeys[index].IsEnabled;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
}

/// <summary>作业页：从工艺程序页带程序进来、当场登记辊、辊形长度核对与拉伸、核对页校验、离线时下发被拒。</summary>
internal sealed class JobSuite : ISelfTestSuite
{
    public string Name => "Job";

    public async Task RunAsync(SelfTestHarness h)
    {
        JobViewModel job = h.Page<JobViewModel>();
        bool offline = h.Services.GetRequiredService<RollGrinder.Contracts.IAppOptions>().IsOffline;

        StepStatus entered = await h.StepAsync("Wizard", "EnterFromProgramPage", async ctx =>
        {
            await JobWizard.EnterFromProgramAsync(h, ctx, SelfTestNames.ProgramA);
            ctx.Check(job.ActiveStep == JobViewModel.RollStep || job.SelectedRoll is not null, "a fresh job starts on the roll step");
        }, StepOptions.Shot);

        if (entered != StepStatus.Pass && entered != StepStatus.Warn)
        {
            h.Note("could not reach the job page: the job suite is skipped");
            return;
        }

        await h.StepAsync("Wizard", "NewJobClearsRoll", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_NewJob");
            ctx.Check(job.ActiveStep == JobViewModel.RollStep && job.SelectedRoll is null, "a new job starts on step 1 with no roll");
            ctx.Check(!JobWizard.IsKeyEnabled(h, "Fn_NextStep"), "'next' needs a roll");
        });

        await h.StepAsync("Wizard", "RegisterRollFromJob", async ctx =>
        {
            // 辊身比辊形设计长度短 100 mm：让长度核对这一步一定要人选。
            RollProfileSummary? profile = (await h.Services.GetRequiredService<IRollProfileRepository>()
                    .ListAsync(200, default))
                .FirstOrDefault(p => p.Name == SelfTestNames.ProfileA);
            double body = profile is null ? 2000 : Math.Max(500, profile.BodyLengthMm - 100);
            await JobWizard.RegisterRollAsync(h, ctx, SelfTestNames.JobRollId, body, 600, 596);
            ctx.Check(job.RollSummaryText.Length > 0, "the roll size should be summarised");
            ctx.Note(job.RollSummaryText);
        }, StepOptions.Shot);

        await h.StepAsync("Wizard", "ProfileLengthCheck", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_NextStep");
            bool mismatch = await JobWizard.ChooseProfileAsync(h, ctx, SelfTestNames.ProfileA);
            ctx.Check(mismatch, "a roll 100 mm shorter than the profile design should ask stretch or center");
            h.TryScreenshot("job-profile-fit");
        });

        await h.StepAsync("Wizard", "ProgramAndOptions", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_NextStep");
            ctx.Check(job.ActiveStep == JobViewModel.ProgramStep, "should be on the program step");
            ctx.Check(job.ProgramSteps.Count >= 2, "the chosen program's steps should be listed");
            ctx.Note(job.ProgramSteps.Count + " steps, " + job.TotalDurationText);
            await h.PressKeyAsync(ctx, "Fn_NextStep");
            ctx.Check(job.ActiveStep == JobViewModel.OptionsStep, "should be on the options step");
            ctx.Check(job.ProgramOptions.Any(o => o.IsAvailable), "program options should be offered");
        });

        await h.StepAsync("Wizard", "ReviewValidates", async ctx =>
        {
            await JobWizard.NextUntilReviewAsync(h, ctx);
            ctx.Check(job.ReviewRows.Count > 0, "the review should list the job");
            ctx.Check(job.StatusResourceKey == "Job_ReadyToHandOver",
                "the job should validate, status " + job.StatusResourceKey
                + (job.Violations.Count > 0 ? ", first violation: " + job.Violations[0].ParameterText + " " + job.Violations[0].ReasonText : string.Empty));
            ctx.Check(job.BuildJob() is { } built && built.ProgramName == SelfTestNames.ProgramA, "the built job should carry the program");
        }, StepOptions.Shot);

        await h.StepAsync("Wizard", "PreviousStepGoesBack", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_PreviousStep");
            ctx.Check(job.ActiveStep == JobViewModel.OptionsStep, "'previous' should go back one step");
            await h.PressKeyAsync(ctx, "Fn_NextStep");
            ctx.Check(job.ActiveStep == JobViewModel.ReviewStep, "and 'next' forward again");
        });

        if (offline)
        {
            await h.StepAsync("Download", "RefusedWithoutMachine", async ctx =>
            {
                await h.PressKeyAsync(ctx, "Fn_DownloadNc");
                ctx.Check(job.StatusResourceKey != "Job_HandedOver", "download must not claim success without a machine");
                ctx.Check(h.Shell.CurrentPage.Key == PageKey.Job, "a refused download stays on the job page");
                ctx.Note("status=" + job.StatusResourceKey);
            }, StepOptions.Expect("*"));
        }

        await h.StepAsync("Wizard", "BackToProgramPage", async ctx =>
        {
            await h.PressNavigationKeyAsync();
            ctx.Check(h.Shell.CurrentPage.Key == PageKey.Steps, "the navigation key should return to the program page");
        });
    }
}
