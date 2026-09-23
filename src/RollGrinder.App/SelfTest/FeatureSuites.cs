using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;
using RollGrinder.Core.Calibration;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;

namespace RollGrinder.App.SelfTest;

/// <summary>自检里用到的固定名字。</summary>
internal static class SelfTestNames
{
    public const string ProfileA = "SelfTest Profile A";
    public const string ProfileB = "SelfTest Profile B";
    public const string ProgramA = "SelfTest Program A";
    public const string ProgramB = "SelfTest Program B";
    public const string RollId = "SELFTEST-R1";
    public const string FlowRollId = "SELFTEST-FLOW";
    public const string BodyLengthMm = "2000";
    public const string DiameterMm = "600";
}

/// <summary>辊形编辑：每种曲线段都插一次、上移、删除、校验、库的存/取/删、生成点列、导入对照线。</summary>
internal sealed class ProfileSuite : ISelfTestSuite
{
    public string Name => "Profile";

    public async Task RunAsync(SelfTestHarness h)
    {
        ProfileViewModel page = h.Page<ProfileViewModel>();
        await h.GoToAsync(PageKey.Profile);
        page.DiscardChanges();

        await h.StepAsync("Segments", "InsertEveryType", async ctx =>
        {
            ctx.Check(page.AvailableTypes.Count > 0, "no profile types registered");
            RollProfileTypeRegistry registry = h.Services.GetRequiredService<RollProfileTypeRegistry>();
            int before = page.Segments.Count;
            foreach (string type in page.AvailableTypes.ToList())
            {
                page.SelectedTypeForInsert = type;
                await h.RunAsync(page.InsertSegmentCommand);
                page.SelectedSegment = page.Segments.Last();
                await h.SettleAsync(50);

                // 界面上的参数格数必须和这种辊形的参数定义一一对应（圆柱就是 0 个）。
                int declared = registry.Get(type).Schema.Descriptors.Count;
                ctx.Check(page.SegmentParameters.Count == declared,
                    Invariant($"segment of type {type} shows {page.SegmentParameters.Count} parameters, schema declares {declared}"));
            }

            ctx.Check(page.Segments.Count == before + page.AvailableTypes.Count, "every type should add one segment");
            ctx.Note(Invariant($"types={page.AvailableTypes.Count}, segments={page.Segments.Count}"));
        }, StepOptions.Shot);

        await h.StepAsync("Segments", "Validate", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_Validate");
            ctx.Note("status=" + page.StatusResourceKey + ", maxChordError=" + page.MaxChordErrorText);
        }, new StepOptions(Tolerant: true));

        await h.StepAsync("Segments", "MoveUpAndRemove", async ctx =>
        {
            int count = page.Segments.Count;
            page.SelectedSegment = page.Segments.Last();
            await h.RunAsync(page.MoveSegmentUpCommand);
            page.SelectedSegment = page.Segments.Last();
            await h.RunAsync(page.RemoveSegmentCommand);
            ctx.Check(page.Segments.Count == count - 1, "remove should drop one segment");
        });

        await h.StepAsync("Library", "SaveWithoutNameRefused", async ctx =>
        {
            page.ProfileName = string.Empty;
            await h.RunAsync(page.SaveAsCommand);
            ctx.Check(page.ProfileId is null || page.IsDirty, "a nameless profile must not be stored");
        }, StepOptions.Expect("Profile_NeedsName"));

        await h.StepAsync("Library", "SaveAsAndReload", async ctx =>
        {
            int segments = page.Segments.Count;
            page.ProfileName = SelfTestNames.ProfileA;
            await h.RunAsync(page.SaveAsCommand);
            ctx.Check(!page.IsDirty && page.ProfileId is not null, "save-as should store and clear the dirty flag");
            await h.RunAsync(page.OpenLibraryCommand);
            ctx.Check(page.IsLibraryOpen, "library panel should open");
            h.TryScreenshot("profile-library");
            page.SelectedLibraryEntry = page.LibraryEntries.FirstOrDefault(e => e.Name == SelfTestNames.ProfileA);
            ctx.Check(page.SelectedLibraryEntry is not null, "saved profile should be listed");
            await h.RunAsync(page.LoadFromLibraryCommand);
            ctx.Check(page.Segments.Count == segments, Invariant($"reloaded profile should have {segments} segments, has {page.Segments.Count}"));
            ctx.Check(!page.IsDirty, "a freshly loaded profile is clean");
        }, StepOptions.Expect("Profile_Saved"));

