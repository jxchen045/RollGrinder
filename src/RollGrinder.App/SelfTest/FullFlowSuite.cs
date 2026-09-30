using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.App.Interaction;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;
using RollGrinder.Core.Steps;
using RollGrinder.Data.Model;
using RollGrinder.Services.Records;

namespace RollGrinder.App.SelfTest;

/// <summary>
/// 一支辊的全流程（仿真机床）：
/// 编工艺程序并存库 → 用于作业 → 在作业页登记辊、选辊形、核对 → 下发（自动进自动加工页）→ 开磨
/// → 运行中编辑页锁只读 → 运行中的操作（启动两下、保持、冷却、
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
        JobViewModel job = h.Page<JobViewModel>();
        AutoGrindingViewModel auto = h.Page<AutoGrindingViewModel>();
        int printsBefore = h.Interaction.Produced.Count(p => p.Contains("-auto-", StringComparison.Ordinal));

        await h.StepAsync("Prepare", "BuildProgram", async ctx =>
        {
            await h.GoToAsync(PageKey.Steps, ctx);
            await SelfTestNames.NewProgramAsync(h, ctx, SelfTestNames.ProfileA);

            // 磨前测量 → 粗磨 → 辅助动作（样例机床登记了动作时）→ 磨后测量 → 圆度：
            // 把测量分阶段落库、圆度存档、辅助动作的下发与原地停留都走一遍。
            // 机床有修整装置时再加一道砂轮修整：NC 走完它，砂轮页的修整记录里要多一行。
            bool hasAuxiliary = steps.StepTypeOptions.Any(o => o.Key == StepTypeKeys.Auxiliary);
            bool hasDresser = steps.StepTypeOptions.Any(o => o.Key == StepTypeKeys.WheelDress && o.IsAvailable);
            var sequenceList = new List<string> { StepTypeKeys.Measure, StepTypeKeys.Rough };
            if (hasAuxiliary)
            {
                sequenceList.Add(StepTypeKeys.Auxiliary);
            }

            if (hasDresser)
            {
                sequenceList.Add(StepTypeKeys.WheelDress);
            }

            sequenceList.AddRange(new[] { StepTypeKeys.Measure, StepTypeKeys.Roundness });
            string[] sequence = sequenceList.ToArray();
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

            await h.PressKeyAsync(ctx, "Fn_Validate");
            ctx.Check(steps.StatusResourceKey == "Program_Valid", "program should validate, status " + steps.StatusResourceKey);
            ctx.Check(await SelfTestNames.SaveAsAsync(h, steps.SaveProgramAsCommand, steps.NamePrompt, SelfTestNames.FlowProgram),
                "the flow program should be saved, error: " + steps.NamePrompt.ErrorText);
            ctx.Note("steps " + string.Join(">", sequence) + ", duration " + steps.TotalDurationText);
        }, StepOptions.Shot);

        await h.StepAsync("Prepare", "BuildJob", async ctx =>
        {
            await JobWizard.EnterFromProgramAsync(h, ctx, SelfTestNames.FlowProgram);
            await JobWizard.NewJobAsync(h, ctx);
            await JobWizard.RegisterRollAsync(h, ctx, SelfTestNames.FlowRollId, 2000, 600, 600);
            await h.PressVerticalKeyAsync(ctx, "Vk_NextStep");
            await JobWizard.ChooseProfileAsync(h, ctx, SelfTestNames.ProfileA);
            await h.PressVerticalKeyAsync(ctx, "Vk_NextStep");
            job.SelectedProgram = job.Programs.FirstOrDefault(p => p.Name == SelfTestNames.FlowProgram);
            await h.SettleAsync(300);
            await JobWizard.NextUntilReviewAsync(h, ctx);
            ctx.Check(job.StatusResourceKey == "Job_ReadyToHandOver", "job should validate, status " + job.StatusResourceKey
                + (job.Violations.Count > 0 ? ", first violation " + job.Violations[0].ParameterText + " " + job.Violations[0].ReasonText : string.Empty));
            ctx.Note("job " + job.JobId + ", " + job.TotalDurationText);
        }, StepOptions.Shot);

        StepStatus download = await h.StepAsync("Run", "DownloadToNc", async ctx =>
        {
            await h.PressVerticalKeyAsync(ctx, "Vk_ConfirmDownload");
            bool moved = await h.WaitUntilAsync(() => shell.CurrentPage.Key == PageKey.AutoGrinding, TimeSpan.FromSeconds(10));
            ctx.Check(moved, "a successful download should open the auto page, status " + job.StatusResourceKey
                + (job.Violations.Count > 0 ? ", first violation " + job.Violations[0].ParameterText + " " + job.Violations[0].ReasonText : string.Empty));
            ctx.Check(job.StatusResourceKey == "Job_HandedOver", "status should say handed over, is " + job.StatusResourceKey);
        }, StepOptions.Shot);

        if (download != StepStatus.Pass && download != StepStatus.Warn)
        {
            h.Note("download failed: the rest of the full flow is skipped");
            return;
        }

        await h.StepAsync("Run", "GrindingStarts", async ctx =>
        {
            bool running = await h.WaitUntilAsync(() => shell.IsMachineRunning, TimeSpan.FromSeconds(15));
            ctx.Check(running, "the simulated machine should start grinding after the download");
            ctx.Note(Invariant($"sequence rows={auto.Sequence.Count}, live values={auto.LiveValues.Count}"));
        }, StepOptions.Shot);

        await h.StepAsync("RunLock", "EditPagesReadOnly", async ctx =>
        {
            ctx.Check(steps.IsReadOnly, "program page must be read-only while the cycle runs");
            ctx.Check(job.IsReadOnly, "job page must be read-only while the cycle runs");
            ctx.Check(h.Page<ProfileViewModel>().IsReadOnly, "profile page must be read-only while the cycle runs");
            await h.GoToAsync(PageKey.Steps, ctx);
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
                await h.PressVerticalKeyAsync(ctx, "Curve_" + kind);
                await h.SettleAsync(200);
                ctx.Note(kind + (auto.CurveHasData ? "=data" : "=empty"));
                h.TryScreenshot("auto-curve-" + kind);
            }
        });

        await h.StepAsync("Run", "CycleStartAsksFirst", async ctx =>
        {
            // 循环启动在按钮板上时没有这个键（machine.json panelActions）；有的话按下去先问、这里取消。
            if (h.IndexOfKey("Fn_CycleStart") < 0)
            {
                ctx.Skip("cycle start is on the operator panel");
            }

            await h.PressKeyAsync(ctx, "Fn_CycleStart");
            if (!h.HasPendingConfirmation)
            {
                ctx.Note("refused while running: " + h.DialogLineText);
                return;
            }

            ctx.Note("asked: " + h.PendingQuestion);
            await h.CancelConfirmationAsync();
        }, new StepOptions(Tolerant: true));

        await h.StepAsync("Run", "Coolant", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_Coolant");
            await h.PressKeyAsync(ctx, "Fn_Coolant");
        }, new StepOptions(Tolerant: true));

        await h.StepAsync("Run", "EndEarlyConfirmationExpires", async ctx =>
        {
            // 最终稿 D5：会让机床动的请求先问一句，5 秒不答自动取消。这里只按一下、不答，确认它自己撤销了。
            await h.PressKeyAsync(ctx, "Fn_EndEarly");
            if (!h.HasPendingConfirmation)
            {
                ctx.Note("end-early refused in the current state: " + h.DialogLineText);
                return;
            }

            bool expired = await h.WaitUntilAsync(() => !h.HasPendingConfirmation, ConfirmationService.Timeout + TimeSpan.FromSeconds(3));
            ctx.Check(expired, "an unanswered end-early should cancel itself after the timeout");
            ctx.Check(h.Shell.VerticalKeys[7].Kind != FunctionKeyKind.Confirm, "the confirm key should be gone again");
        }, new StepOptions(Tolerant: true));

        await h.StepAsync("Run", "GrindsToCompletion", async ctx =>
        {
            bool finished = await h.WaitUntilAsync(() => !shell.IsMachineRunning, GrindTimeout, 500);
            ctx.Check(finished, Invariant($"the simulated grind should finish within {GrindTimeout.TotalMinutes:0} minutes"));
            ctx.Note("measured=" + auto.MeasuredDiameterText + ", target=" + auto.TargetDiameterText + ", rms=" + auto.RmsText + ", tolerance=" + auto.ToleranceText);
            await h.SettleAsync(1000);
        }, new StepOptions(Screenshot: true, TimeoutSeconds: GrindTimeout.TotalSeconds + 30, Tolerant: true));

        await h.StepAsync("After", "AutoPageShowsRmsWithoutClicking", async ctx =>
        {
            // 磨后测量存下来后，RMS 自己换上，不用点误差曲线；也不随正在看哪条曲线变成"--"。
            auto.SelectCurveCommand.Execute(CurveKind.GrindingCurrent);
            bool shown = await h.WaitUntilAsync(() => auto.RmsText != "--", TimeSpan.FromSeconds(10));
            ctx.Note("rms=" + auto.RmsText);
            ctx.Check(shown, "the RMS should appear by itself once the post-grind measurement is stored");
        });

        // 阶段 3：顶上的状态带有数；补偿子视图里有 NC 的行程修正与这支辊的各次测量。
        await h.StepAsync("After", "StatusBandAndCompensationView", async ctx =>
        {
            ctx.Check(auto.StatusBand.Fields.All(field => field.Label.Length > 0), "the status band fields should be labelled");
            ctx.Note(string.Join(" · ", auto.StatusBand.Fields.Select(field => field.Label + " " + field.ValueText)));
            ctx.Note(string.Join(" · ", auto.StatusBand.Lamps.Select(lamp => lamp.Label + " " + lamp.StateText)));
            ctx.Check(auto.StatusBand.Lamps.Any(lamp => !lamp.IsUnknown), "the simulator should light at least one mechanism lamp");

            await h.GoToAsync(PageKey.AutoGrinding, ctx);
            await h.PressKeyAsync(ctx, "Fn_Compensation");
            ctx.Check(auto.ActiveSubViewKey == AutoGrindingViewModel.CompensationSubView, "the compensation view should open");
            bool measured = await h.WaitUntilAsync(() => auto.MeasurementConvergence.Count > 0, TimeSpan.FromSeconds(5));
            ctx.Check(measured, "the measurements of this roll should be listed with their distance to target");
            ctx.Note(Invariant($"stroke corrections={auto.StrokeCorrections.Count}, measurements={auto.MeasurementConvergence.Count}"));
            h.TryScreenshot("auto-compensation");
            await h.RecoverAsync();
        }, StepOptions.Shot);

        await h.StepAsync("After", "ProgramDressIsInTheWheelHistory", async ctx =>
        {
            if (!steps.Steps.Any(step => step.StepTypeKey == StepTypeKeys.WheelDress))
            {
                ctx.Skip("this machine has no wheel dresser");
            }

            IReadOnlyList<RollGrinder.Data.Model.WheelEvent> history = await h.Services
                .GetRequiredService<RollGrinder.Services.Calibration.IWheelHistory>()
                .ListAsync(20, default);
            ctx.Check(history.Any(entry => entry.Kind == RollGrinder.Data.Model.WheelEventKind.Dress
                    && entry.Source == RollGrinder.Data.Model.WheelEventSource.Program),
                "the dressing step the NC ran should be recorded in the wheel history");
        });

        await h.StepAsync("After", "EditPagesUnlocked", ctx =>
        {
            ctx.Check(!steps.IsReadOnly && !job.IsReadOnly, "program and job pages should unlock after the cycle");
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

        await h.StepAsync("After", "RecordClosedAutomatically", async ctx =>
        {
            // NC 走完最后一道置"循环正常结束"位（仿真里就是 R124=1），上位机据此自动收尾：
            // 记录变成已完成、带结束时间，勾了"打印磨后数据"就自动出磨削报告——不用人去记录页点。
            RecordsViewModel records = h.Page<RecordsViewModel>();
            await h.GoToAsync(PageKey.Records, ctx);
            records.FromDate = DateTime.Today.AddDays(-1);
            records.ToDate = DateTime.Today;
            RecordRowViewModel? row = null;
            for (int i = 0; i < 15; i++)
            {
                await h.RunAsync(records.QueryCommand);
                row = records.Records.FirstOrDefault(r => r.RollCode == SelfTestNames.FlowRollId);
                if (row?.View.FinishedAtUtc is not null)
                {
                    break;
                }

                await Task.Delay(1000);
            }

            ctx.Check(row is not null, "a grinding record for " + SelfTestNames.FlowRollId + " should exist");
            ctx.Note("state=" + row!.StateText + ", duration=" + row.DurationText + ", worst=" + row.WorstDeviationText);
            ctx.Check(row.View.FinishedAtUtc is not null, "the record should close by itself when the NC reports cycle complete");
            ctx.Check(row.View.State == JobState.Completed, "a normally finished roll should be recorded as completed, is " + row.View.State);

            bool postPrinted = await h.WaitUntilAsync(
                () => h.Interaction.Produced.Count(p => p.Contains("-auto-", StringComparison.Ordinal)) >= printsBefore + 2,
                TimeSpan.FromSeconds(15));
            ctx.Note("automatic prints: " + string.Join(", ", h.Interaction.Produced.Where(p => p.Contains("-auto-", StringComparison.Ordinal)).Select(Path.GetFileName)));
            ctx.Check(postPrinted, "with 'print post-grind data' on, the grinding report should print by itself after the roll finishes");
        }, new StepOptions(Screenshot: true, ExpectedAlarms: new[] { "Alarm_RecordCompleted" }));

        await h.StepAsync("After", "FinishButtonDoesNotReopenAClosedRecord", async ctx =>
        {
            RecordsViewModel records = h.Page<RecordsViewModel>();
            RecordRowViewModel? row = records.Records.FirstOrDefault(r => r.RollCode == SelfTestNames.FlowRollId);
            if (row?.View.FinishedAtUtc is null)
            {
                ctx.Skip("the flow record is not closed");
            }

            DateTimeOffset? finishedAt = row!.View.FinishedAtUtc;
            int prints = h.Interaction.Produced.Count;
            records.SelectedRecord = row;
            await h.PressVerticalKeyAsync(ctx, "Vk_MarkFinished");
            if (h.HasPendingConfirmation)
            {
                await h.ConfirmAsync(ctx);
            }

            ctx.Check(records.StatusResourceKey == "Records_AlreadyFinished", "pressing finish on a closed record should say so, status " + records.StatusResourceKey);
            await h.RunAsync(records.QueryCommand);
            RecordRowViewModel? again = records.Records.FirstOrDefault(r => r.RollCode == SelfTestNames.FlowRollId);
            ctx.Check(again?.View.FinishedAtUtc == finishedAt, "the finish time must not change");
            ctx.Check(h.Interaction.Produced.Count == prints, "no second report may be printed");
        });

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
            await h.GoToAsync(PageKey.Records, ctx);
            foreach ((RecordCurveKind kind, string key) in new[]
            {
                (RecordCurveKind.BeforeAfterProfile, "Curve_BeforeAfter"), (RecordCurveKind.Deviation, "Curve_Error"),
                (RecordCurveKind.Roundness, "Curve_Roundness"), (RecordCurveKind.CompensationConvergence, "Curve_Convergence"),
            })
            {
                await h.PressVerticalKeyAsync(ctx, key);
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
