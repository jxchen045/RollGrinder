using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;
using RollGrinder.Core.Steps;

namespace RollGrinder.App.SelfTest;

/// <summary>
/// 一支辊的全流程（仿真机床）：
/// 建作业 → 校验 → 下发 → 开磨 → 运行中编辑页锁只读、下发键变灰 → 运行中的操作（启动两下、保持、冷却、
/// 曲线、跳步/提前结束）→ 磨完 → 自动打印报表 → 生成磨削记录。
///
/// 注意：仿真机床收到下发的"参数有效"就直接开磨，不等循环启动信号；真 NC 会等。
/// 所以这里"启动"键只验证按两下的确认流程与请求能发出去，不以它作为开磨的前提。
/// </summary>
internal sealed class FullFlowSuite : ISelfTestSuite
{
    /// <summary>等磨完的上限。仿真 20 倍速下一支辊约 15 秒。</summary>
    private static readonly TimeSpan GrindTimeout = TimeSpan.FromMinutes(4);

    public string Name => "FullFlow";

    public async Task RunAsync(SelfTestHarness h)
    {
        ShellViewModel shell = h.Shell;
        StepsViewModel steps = h.Page<StepsViewModel>();
        AutoGrindingViewModel auto = h.Page<AutoGrindingViewModel>();
        int printsBefore = h.Interaction.Produced.Count(p => p.Contains("-auto-", StringComparison.Ordinal));

        await h.StepAsync("Prepare", "BuildJob", async ctx =>
        {
            await h.GoToAsync(PageKey.Steps, ctx);
            await h.RunAsync(steps.NewJobCommand);
            steps.RollId = SelfTestNames.FlowRollId;
            steps.BodyLengthMmText = SelfTestNames.BodyLengthMm;
            steps.NominalDiameterMmText = SelfTestNames.DiameterMm;
            if (steps.UsesLibraryProfile)
            {
                await h.RunAsync(steps.ClearProfileSelectionCommand);
            }

            StepTypeOptionViewModel? rough = steps.StepTypeOptions.FirstOrDefault(o => o.IsAvailable && o.SlotKey == StepSlotKeys.Rough);
            ctx.Check(rough is not null, "no rough grinding step type is available");
            steps.SelectedStepType = rough;
            await h.RunAsync(steps.AddStepCommand);

            foreach (ProgramOptionRowViewModel option in steps.ProgramOptions.Where(o => o.IsAvailable))
            {
                if (option.Descriptor.Key is ProgramOptionKeys.PrintPreGrindData or ProgramOptionKeys.PrintPostGrindData)
                {
                    option.IsOn = true;
                }
            }

            await h.RunAsync(steps.ValidateCommand);
            ctx.Check(steps.StatusResourceKey == "Job_ReadyToHandOver", "job should validate, status " + steps.StatusResourceKey);
            ctx.Note("job " + steps.JobId + ", step " + rough!.Key + ", duration " + steps.TotalDurationText);
        }, StepOptions.Shot);

        StepStatus download = await h.StepAsync("Run", "DownloadToNc", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_DownloadNc");
            ctx.Check(steps.StatusResourceKey == "Job_HandedOver", "download should succeed, status " + steps.StatusResourceKey
                + (steps.Violations.Count > 0 ? ", first violation " + steps.Violations[0].ParameterText + " " + steps.Violations[0].ReasonText : string.Empty));
            ctx.Check(!steps.IsDirty, "after a successful download the page matches the machine and is clean");
        }, StepOptions.Shot);

        if (download != StepStatus.Pass && download != StepStatus.Warn)
        {
            h.Note("download failed: the rest of the full flow is skipped");
            return;
        }