        await h.StepAsync("Library", "DeleteSecondProfile", async ctx =>
        {
            page.ProfileName = SelfTestNames.ProfileB;
            await h.RunAsync(page.SaveAsCommand);
            await h.RunAsync(page.OpenLibraryCommand);
            page.SelectedLibraryEntry = page.LibraryEntries.First(e => e.Name == SelfTestNames.ProfileB);
            await h.RunAsync(page.DeleteFromLibraryCommand);
            ctx.Check(page.LibraryEntries.All(e => e.Name != SelfTestNames.ProfileB), "deleted profile should disappear");
            ctx.Check(page.LibraryEntries.Any(e => e.Name == SelfTestNames.ProfileA), "the other profile must stay");
            await h.RunAsync(page.CloseLibraryCommand);

            // 把 A 调回编辑器，后面工序页要从库里选它。
            await h.RunAsync(page.OpenLibraryCommand);
            page.SelectedLibraryEntry = page.LibraryEntries.First(e => e.Name == SelfTestNames.ProfileA);
            await h.RunAsync(page.LoadFromLibraryCommand);
        }, StepOptions.Expect("Profile_Saved"));

        string? generated = null;
        await h.StepAsync("Points", "GenerateCsv", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_GeneratePoints");
            generated = h.Interaction.LastProduced;
            ctx.Check(generated is not null && File.Exists(generated), "a CSV should have been written");
            int lines = File.ReadAllLines(generated!).Length;
            ctx.Check(lines > 10, Invariant($"CSV should hold the sampled points, has {lines} lines"));
            ctx.Check(page.StatusResourceKey == "Profile_PointsGenerated", "status should say generated, is " + page.StatusResourceKey);
            ctx.Note(Invariant($"{Path.GetFileName(generated)} lines={lines}"));
        });

        await h.StepAsync("Points", "ImportReferenceLine", async ctx =>
        {
            if (generated is null)
            {
                ctx.Skip("no generated CSV to import");
            }

            h.Interaction.OpenAnswers.Enqueue(generated!);
            await h.PressKeyAsync(ctx, "Fn_ImportPoints");
            ctx.Check(page.StatusResourceKey == "Profile_PointsImported", "status should say imported, is " + page.StatusResourceKey);
            ctx.Note("reference deviation=" + page.ReferenceDeviationText);
        }, StepOptions.Shot);

        await h.StepAsync("Points", "ImportGarbageRefused", async ctx =>
        {
            string junk = Path.Combine(h.OutputDirectory, "files", "junk-points.csv");
            await File.WriteAllTextAsync(junk, "this,is\nnot,a profile\n");
            h.Interaction.OpenAnswers.Enqueue(junk);
            await h.PressKeyAsync(ctx, "Fn_ImportPoints");
            ctx.Check(page.StatusResourceKey == "Profile_PointsNotUsable", "garbage should be refused, status " + page.StatusResourceKey);
        });

        await h.StepAsync("Points", "ImportCancelledDoesNothing", async ctx =>
        {
            string before = page.StatusResourceKey;
            await h.PressKeyAsync(ctx, "Fn_ImportPoints");
            ctx.Check(page.StatusResourceKey == before, "cancelling the file dialog should change nothing");
        });
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>工序编程：新作业、每种工序、程序步骤开关、校验、轧辊数据、程序库、从辊形库选辊形。离线时验证下发被拒。</summary>
internal sealed class StepsSuite : ISelfTestSuite
{
    public string Name => "Steps";

