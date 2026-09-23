using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;
using RollGrinder.Core.Steps;
using RollGrinder.Services.Records;

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

            // 磨前测量 → 粗磨 → 磨后测量 → 圆度：把测量分阶段落库、圆度存档都走一遍。
            string[] sequence = { StepTypeKeys.Measure, StepTypeKeys.Rough, StepTypeKeys.Measure, StepTypeKeys.Roundness };
            foreach (string key in sequence)
            {
                StepTypeOptionViewModel? option = steps.StepTypeOptions.FirstOrDefault(o => o.Key == key);
                ctx.Check(option is not null && option.IsAvailable, "step type " + key + " is not available on this machine");
                steps.SelectedStepType = option;
                await h.RunAsync(steps.AddStepCommand);
            }

            string[] wanted =
            {
                ProgramOptionKeys.PreGrindMeasure, ProgramOptionKeys.PostGrindMeasure,
                ProgramOptionKeys.PrintPreGrindData, ProgramOptionKeys.PrintPostGrindData,
            };
            foreach (ProgramOptionRowViewModel option in steps.ProgramOptions.Where(o => o.IsAvailable))
            {
                if (wanted.Contains(option.Descriptor.Key))
                {
                    option.IsOn = true;
                }
            }

            await h.RunAsync(steps.ValidateCommand);
            ctx.Check(steps.StatusResourceKey == "Job_ReadyToHandOver", "job should validate, status " + steps.StatusResourceKey);
            ctx.Note("job " + steps.JobId + ", steps " + string.Join(">", sequence) + ", duration " + steps.TotalDurationText);
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

        await h.StepAsync("Run", "EndEarlyConfirmationExpires", async ctx =>
        {
            // 规范（手动动作规范.md）：危险动作第一下只预备，4 秒内不按第二下自动撤销。
            // 这里只按一下，等过窗口，确认它自己撤销了——不真的提前结束这一道。
            await h.PressKeyAsync(ctx, "Fn_EndEarly");
            if (h.IndexOfKey("Fn_ConfirmAgain") < 0)
            {
                ctx.Note("end-early refused without arming in the current state");
                return;
            }

            bool expired = await h.WaitUntilAsync(() => h.IndexOfKey("Fn_ConfirmAgain") < 0, TimeSpan.FromSeconds(6));
            ctx.Check(expired, "an armed end-early should disarm by itself after the 4-second window");
            ctx.Check(h.IndexOfKey("Fn_EndEarly") >= 0, "the key should be labelled 'end early' again");
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

        await h.StepAsync("After", "PreGrindSheetPrintedAutomatically", async ctx =>
        {
            bool printed = await h.WaitUntilAsync(
                () => h.Interaction.Produced.Count(p => p.Contains("-auto-", StringComparison.Ordinal)) > printsBefore,
                TimeSpan.FromSeconds(15));
            int count = h.Interaction.Produced.Count(p => p.Contains("-auto-", StringComparison.Ordinal)) - printsBefore;
            ctx.Note(Invariant($"{count} automatic print(s): ") + string.Join(", ", h.Interaction.Produced.Where(p => p.Contains("-auto-", StringComparison.Ordinal)).Select(Path.GetFileName)));
            ctx.Check(printed, "with 'print pre-grind data' on, the pre-grind sheet should print without asking");
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

        await h.StepAsync("After", "FinishRecordOnRecordsPage", async ctx =>
        {
            // 现行设计：记录由操作员在记录页点"完成"收尾；收尾时定格砂轮直径、存圆度、出磨后报告。
            RecordsViewModel records = h.Page<RecordsViewModel>();
            RecordRowViewModel? row = records.Records.FirstOrDefault(r => r.RollCode == SelfTestNames.FlowRollId);
            if (row is null)
            {
                ctx.Skip("no record to finish");
            }

            int autoBefore = h.Interaction.Produced.Count(p => p.Contains("-auto-", StringComparison.Ordinal));
            records.SelectedRecord = row;
            records.Note = "self-test";
            await h.RunAsync(records.FinishSelectedCommand);
            ctx.Check(records.StatusResourceKey == "Records_Finished", "status should say finished, is " + records.StatusResourceKey);

            RecordRowViewModel? finished = records.Records.FirstOrDefault(r => r.RollCode == SelfTestNames.FlowRollId);
            ctx.Check(finished?.View.FinishedAtUtc is not null, "the record should carry a finish time");
            ctx.Note("state=" + finished?.StateText + ", duration=" + finished?.DurationText + ", worst=" + finished?.WorstDeviationText);

            bool postPrinted = await h.WaitUntilAsync(
                () => h.Interaction.Produced.Count(p => p.Contains("-auto-", StringComparison.Ordinal)) > autoBefore,
                TimeSpan.FromSeconds(15));
            ctx.Check(postPrinted, "with 'print post-grind data' on, finishing should print the grinding report without asking");
            ctx.Note("printed " + Path.GetFileName(h.Interaction.LastProduced));
        }, StepOptions.Shot);

        await h.StepAsync("After", "RecordCurvesHaveData", async ctx =>
        {
            RecordsViewModel records = h.Page<RecordsViewModel>();
            records.SelectedRecord = records.Records.FirstOrDefault(r => r.RollCode == SelfTestNames.FlowRollId);
            if (records.SelectedRecord is null)
            {
                ctx.Skip("no record");
            }

            await h.SettleAsync(300);
            var empty = new System.Collections.Generic.List<string>();
            foreach (RecordCurveKind kind in Enum.GetValues<RecordCurveKind>())
            {
                records.SelectCurveCommand.Execute(kind);
                await h.SettleAsync(300);
                ctx.Note(kind + (records.CurveHasData ? "=data" : "=empty"));
                h.TryScreenshot("flow-record-curve-" + kind);
                if (!records.CurveHasData && kind != RecordCurveKind.CompensationConvergence)
                {
                    empty.Add(kind.ToString());
                }
            }

            ctx.Check(empty.Count == 0, "a job with pre/post measurement and roundness should fill these curves: " + string.Join(", ", empty));
        });
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