        await h.StepAsync("Run", "EnterAutoAndStart", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_EnterAuto");
            ctx.Check(shell.CurrentPage.Key == PageKey.AutoGrinding, "'enter auto' should open the auto page");
            bool running = await h.WaitUntilAsync(() => shell.IsMachineRunning, TimeSpan.FromSeconds(15));
            ctx.Check(running, "the simulated machine should start grinding after the download");
            ctx.Note(Invariant($"sequence rows={auto.Sequence.Count}, live values={auto.LiveValues.Count}"));
        }, StepOptions.Shot);

        await h.StepAsync("RunLock", "EditPagesReadOnly", async ctx =>
        {
            ctx.Check(steps.IsReadOnly, "steps page must be read-only while the cycle runs");
            ctx.Check(h.Page<ProfileViewModel>().IsReadOnly, "profile page must be read-only while the cycle runs");
            await h.GoToAsync(PageKey.Steps, ctx);
            int downloadKey = h.IndexOfKey("Fn_DownloadNc");
            ctx.Check(downloadKey >= 0 && !shell.FunctionKeys[downloadKey].IsEnabled, "download must be disabled while running");
            h.TryScreenshot("runlock-steps");
            await h.GoToAsync(PageKey.AutoGrinding, ctx);
        });

        await h.StepAsync("Run", "LiveValuesMove", async ctx =>
        {
            string first = auto.ProgressText + "|" + auto.RemainingStockText + "|" + auto.MeasuredDiameterText;
            await Task.Delay(TimeSpan.FromSeconds(2));
            string second = auto.ProgressText + "|" + auto.RemainingStockText + "|" + auto.MeasuredDiameterText;
            ctx.Note("t0=" + first + "  t2=" + second);
            ctx.Check(first != second || !shell.IsMachineRunning, "live values should change while grinding");
        });

        await h.StepAsync("Run", "CurvesWhileGrinding", async ctx =>
        {
            foreach (CurveKind kind in Enum.GetValues<CurveKind>())
            {
                auto.SelectCurveCommand.Execute(kind);
                await h.SettleAsync(200);
                ctx.Note(kind + (auto.CurveHasData ? "=data" : "=empty"));
                h.TryScreenshot("auto-curve-" + kind);
            }
        });

        await h.StepAsync("Run", "StartNeedsTwoPresses", async ctx =>
        {
            // 正在磨时按启动，服务层可以直接拒绝（不进入"再按一次"）；两种结果都照实记下。
            await h.PressKeyAsync(ctx, "Fn_Start");
            if (h.IndexOfKey("Fn_ConfirmAgain") < 0)
            {
                ctx.Note("start refused without arming in the running state");
                return;
            }

            ctx.Note("armed: key relabelled 'confirm again'");
            await h.PressKeyAsync(ctx, "Fn_ConfirmAgain");
            ctx.Check(h.IndexOfKey("Fn_Start") >= 0, "second press should send and restore the label");
        }, new StepOptions(Tolerant: true));

        await h.StepAsync("Run", "FeedHoldAndCoolant", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_Pause");
            await h.PressKeyAsync(ctx, "Fn_Coolant");
            await h.PressKeyAsync(ctx, "Fn_Coolant");
        }, new StepOptions(Tolerant: true));

        await h.StepAsync("Run", "SkipAndEndEarlyNeedConfirmation", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_EndEarly");
            bool armed = h.IndexOfKey("Fn_ConfirmAgain") >= 0;
            ctx.Note("end-early armed=" + armed);
            if (armed)
            {
                // 另一个键会把确认撤掉：这里按"保持"验证撤销，而不真的提前结束这一道。
                await h.PressKeyAsync(ctx, "Fn_Pause");
                ctx.Check(h.IndexOfKey("Fn_ConfirmAgain") < 0, "pressing another key should disarm the pending confirmation");
            }

            SequenceRowViewModel? later = auto.Sequence.Skip(1).FirstOrDefault(r => r.JumpCommand.CanExecute(null));
            ctx.Note("jump target available=" + (later is not null));
        }, new StepOptions(Tolerant: true));

        await h.StepAsync("Run", "GrindsToCompletion", async ctx =>
        {
            bool finished = await h.WaitUntilAsync(() => !shell.IsMachineRunning, GrindTimeout, 500);
            ctx.Check(finished, Invariant($"the simulated grind should finish within {GrindTimeout.TotalMinutes:0} minutes"));
            ctx.Note("measured=" + auto.MeasuredDiameterText + ", target=" + auto.TargetDiameterText + ", rms=" + auto.RmsText + ", tolerance=" + auto.ToleranceText);
            await h.SettleAsync(1000);
        }, new StepOptions(Screenshot: true, TimeoutSeconds: GrindTimeout.TotalSeconds + 30, Tolerant: true));

        await h.StepAsync("After", "EditPagesUnlocked", ctx =>
        {
            ctx.Check(!steps.IsReadOnly, "steps page should unlock after the cycle");
            return Task.CompletedTask;
        });

        await h.StepAsync("After", "ReportsPrintedAutomatically", async ctx =>
        {
            bool printed = await h.WaitUntilAsync(
                () => h.Interaction.Produced.Count(p => p.Contains("-auto-", StringComparison.Ordinal)) > printsBefore,
                TimeSpan.FromSeconds(15));
            int count = h.Interaction.Produced.Count(p => p.Contains("-auto-", StringComparison.Ordinal)) - printsBefore;
            ctx.Note(Invariant($"{count} automatic print(s): ") + string.Join(", ", h.Interaction.Produced.Where(p => p.Contains("-auto-", StringComparison.Ordinal)).Select(Path.GetFileName)));
            ctx.Check(printed, "with 'print post-grind data' on, finishing should print a report without asking");
        });

        await h.StepAsync("After", "RecordCreated", async ctx =>
        {
            RecordsViewModel records = h.Page<RecordsViewModel>();
            await h.GoToAsync(PageKey.Records, ctx);
            records.FromDate = DateTime.Today.AddDays(-1);
            records.ToDate = DateTime.Today;
            bool found = false;
            for (int i = 0; i < 10 && !found; i++)
            {
                await h.RunAsync(records.QueryCommand);
                found = records.Records.Any(r => r.RollCode == SelfTestNames.FlowRollId);
                if (!found)
                {
                    await Task.Delay(1000);
                }
            }

            ctx.Check(found, "a grinding record for " + SelfTestNames.FlowRollId + " should exist");
            RecordRowViewModel? row = records.Records.FirstOrDefault(r => r.RollCode == SelfTestNames.FlowRollId);
            ctx.Note(row is null ? "none" : "state=" + row.StateText + ", duration=" + row.DurationText + ", worst=" + row.WorstDeviationText);
        }, StepOptions.Shot);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