    public async Task RunAsync(SelfTestHarness h)
    {
        StepsViewModel page = h.Page<StepsViewModel>();
        await h.GoToAsync(PageKey.Steps);

        await h.StepAsync("Job", "NewJob", async ctx =>
        {
            await h.RunAsync(page.NewJobCommand);
            ctx.Check(!string.IsNullOrWhiteSpace(page.JobId), "a new job id should be generated");
            ctx.Check(page.Steps.Count == 0, "a new job starts with no steps");
        });

        await h.StepAsync("StepTypes", "GroupedBySlotInOrder", ctx =>
        {
            int[] orders = page.StepTypeOptions.Select(o => o.SlotOrder).ToArray();
            ctx.Check(orders.SequenceEqual(orders.OrderBy(o => o)), "step types should be sorted by slot");
            ctx.Note(Invariant($"{page.StepTypeOptions.Count} types, {page.StepTypeOptions.Count(o => o.IsAvailable)} available"));
            return Task.CompletedTask;
        });

        await h.StepAsync("StepTypes", "AddEveryAvailableType", async ctx =>
        {
            foreach (StepTypeOptionViewModel option in page.StepTypeOptions.Where(o => o.IsAvailable).ToList())
            {
                page.SelectedStepType = option;
                await h.RunAsync(page.AddStepCommand);
            }

            int expected = page.StepTypeOptions.Count(o => o.IsAvailable);
            ctx.Check(page.Steps.Count == expected, Invariant($"expected {expected} steps, got {page.Steps.Count}"));
            ctx.Note("total duration " + page.TotalDurationText);
        }, StepOptions.Shot);

        StepTypeOptionViewModel? unavailable = page.StepTypeOptions.FirstOrDefault(o => !o.IsAvailable);
        if (unavailable is not null)
        {
            await h.StepAsync("StepTypes", "UnavailableTypeRefused", async ctx =>
            {
                int count = page.Steps.Count;
                page.SelectedStepType = unavailable;
                await h.RunAsync(page.AddStepCommand);
                ctx.Check(page.Steps.Count == count, "a step the machine cannot do must not be added");
            }, StepOptions.Expect("Alarm_StepTypeNotAvailable"));
        }

        await h.StepAsync("StepTypes", "RemoveStep", async ctx =>
        {
            int count = page.Steps.Count;
            page.RemoveStepCommand.Execute(page.Steps.Last());
            await h.SettleAsync();
            ctx.Check(page.Steps.Count == count - 1, "remove should drop one step");
        });

        await h.StepAsync("Validation", "MissingRollIdReported", async ctx =>
        {
            page.RollId = string.Empty;
            await h.RunAsync(page.ValidateCommand);
            ctx.Check(page.StatusResourceKey == "Job_IdentifiersMissing", "status should flag missing ids, is " + page.StatusResourceKey);
        });

        await h.StepAsync("Validation", "BadGeometryReported", async ctx =>
        {
            page.RollId = SelfTestNames.RollId;
            page.BodyLengthMmText = "abc";
            await h.RunAsync(page.ValidateCommand);
            ctx.Check(page.StatusResourceKey == "Job_GeometryInvalid", "status should flag bad geometry, is " + page.StatusResourceKey);
        });

        await h.StepAsync("Validation", "FullJobValidates", async ctx =>
        {
            page.BodyLengthMmText = SelfTestNames.BodyLengthMm;
            page.NominalDiameterMmText = SelfTestNames.DiameterMm;
            await h.RunAsync(page.ValidateCommand);
            ctx.Note("status=" + page.StatusResourceKey + ", violations=" + page.Violations.Count);
            ctx.Check(page.StatusResourceKey == "Job_ReadyToHandOver",
                "a job built from default parameters of every step type should validate; status " + page.StatusResourceKey
                + (page.Violations.Count > 0 ? ", first violation: " + page.Violations[0].ParameterText + " " + page.Violations[0].ReasonText : string.Empty));
        }, StepOptions.Shot);

        await h.StepAsync("ProgramOptions", "ToggleEachAvailable", async ctx =>
        {
            int toggled = 0;
            foreach (ProgramOptionRowViewModel option in page.ProgramOptions.Where(o => o.IsAvailable).ToList())
            {
                bool original = option.IsOn;
                option.IsOn = !original;
                await h.SettleAsync(20);
                option.IsOn = original;
                toggled++;
            }

            ctx.Note(Invariant($"{toggled} of {page.ProgramOptions.Count} options toggled"));
            ctx.Check(toggled > 0, "at least one program option should be available");
        });

        await h.StepAsync("RollData", "EditSaveReopen", async ctx =>
        {
            page.RollId = SelfTestNames.RollId;
            await h.PressKeyAsync(ctx, "Fn_RollData");
            ctx.Check(page.ActiveSubViewKey == StepsViewModel.RollDataSubView, "roll data sub view should open");
            ctx.Check(page.RollData.Count > 0, "roll data rows should be listed");
            foreach (RollDataRowViewModel row in page.RollData)
            {
                row.Text = "100";
            }

            await h.RunAsync(page.SaveRollDataCommand);
            ctx.Check(page.StatusResourceKey == "Steps_RollDataSaved", "status should say saved, is " + page.StatusResourceKey);
            ctx.Note("total weight " + page.TotalWeightText);
            h.TryScreenshot("steps-rolldata");
            await h.PressNavigationKeyAsync();
            await h.PressKeyAsync(ctx, "Fn_RollData");
            ctx.Check(page.RollData.All(r => r.Text.StartsWith("100", StringComparison.Ordinal)), "saved roll data should come back");
            await h.PressNavigationKeyAsync();
        });

        await h.StepAsync("ProgramLibrary", "SaveWithoutNameRefused", async ctx =>
        {
            page.ProgramName = string.Empty;
            await h.RunAsync(page.SaveProgramAsCommand);
            ctx.Check(page.IsDirty, "a nameless program must not be stored");
        }, StepOptions.Expect("Program_NeedsName"));

        await h.StepAsync("ProgramLibrary", "SaveAsNewJobLoad", async ctx =>
        {
            int steps = page.Steps.Count;
            page.ProgramName = SelfTestNames.ProgramA;
            await h.RunAsync(page.SaveProgramAsCommand);
            await h.RunAsync(page.NewJobCommand);
            ctx.Check(page.Steps.Count == 0, "new job should clear steps");
            ctx.Check(page.ProgramId is null && string.IsNullOrEmpty(page.ProgramName), "new job must not keep the old program's identity");
            await h.PressKeyAsync(ctx, "Fn_ProgramLibrary");
            ctx.Check(page.IsProgramLibraryOpen, "program library should open");
            h.TryScreenshot("steps-program-library");
            page.SelectedProgramEntry = page.ProgramLibraryEntries.FirstOrDefault(p => p.Name == SelfTestNames.ProgramA);
            ctx.Check(page.SelectedProgramEntry is not null, "saved program should be listed");
            await h.RunAsync(page.LoadProgramCommand);
            ctx.Check(page.Steps.Count == steps, Invariant($"loaded program should have {steps} steps, has {page.Steps.Count}"));
        }, StepOptions.Expect("Program_Saved"));

        await h.StepAsync("ProgramLibrary", "DeleteSecondProgram", async ctx =>
        {
            page.ProgramName = SelfTestNames.ProgramB;
            await h.RunAsync(page.SaveProgramAsCommand);
            await h.RunAsync(page.OpenProgramLibraryCommand);
            page.SelectedProgramEntry = page.ProgramLibraryEntries.First(p => p.Name == SelfTestNames.ProgramB);
            await h.RunAsync(page.DeleteProgramCommand);
            ctx.Check(page.ProgramLibraryEntries.All(p => p.Name != SelfTestNames.ProgramB), "deleted program should disappear");
            await h.RunAsync(page.CloseProgramLibraryCommand);
        }, StepOptions.Expect("Program_Saved"));

        await h.StepAsync("ProfileFromLibrary", "UseAndClear", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_SelectProfile");
            ctx.Check(page.IsProfileLibraryOpen, "profile library panel should open");
            page.SelectedProfileEntry = page.ProfileLibraryEntries.FirstOrDefault(p => p.Name == SelfTestNames.ProfileA);
            if (page.SelectedProfileEntry is null)
            {
                ctx.Skip("profile saved by the Profile suite is not in the library");
            }

            await h.RunAsync(page.UseProfileFromLibraryCommand);
            ctx.Check(page.UsesLibraryProfile && !page.CanEditInlineProfile, "library profile should replace the inline one");
            h.TryScreenshot("steps-library-profile");
            await h.RunAsync(page.ClearProfileSelectionCommand);
            ctx.Check(!page.UsesLibraryProfile && page.CanEditInlineProfile, "clearing should bring back the inline profile");
        });

        if (h.Services.GetRequiredService<RollGrinder.Contracts.IAppOptions>().IsOffline)
        {
            await h.StepAsync("Download", "RefusedWithoutMachine", async ctx =>
            {
                page.RollId = SelfTestNames.RollId;
                await h.PressKeyAsync(ctx, "Fn_DownloadNc");
                ctx.Check(page.StatusResourceKey != "Job_HandedOver", "download must not claim success without a machine");
                ctx.Note("status=" + page.StatusResourceKey);
            }, StepOptions.Expect("*"));
        }

        page.DiscardChanges();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>设置：标定值的改/存/重载；换砂轮向导走完五步，再走一次中途取消。</summary>
internal sealed class SettingsSuite : ISelfTestSuite
{
    public string Name => "Settings";

    public async Task RunAsync(SelfTestHarness h)
    {
        SettingsViewModel page = h.Page<SettingsViewModel>();
        bool offline = h.Services.GetRequiredService<RollGrinder.Contracts.IAppOptions>().IsOffline;
        await h.GoToAsync(PageKey.Settings);

        await h.StepAsync("Calibration", "ValuesListed", ctx =>
        {
            ctx.Check(page.Values.Count > 0, "calibration values should be listed");
            ctx.Check(page.CanEdit, "a manufacturer account should be able to edit calibration");
            ctx.Note(Invariant($"{page.Values.Count} values"));
            return Task.CompletedTask;
        }, StepOptions.Shot);

        await h.StepAsync("Calibration", "EditSaveReload", async ctx =>
        {
            ParameterRowViewModel? row = page.Values.FirstOrDefault(r => r.Kind == ParameterValueKind.Number
                && double.TryParse(r.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out _));
            if (row is null)
            {
                ctx.Skip("no numeric calibration value to edit");
            }

            double value = double.Parse(row!.Text, NumberStyles.Float, CultureInfo.CurrentCulture);
            string edited = (value + 0.001).ToString("0.###", CultureInfo.CurrentCulture);
            row.Text = edited;
            await h.SettleAsync();
            ctx.Check(page.IsDirty, "editing a value should mark the page dirty");
            await h.PressKeyAsync(ctx, "Fn_SaveSettings");
            ctx.Check(!page.IsDirty, "save should clear the dirty flag");
            await h.PressKeyAsync(ctx, "Fn_ReloadSettings");
            ParameterRowViewModel reloaded = page.Values.First(r => r.Key == row.Key);
            ctx.Check(
                double.Parse(reloaded.Text, NumberStyles.Float, CultureInfo.CurrentCulture) == double.Parse(edited, NumberStyles.Float, CultureInfo.CurrentCulture),
                "reloaded value should equal the saved one: " + reloaded.Text + " vs " + edited);
            ctx.Note(row.Key + ": " + value.ToString(CultureInfo.InvariantCulture) + " -> " + edited);
        }, StepOptions.Expect("*"));

        string[] machineWrites = offline ? new[] { "*" } : Array.Empty<string>();
        await h.StepAsync("WheelChange", "FullWizard", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_NewWheel");
            ctx.Check(page.ActiveSubViewKey == SettingsViewModel.WheelChangeSubView, "wheel change wizard should open");
            ctx.Check(page.WheelChangeStage == WheelChangeStage.EnterNewWheel, "wizard should start at 'enter new wheel'");
            h.TryScreenshot("wheel-1-enter");

            page.NewWheelDiameterText = "900";
            await h.RunAsync(page.WheelChangeNextCommand);
            ctx.Check(page.WheelChangeStage == WheelChangeStage.SwitchToManualTouch, "stage 2 expected, got " + page.WheelChangeStage);

            await h.RunAsync(page.WheelChangeNextCommand);
            if (offline && page.WheelChangeStage == WheelChangeStage.SwitchToManualTouch)
            {
                ctx.Note("offline: switching touch mode needs the machine, wizard stays at stage 2 as designed");
                await h.RunAsync(page.CancelWheelChangeCommand);
                return;
            }

            ctx.Check(page.WheelChangeStage == WheelChangeStage.TrialGrind, "stage 3 expected, got " + page.WheelChangeStage);
            page.TrialExpectedDiameterText = "600.000";
            page.TrialMeasuredDiameterText = "600.010";
            await h.RunAsync(page.WheelChangeNextCommand);
            ctx.Check(page.WheelChangeStage == WheelChangeStage.Verify, "stage 4 expected, got " + page.WheelChangeStage);
            ctx.Check(page.WheelChangeResultText.Length > 0, "the computed wheel error should be shown");
            ctx.Note("result: " + page.WheelChangeResultText);
            h.TryScreenshot("wheel-4-verify");

            await h.RunAsync(page.WheelChangeNextCommand);
            await h.RunAsync(page.WheelChangeNextCommand);
            ctx.Check(page.ActiveSubViewKey is null, "wizard should close when done");
            ctx.Check(page.StatusResourceKey == "WheelChange_Done", "status should say done, is " + page.StatusResourceKey);
        }, new StepOptions(ExpectedAlarms: machineWrites));

        await h.StepAsync("WheelChange", "CancelHalfway", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_NewWheel");
            page.NewWheelDiameterText = "880";
            await h.RunAsync(page.WheelChangeNextCommand);
            await h.RunAsync(page.CancelWheelChangeCommand);
            ctx.Check(page.ActiveSubViewKey is null, "cancel should close the wizard");
        }, new StepOptions(ExpectedAlarms: machineWrites));
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>手动与辅助：每个动作按钮（要确认的按两下）、测量取点与保存、对中、HMI 复位、跳诊断。</summary>
internal sealed class ManualSuite : ISelfTestSuite
{
    public string Name => "Manual";

    public async Task RunAsync(SelfTestHarness h)
    {
        ManualViewModel page = h.Page<ManualViewModel>();
        await h.GoToAsync(PageKey.Manual);

        await h.StepAsync("Page", "LiveValues", ctx =>
        {
            ctx.Check(page.AxisValues.Count > 0, "axis values should be listed");
            ctx.Note(Invariant($"axes={page.AxisValues.Count}, spindles={page.SpindleValues.Count}"));
            return Task.CompletedTask;
        }, StepOptions.Shot);

        var groups = new (string Name, System.Collections.ObjectModel.ObservableCollection<MachineActionViewModel> Actions)[]
        {
            ("MeasuringArm", page.MeasuringArmActions),
            ("Tailstock", page.TailstockActions),
            ("Other", page.OtherActions),
            ("Cycles", page.CycleActions),
        };

        foreach ((string groupName, var actions) in groups)
        {
            foreach (MachineActionViewModel action in actions.ToList())
            {
                await h.StepAsync("Actions_" + groupName, action.Descriptor.Key, async ctx =>
                {
                    if (!action.IsEnabled)
                    {
                        ctx.Skip(action.IsMapped ? "not allowed in the current machine state" : "not mapped in tagmap");
                    }

                    await h.RunAsync(action.Command);
                    if (action.IsAwaitingConfirmation)
                    {
                        ctx.Note("asked for confirmation, pressed again");
                        await h.RunAsync(action.Command);
                    }

                    ctx.Check(!action.IsAwaitingConfirmation, "action should not stay armed after the second press");
                    ctx.Note("feedback: " + page.LastActionText);
                });
            }
        }

        await h.StepAsync("Measurement", "CaptureSaveClear", async ctx =>
        {
            // 前面按过的"测量采样"动作也会取一个点：先清空，再数自己取的。
            await h.RunAsync(page.ClearPointsCommand);
            for (int i = 0; i < 3; i++)
            {
                await h.RunAsync(page.CapturePointCommand);
            }

            ctx.Check(page.Points.Count == 3, Invariant($"3 captured points expected, got {page.Points.Count}"));
            h.TryScreenshot("manual-points");
            await h.RunAsync(page.SaveMeasurementCommand);
            ctx.Note("save status=" + page.StatusResourceKey);
            await h.RunAsync(page.ClearPointsCommand);
            ctx.Check(page.Points.Count == 0, "clear should remove points");
        }, StepOptions.Expect("*"));

        await h.StepAsync("Centring", "HeadTailClear", async ctx =>
        {
            await h.RunAsync(page.CaptureHeadCommand);
            await h.RunAsync(page.CaptureTailCommand);
            ctx.Note("mounting deviation=" + page.MountingDeviationText + ", hint=" + page.AlignmentHintText);
            h.TryScreenshot("manual-centring");
            await h.RunAsync(page.ClearCentringCommand);
        });

        await h.StepAsync("Keys", "HmiResetClearsAlarms", async ctx =>
        {
            h.Services.GetRequiredService<IAlarmSink>().Raise(AlarmSeverity.Information, "Banner_NoAlarm", "self-test marker");
            await h.PressKeyAsync(ctx, "Fn_HmiReset");
            if (h.Shell.FunctionKeys.Any(k => k.LabelResourceKey == "Fn_ConfirmAgain"))
            {
                await h.PressKeyAsync(ctx, "Fn_ConfirmAgain");
            }

            ctx.Check(h.Services.GetRequiredService<IAlarmLog>().Snapshot().Count == 0, "HMI reset should clear the alarm list");
        });

        await h.StepAsync("Keys", "JumpToDiagnosticsAndBack", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_Diagnostics");
            ctx.Check(h.Shell.CurrentPage.Key == PageKey.Diagnostics, "should land on diagnostics");
            ctx.Check(h.NavigationKeyLabel == "Nav_BackToPageFormat", "F8 should offer the way back to manual");
            await h.PressNavigationKeyAsync();
            ctx.Check(h.Shell.CurrentPage.Key == PageKey.Manual, "F8 should return to manual");
        });
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>磨削记录：查询、打开、12 项指标、4 条曲线、日/月汇总、辊件台账、磨前/磨后报表预览与打印、导出。</summary>
internal sealed class RecordsSuite : ISelfTestSuite
{
    public string Name => "Records";

    public async Task RunAsync(SelfTestHarness h)
    {
        RecordsViewModel page = h.Page<RecordsViewModel>();
        IStringLocalizer localizer = h.Services.GetRequiredService<IStringLocalizer>();
        await h.GoToAsync(PageKey.Records);

        await h.StepAsync("Query", "LastThirtyDays", async ctx =>
        {
            page.FromDate = DateTime.Today.AddDays(-30);
            page.ToDate = DateTime.Today;
            await h.RunAsync(page.QueryCommand);
            ctx.Note(Invariant($"{page.Records.Count} records"));
        }, StepOptions.Shot);

        bool hasRecords = page.Records.Count > 0;
        await h.StepAsync("Record", "OpenMetricsAndCurves", async ctx =>
        {
            if (!hasRecords)
            {
                ctx.Skip("no records (offline run, or the full flow did not produce one)");
            }

            page.SelectedRecord = page.Records.FirstOrDefault(r => r.RollCode == SelfTestNames.FlowRollId) ?? page.Records[0];
            await h.SettleAsync(300);
            ctx.Check(page.Metrics.Count >= 12, Invariant($"12 metrics expected, got {page.Metrics.Count}"));
            foreach (RecordCurveKind kind in Enum.GetValues<RecordCurveKind>())
            {
                page.SelectCurveCommand.Execute(kind);
                await h.SettleAsync(200);
                ctx.Note(kind + (page.CurveHasData ? "=data" : "=empty"));
                h.TryScreenshot("records-curve-" + kind);
            }
        });

        await h.StepAsync("Summary", "DailyAndMonthly", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_DailyReport");
            ctx.Note("daily: " + page.SummaryText);
            await h.PressKeyAsync(ctx, "Fn_MonthlyReport");
            ctx.Note("monthly: " + page.SummaryText);
            ctx.Check(page.SummaryText.Length > 0, "summary should produce text");
        });

        await h.StepAsync("Ledger", "OpenAndClose", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_RollLedger");
            ctx.Check(page.ActiveSubViewKey is not null, "ledger sub view should open");
            ctx.Note(Invariant($"{page.Ledger.Count} rolls, {page.SummaryLineText}"));
            h.TryScreenshot("records-ledger");
            await h.PressNavigationKeyAsync();
        });

        foreach ((string key, string name) in new[] { ("Fn_PreGrindReport", "PreGrind"), ("Fn_Print", "PostGrind") })
        {
            await h.StepAsync("Report", name + "PreviewAndPrint", async ctx =>
            {
                if (!hasRecords)
                {
                    ctx.Skip("no record to report on");
                }

                await h.PressKeyAsync(ctx, key);
                ctx.Check(page.Report is not null, "a report should be composed");
                ctx.Check(page.ActiveSubViewKey is not null, "report preview should open as a sub view");
                h.TryScreenshot("records-report-" + name);
                int before = h.Interaction.Produced.Count;
                bool clicked = await h.ClickButtonAsync(localizer["Report_PrintButton"]);
                ctx.Check(clicked, "print button should be on the preview");
                ctx.Check(h.Interaction.Produced.Count == before + 1, "printing should produce one document");
                ctx.Note("printed " + Path.GetFileName(h.Interaction.LastProduced));
                await h.PressNavigationKeyAsync();
            });
        }

        await h.StepAsync("Export", "Csv", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_ExportExcel");
            string? file = h.Interaction.LastProduced;
            ctx.Check(file is not null && File.Exists(file) && file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), "a CSV export should be written");
            ctx.Note(Invariant($"{Path.GetFileName(file)} lines={File.ReadAllLines(file!).Length}"));
        });
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>诊断：连接与映射检查、四个子视图、运行日志、快照导出、整机备份（检查 zip 内容）。</summary>
internal sealed class DiagnosticsSuite : ISelfTestSuite
{
    public string Name => "Diagnostics";

    public async Task RunAsync(SelfTestHarness h)
    {
        DiagnosticsViewModel page = h.Page<DiagnosticsViewModel>();
        await h.GoToAsync(PageKey.Diagnostics);

        await h.StepAsync("Page", "ConnectionAndTagMap", ctx =>
        {
            ctx.Check(page.ConnectionRows.Count > 0, "connection rows should be listed");
            ctx.Note("tagmap: " + page.TagMapCheckText + Invariant($", missing={page.MissingTags.Count}, degradation={page.DegradationLevel}"));
            ctx.Check(page.TagMapIsValid, "the sample tagmap should be complete: missing " + string.Join(", ", page.MissingTags.Take(5)));
            return Task.CompletedTask;
        }, StepOptions.Shot);

        foreach (string key in new[] { "Fn_TagMonitor", "Fn_MachineConfig", "Fn_TagMapping", "Fn_AuditLog", "Fn_RunLog" })
        {
            await h.StepAsync("SubViews", key, async ctx =>
            {
                await h.PressKeyAsync(ctx, key);
                ctx.Note("sub view=" + (page.ActiveSubViewKey ?? "none") + Invariant($", monitorRows={page.TagMonitorRows.Count}, auditRows={page.AuditRows.Count}, inspectorChars={page.InspectorText.Length}"));
                h.TryScreenshot("diag-" + key);
                await h.RecoverAsync();
            });
        }

        await h.StepAsync("Export", "Snapshot", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_ExportSnapshot");
            string? file = h.Interaction.LastProduced;
            ctx.Check(file is not null && File.Exists(file) && new FileInfo(file).Length > 0, "a snapshot file should be written");
            ctx.Note(Invariant($"{Path.GetFileName(file)} {new FileInfo(file!).Length} bytes"));
        });

        await h.StepAsync("Export", "Backup", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_BackupRestore", TimeSpan.FromSeconds(60));
            string? file = h.Interaction.LastProduced;
            ctx.Check(file is not null && File.Exists(file), "a backup zip should be written");
            using ZipArchive zip = ZipFile.OpenRead(file!);
            ctx.Check(zip.Entries.Count > 0, "backup should not be empty");
            ctx.Note(Invariant($"{Path.GetFileName(file)} entries={zip.Entries.Count}: ") + string.Join(", ", zip.Entries.Take(8).Select(e => e.FullName)));
        }, new StepOptions(TimeoutSeconds: 90));
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
