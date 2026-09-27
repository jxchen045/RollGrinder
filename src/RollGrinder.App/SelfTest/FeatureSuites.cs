using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.App.ViewModels;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Core.Calibration;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;

namespace RollGrinder.App.SelfTest;

/// <summary>自检里用到的固定名字。</summary>
internal static class SelfTestNames
{
    public const string ProfileA = "SelfTest Profile A";
    public const string ProfileB = "SelfTest Profile B";
    public const string OldProfile = "SelfTest Old Profile";
    public const string LedgerRollId = "SELFTEST-BR1";
    public const string ProgramA = "SelfTest Program A";
    public const string ProgramB = "SelfTest Program B";
    public const string RollId = "SELFTEST-R1";
    public const string FlowRollId = "SELFTEST-FLOW";
    public const string JobRollId = "SELFTEST-JOB";
    public const string FlowProgram = "SelfTest Flow Program";
    public const string BodyLengthMm = "2000";
    public const string DiameterMm = "600";

    /// <summary>按"另存为"、在命名框里填名字、按确定。返回命名框是否已关（关了 = 存成了）。</summary>
    public static async Task<bool> SaveAsAsync(
        SelfTestHarness h, System.Windows.Input.ICommand saveAs, NamePromptViewModel prompt, string name)
    {
        await h.RunAsync(saveAs);
        if (!prompt.IsOpen)
        {
            return false;
        }

        prompt.Name = name;
        await h.RunAsync(prompt.ConfirmCommand);
        if (prompt.IsOpen && prompt.CanOverwrite)
        {
            // 同一个数据目录上次自检留下的同名条目：按"覆盖"。
            await h.RunAsync(prompt.OverwriteCommand);
        }

        return !prompt.IsOpen;
    }
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

        await h.StepAsync("Segments", "BuildFromEmptyEveryType", async ctx =>
        {
            ctx.Check(page.AvailableTypes.Count > 1, "profile types should be registered");
            RollProfileTypeRegistry registry = h.Services.GetRequiredService<RollProfileTypeRegistry>();
            page.BodyLengthMmText = "2000";
            await RemoveAllAsync(h, page);
            ctx.Check(page.Segments.Count == 0 && page.HasErrors, "an empty profile should be reported");

            // 全靠竖向软键：插入段 ▸ → 类型键；选完子菜单自己收回根层。
            List<string> types = page.AvailableTypes.ToList();
            foreach (string type in types)
            {
                page.SelectedSegment = page.Segments.LastOrDefault();
                await h.PressVerticalKeyAsync(ctx, "Vk_InsertSegment");
                ctx.Check(h.IndexOfVerticalKey("Vk_Back") == PageViewModelBase.VerticalKeyCount - 1,
                    "a sub menu keeps 'back' in the eighth slot");
                await h.PressVerticalKeyAsync(ctx, "ProfileType_" + type);
                ctx.Check(h.IndexOfVerticalKey("Vk_InsertSegment") == 0, "choosing a type should return to the root keys");
                ctx.Check(page.SelectedSegment?.Order == page.Segments.Count, "the new segment should be selected, at the end");

                // 参数格数等于这种辊形的参数定义（点表那一列单独用表格编辑，不算在格子里）。
                int declared = registry.Get(type).Schema.Descriptors.Count(d => d.Kind != ParameterValueKind.Points);
                ctx.Check(page.SegmentParameters.Count == declared,
                    Invariant($"segment of type {type} shows {page.SegmentParameters.Count} parameters, schema declares {declared}"));
                ctx.Check(h.IsVerticalKeyUsable(h.IndexOfVerticalKey("Vk_Interpolation")) == (type == ProfileTypeKeys.PointTable),
                    "'interpolation ▸' is only for point-table segments");
                if (type == ProfileTypeKeys.PointTable)
                {
                    ctx.Check(page.IsPointTableSelected && page.PointRows.Count == 2, "a new point table starts with two points");
                }
            }

            ctx.Check(page.Segments.Count == types.Count, "every type should add one segment");

            // 第一段铺满了设计长度，后面每段先给 100 mm：合计超了，要报错。
            ctx.Check(page.HasErrors, "segments running past the design length should be reported");

            // 把第一段缩短，让合计正好 2000：错误消失，后面各段的起点跟着前移。
            page.SelectedSegment = page.Segments[0];
            double first = 2000.0 - (100.0 * (types.Count - 1));
            page.SegmentLengthText = first.ToString(CultureInfo.CurrentCulture);
            await h.SettleAsync(50);
            ctx.Check(!page.HasErrors, "segments adding up to the design length should be clean: " + Issues(page));
            ctx.Check(page.Segments[1].StartText == page.Segments[0].EndText, "the second segment should start where the first ends");
            ctx.Check(page.Composite?.Layout == ProfileLayout.Sequential, "new profiles are sequential");
            ctx.Note(Invariant($"types={types.Count}, first length={first}"));
        }, StepOptions.Shot);

        await h.StepAsync("Segments", "Validate", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_Validate");
            ctx.Note("status=" + page.StatusResourceKey + ", maxChordError=" + page.MaxChordErrorText);
        }, new StepOptions(Tolerant: true));

        await h.StepAsync("Segments", "MoveCopyAndRemove", async ctx =>
        {
            int count = page.Segments.Count;
            page.SelectedSegment = page.Segments.Last();
            string movedType = page.SelectedSegment.DisplayName;
            await h.RunAsync(page.MoveSegmentUpCommand);
            ctx.Check(page.SelectedSegment?.Order == count - 1 && page.SelectedSegment.DisplayName == movedType,
                "after moving up the selection should follow the moved segment");
            await h.RunAsync(page.MoveSegmentDownCommand);
            ctx.Check(page.SelectedSegment?.Order == count && page.SelectedSegment.DisplayName == movedType,
                "move down should bring it back to the end");

            await h.RunAsync(page.CopySegmentCommand);
            ctx.Check(page.Segments.Count == count + 1 && page.HasErrors, "a copy makes the profile longer than the design length");
            await h.RunAsync(page.RemoveSegmentCommand);
            ctx.Check(page.Segments.Count == count && !page.HasErrors, "removing the copy should make it fit again: " + Issues(page));
        });

        await h.StepAsync("Segments", "LiveValidationBlocksSave", async ctx =>
        {
            int saveKey = h.IndexOfKey("Fn_Save");
            page.SelectedSegment = page.Segments.Last();
            string originalLength = page.SegmentLengthText;
            ctx.Check(!page.HasErrors, "the profile should start without errors: " + Issues(page));

            // 最后一段伸出设计长度：立刻报错、段标红、保存与另存为变灰。
            page.SegmentLengthText = (double.Parse(originalLength, CultureInfo.CurrentCulture) + 500.0).ToString(CultureInfo.CurrentCulture);
            await h.SettleAsync(50);
            ctx.Check(page.HasErrors && page.Issues.Any(i => i.IsError), "a segment past the design length must show an error at once");
            ctx.Check(page.SelectedSegment?.HasError == true, "the offending segment should be marked");
            ctx.Check(!h.Shell.FunctionKeys[saveKey].Command.CanExecute(null), "save must be disabled while there are errors");
            ctx.Check(!page.SaveAsCommand.CanExecute(null), "save-as must be disabled while there are errors");
            h.TryScreenshot("profile-live-errors");

            // 正在输入、还不成立的内容也要说出来。
            page.SegmentLengthText = "-";
            await h.SettleAsync(50);
            ctx.Check(page.HasErrors, "an unfinished length should be reported while typing");

            page.SegmentLengthText = originalLength;
            await h.SettleAsync(50);
            ctx.Check(!page.HasErrors, "restoring the length should clear the error: " + Issues(page));
            ctx.Check(h.Shell.FunctionKeys[saveKey].Command.CanExecute(null), "save should be enabled again");

            string designLength = page.BodyLengthMmText;
            page.BodyLengthMmText = "1";
            await h.SettleAsync(50);
            ctx.Check(page.HasErrors, "a design length below the machine minimum is an error");
            page.BodyLengthMmText = designLength;
            await h.SettleAsync(50);
            ctx.Check(!page.HasErrors, "restoring the design length should clear the error");
        });

        await h.StepAsync("PointTable", "EditPoints", async ctx =>
        {
            int order = page.Composite!.Segments.First(s => s.ProfileTypeKey == ProfileTypeKeys.PointTable).Order;
            page.SelectedSegment = page.Segments[order - 1];
            await h.SettleAsync(50);
            ctx.Check(page.IsPointTableSelected, "the point table segment should show its table");

            page.SelectedPoint = page.PointRows[0];
            await h.RunAsync(page.AddPointCommand);
            ctx.Check(page.PointRows.Count == 3, "add point should insert one between the first two");
            page.PointRows[1].ValueText = "20";
            await h.SettleAsync(50);
            ctx.Check(!page.HasErrors, "a valid table should be clean: " + Issues(page));
            ctx.Check(page.TablePoints.Count == 3, "the preview should show the raw points");
            IReadOnlyList<TablePoint> stored = page.Composite.Segments[order - 1].Parameters.Get(PointTableProfileType.PointsKey).Points;
            ctx.Check(stored.Count == 3 && Math.Abs(stored[1].Y - 20.0) < 1e-9, "the edited point should be written back");
            h.TryScreenshot("profile-point-table");

            // 插值方式 ▸：四种一键一个，选中就写回这一段。
            await h.PressVerticalKeyAsync(ctx, "Vk_Interpolation");
            await h.PressVerticalKeyAsync(ctx, "Choice_interpolation_SmoothingSpline");
            string method = page.Composite.Segments[order - 1].Parameters.GetChoice(PointTableProfileType.InterpolationKey);
            ctx.Check(method == "SmoothingSpline", "the interpolation chosen on the vertical keys should be written back, is " + method);

            string z = page.PointRows[1].ZText;
            page.PointRows[1].ZText = "abc";
            await h.SettleAsync(50);
            ctx.Check(page.HasErrors, "a point that is not a number must be reported");
            page.PointRows[1].ZText = z;
            await h.SettleAsync(50);
            ctx.Check(!page.HasErrors, "restoring the point should clear the error: " + Issues(page));
        });

        await h.StepAsync("Symmetric", "HeadstockSideMirrorsToTailstock", async ctx =>
        {
            await RemoveAllAsync(h, page);
            page.IsSymmetric = true;
            await h.SettleAsync(50);
            ctx.Check(page.IsSymmetric, "symmetric editing should switch on for an empty profile");

            // 头架端：锥度 150 mm（端部减薄 50 µm，自动朝头架端面），再一段跨中点的凸度 1700 mm。
            page.SelectedTypeForInsert = ProfileTypeKeys.Taper;
            await h.RunAsync(page.InsertSegmentCommand);
            ctx.Check(!page.CanMirrorSegment, "a taper faces the end by itself; its mirror box should be disabled");
            page.SegmentLengthText = 150.0.ToString(CultureInfo.CurrentCulture);
            page.SegmentParameters.First().Text = "50";
            page.SelectedTypeForInsert = ProfileTypeKeys.Crown;
            await h.RunAsync(page.InsertSegmentCommand);
            page.SegmentLengthText = 1700.0.ToString(CultureInfo.CurrentCulture);
            page.SegmentParameters.First().Text = "300";
            await h.SettleAsync(50);

            ctx.Check(page.Segments.Count == 2, "only the headstock side is listed");
            ctx.Check(!page.HasErrors, "the design example should be clean: " + Issues(page));
            ctx.Check(page.Composite?.Segments.Count == 3 && page.Composite.Segments[2].IsMirrored == false
                && page.Composite.Segments[2].FromMm == 1850.0,
                "the tailstock taper should be generated as an independent segment (tapers carry no mirror flag)");
            h.TryScreenshot("profile-symmetric");

            // 对称编辑时 CVC 键是灰的；Esc 退出子菜单。
            await h.PressVerticalKeyAsync(ctx, "Vk_InsertSegment");
            ctx.Check(!h.IsVerticalKeyUsable(h.IndexOfVerticalKey("ProfileType_" + ProfileTypeKeys.Cvc)),
                "CVC must be greyed out while editing symmetrically");
            h.Shell.PressEscape();
            await h.SettleAsync();
            ctx.Check(h.IndexOfVerticalKey("Vk_InsertSegment") == 0, "Esc should close the sub menu first");
            ctx.Check(h.Shell.CurrentPage.Key == PageKey.Profile, "Esc on a sub menu must not leave the page");
            page.SelectedTypeForInsert = ProfileTypeKeys.Cvc;
            await h.RunAsync(page.InsertSegmentCommand);
            ctx.Check(page.Segments.Count == 2 && page.StatusResourceKey == "Profile_SymmetryUnsupportedType",
                "CVC must not be inserted while editing symmetrically");

            page.IsSymmetric = false;
            await h.SettleAsync(50);
            ctx.Check(page.Segments.Count == 3, "switching symmetry off lists all expanded segments");
        });

        await h.StepAsync("Symmetric", "AsymmetricProfileCannotFold", async ctx =>
        {
            page.SelectedSegment = page.Segments[2];
            page.SegmentParameters.First().Text = "-40";
            await h.SettleAsync(50);
            page.IsSymmetric = true;
            await h.SettleAsync(50);
            ctx.Check(!page.IsSymmetric && page.StatusResourceKey == "Profile_SymmetryNotFoldable",
                "a lopsided profile must not silently turn symmetric");
            ctx.Check(!page.HasErrors, "the lopsided profile is still valid: " + Issues(page));
        });

        await h.StepAsync("Library", "SaveWithoutNameRefused", async ctx =>
        {
            page.ProfileName = string.Empty;
            await h.PressKeyAsync(ctx, "Fn_Save");
            ctx.Check(page.ProfileId is null || page.IsDirty, "a nameless profile must not be stored");
        }, StepOptions.Expect("Profile_NeedsName"));

        await h.StepAsync("Library", "SaveAsAndReload", async ctx =>
        {
            int segments = page.Segments.Count;
            page.ProfileName = SelfTestNames.ProfileA;
            await h.RunAsync(page.SaveAsCommand);
            ctx.Check(page.NamePrompt.IsOpen, "save-as should ask for a name first");
            ctx.Check(page.NamePrompt.Name.Contains(SelfTestNames.ProfileA, StringComparison.Ordinal),
                "the name box should be prefilled from the current name, is " + page.NamePrompt.Name);
            h.TryScreenshot("profile-save-as");
            page.NamePrompt.Name = SelfTestNames.ProfileA;
            await h.RunAsync(page.NamePrompt.ConfirmCommand);
            ctx.Check(!page.NamePrompt.IsOpen, "a free name should be stored and close the box, error: " + page.NamePrompt.ErrorText);
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

        await h.StepAsync("Library", "SaveAsRefusesTakenName", async ctx =>
        {
            string? idBefore = page.ProfileId;
            await h.RunAsync(page.SaveAsCommand);
            page.NamePrompt.Name = SelfTestNames.ProfileA;
            await h.RunAsync(page.NamePrompt.ConfirmCommand);
            ctx.Check(page.NamePrompt.IsOpen && page.NamePrompt.ErrorText.Length > 0,
                "a name already in the library must be refused inside the box");
            h.TryScreenshot("profile-save-as-taken");
            await h.RunAsync(page.NamePrompt.CancelCommand);
            ctx.Check(!page.NamePrompt.IsOpen && page.ProfileId == idBefore, "cancel should leave everything as it was");
        });

        await h.StepAsync("Library", "OldProfileIsConvertedToAPointTable", async ctx =>
        {
            // 阶段 0 以前存的叠加辊形：打开时按原来的合成曲线转成一段点表，形状不变。
            var old = CompositeRollProfile.Superimposed(new[]
            {
                RollProfileSegment.Create(1, ProfileTypeKeys.Crown, 0.0, 2000.0,
                    new CrownProfileType().Schema.CreateDefaults()
                        .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(300.0))),
                RollProfileSegment.Create(2, ProfileTypeKeys.Taper, 0.0, 150.0, new TaperProfileType().Schema.CreateDefaults()),
            });
            await h.Services.GetRequiredService<RollGrinder.Data.IRollProfileRepository>().SaveAsync(
                RollGrinder.Core.Profiles.RollProfileDefinition.Create(
                    "P-SELFTEST-OLD", SelfTestNames.OldProfile, 2000.0, old, DateTimeOffset.UtcNow),
                System.Threading.CancellationToken.None);

            await h.RunAsync(page.OpenLibraryCommand);
            page.SelectedLibraryEntry = page.LibraryEntries.First(e => e.Name == SelfTestNames.OldProfile);
            await h.RunAsync(page.LoadFromLibraryCommand);
            ctx.Check(page.StatusResourceKey == "Profile_LegacyConverted", "the operator should be told it was converted");
            ctx.Check(page.Composite?.Segments.Count == 1 && page.Composite.Segments[0].ProfileTypeKey == ProfileTypeKeys.PointTable,
                "an old profile should open as one point table");
            ctx.Check(page.IsDirty && !page.HasErrors, "the converted profile is clean but not yet saved: " + Issues(page));
            h.TryScreenshot("profile-legacy-converted");
        });

        await h.StepAsync("Library", "DeleteSecondProfile", async ctx =>
        {
            ctx.Check(await SelfTestNames.SaveAsAsync(h, page.SaveAsCommand, page.NamePrompt, SelfTestNames.ProfileB),
                "save-as B should go through, error: " + page.NamePrompt.ErrorText);
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
            await h.RunAsync(page.RequestImportReferenceCommand);
            await h.SettleAsync(200);
            ctx.Check(page.StatusResourceKey == "Profile_PointsImported", "status should say imported, is " + page.StatusResourceKey);
            ctx.Check(page.ReferencePoints.Count > 10, "the reference line should be drawn");
            ctx.Note("reference deviation=" + page.ReferenceDeviationText);
        }, StepOptions.Shot);

        await h.StepAsync("Points", "ImportPointTableAsANewSegment", async ctx =>
        {
            if (generated is null)
            {
                ctx.Skip("no generated CSV to import");
            }

            int count = page.Segments.Count;
            page.SelectedSegment = page.Segments.Last();
            h.Interaction.OpenAnswers.Enqueue(generated!);
            await h.PressKeyAsync(ctx, "Fn_ImportPoints");
            await h.SettleAsync(200);
            ctx.Check(page.StatusResourceKey == "Profile_PointsImportedIntoSegment", "status should say imported, is " + page.StatusResourceKey);
            ctx.Check(page.Segments.Count == count + 1 && page.IsPointTableSelected && page.PointRows.Count > 10,
                "the table should arrive as a new point-table segment");
            page.DiscardChanges();
            await h.SettleAsync();
            ctx.Check(page.Segments.Count == count, "discard should drop the imported segment");
        });

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

        await h.StepAsync("Segments", "DeleteToEmptyCannotSave", async ctx =>
        {
            int before = page.Segments.Count;
            while (page.Segments.Count > 0)
            {
                page.SelectedSegment = page.Segments.Last();
                await h.RunAsync(page.RemoveSegmentCommand);
            }

            ctx.Check(page.Segments.Count == 0, "every segment should be removable");
            ctx.Check(page.HasErrors && page.Issues.Any(i => i.IsError), "an empty profile must be reported");
            ctx.Check(!page.SaveAsCommand.CanExecute(null), "an empty profile must not be saved");
            h.TryScreenshot("profile-empty");

            page.DiscardChanges();
            await h.SettleAsync();
            ctx.Check(page.Segments.Count == before && !page.HasErrors, "discard should bring the loaded profile back");
        });
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Issues(ProfileViewModel page) => string.Join(" | ", page.Issues.Select(i => i.Text));

    private static async Task RemoveAllAsync(SelfTestHarness h, ProfileViewModel page)
    {
        page.IsSymmetric = false;
        while (page.Segments.Count > 0)
        {
            page.SelectedSegment = page.Segments.Last();
            await h.RunAsync(page.RemoveSegmentCommand);
        }
    }
}

/// <summary>工艺程序：新程序只有开始/结束、每种工序、首尾固定、程序步骤开关、校验、程序库、没存不能用于作业。</summary>
internal sealed class StepsSuite : ISelfTestSuite
{
    public string Name => "Steps";

    public async Task RunAsync(SelfTestHarness h)
    {
        StepsViewModel page = h.Page<StepsViewModel>();
        await h.GoToAsync(PageKey.Steps);

        await h.StepAsync("Program", "NewProgramHasFrame", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_NewProgram");
            ctx.Check(page.Steps.Count == 2, Invariant($"a new program holds start and end only, has {page.Steps.Count}"));
            ctx.Check(page.Steps[0].StepTypeKey == StepTypeKeys.Start && page.Steps[^1].StepTypeKey == StepTypeKeys.End,
                "start first, end last");
            ctx.Check(page.ProgramId is null && page.ProgramName.Length == 0, "a new program has no identity yet");
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
            List<StepTypeOptionViewModel> addable = page.StepTypeOptions
                .Where(o => o.IsAvailable && !ProgramFrame.IsFixed(o.Key))
                .ToList();
            foreach (StepTypeOptionViewModel option in addable)
            {
                page.SelectedStepType = option;
                await h.RunAsync(page.AddStepCommand);
            }

            int expected = addable.Count + 2;
            ctx.Check(page.Steps.Count == expected, Invariant($"expected {expected} steps, got {page.Steps.Count}"));
            ctx.Check(page.Steps[^1].StepTypeKey == StepTypeKeys.End, "new steps go in before the end, which stays last");
            ctx.Note("total duration " + page.TotalDurationText);
        }, StepOptions.Shot);

        await h.StepAsync("StepTypes", "FrameTypesRefused", async ctx =>
        {
            int count = page.Steps.Count;
            page.SelectedStepType = page.StepTypeOptions.FirstOrDefault(o => o.Key == StepTypeKeys.Start);
            if (page.SelectedStepType is null)
            {
                ctx.Skip("the start step is not offered in the list");
            }

            await h.RunAsync(page.AddStepCommand);
            ctx.Check(page.Steps.Count == count, "a second start must not be added");
            ctx.Check(page.StatusResourceKey == "Program_FrameFixed", "status should say start/end are fixed, is " + page.StatusResourceKey);

            page.RemoveStepCommand.Execute(page.Steps[0]);
            await h.SettleAsync();
            ctx.Check(page.Steps.Count == count && page.Steps[0].StepTypeKey == StepTypeKeys.Start, "the start must not be removed");
            ctx.Check(!page.Steps[0].CanBeMoved && !page.Steps[^1].CanBeMoved, "start and end show no move/remove buttons");
        });

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
            page.RemoveStepCommand.Execute(page.Steps[^2]);
            await h.SettleAsync();
            ctx.Check(page.Steps.Count == count - 1, "remove should drop one step");
            ctx.Check(page.Steps[^1].StepTypeKey == StepTypeKeys.End, "the end stays last");
        });

        await h.StepAsync("StepTypes", "MoveUpAndDown", async ctx =>
        {
            ctx.Check(page.Steps.Count >= 4, "need two steps between start and end to reorder");
            StepRowViewModel first = page.Steps[1];
            StepRowViewModel second = page.Steps[2];
            page.MoveStepDownCommand.Execute(first);
            await h.SettleAsync();
            ctx.Check(page.Steps[1] == second && page.Steps[2] == first, "move down should swap with the next step");
            ctx.Check(page.Steps[1].Order == 2 && page.Steps[2].Order == 3, "orders should be renumbered");
            page.MoveStepUpCommand.Execute(first);
            await h.SettleAsync();
            ctx.Check(page.Steps[1] == first && page.Steps[2] == second, "move up should swap it back");
            page.MoveStepUpCommand.Execute(first);
            await h.SettleAsync();
            ctx.Check(page.Steps[1] == first && page.Steps[0].StepTypeKey == StepTypeKeys.Start, "nothing moves in front of the start");
            ctx.Check(page.StatusResourceKey == "Program_FrameFixed", "status should say start/end are fixed, is " + page.StatusResourceKey);
        });

        await h.StepAsync("VerticalKeys", "InsertByCategoryCopyDelete", async ctx =>
        {
            // 竖向软键：插入工序 ▸ → 磨削 ▸ → 精磨，插在选中那道之后并选中它；复制一份；再删掉复制的那份。
            page.SelectedStep = page.Steps[1];
            int count = page.Steps.Count;
            await h.PressVerticalKeyAsync(ctx, "Vk_InsertStep");
            await h.PressVerticalKeyAsync(ctx, "Vk_CatGrinding");
            ctx.Check(h.IndexOfVerticalKey("Vk_Back") == PageViewModelBase.VerticalKeyCount - 1, "the nested menu keeps 'back' in slot 8");
            await h.PressVerticalKeyAsync(ctx, "StepType_" + StepTypeKeys.Finish);
            ctx.Check(page.Steps.Count == count + 1 && page.Steps[2].StepTypeKey == StepTypeKeys.Finish,
                "the chosen step should be inserted right after the selected one");
            ctx.Check(page.SelectedStep == page.Steps[2], "the new step should be selected");
            ctx.Check(h.IndexOfVerticalKey("Vk_InsertStep") == 0, "choosing a step should return to the root keys");

            await h.PressVerticalKeyAsync(ctx, "Vk_CopyStep");
            ctx.Check(page.Steps.Count == count + 2 && page.Steps[3].StepTypeKey == StepTypeKeys.Finish, "copy should add the same step after it");
            await h.PressVerticalKeyAsync(ctx, "Vk_DeleteStep");
            await h.PressVerticalKeyAsync(ctx, "Vk_DeleteStep");
            ctx.Check(page.Steps.Count == count, "delete should remove the selected step");

            // 辅助动作：只有一种工序的类别直接插，不再开一层；动作与开关是分段键。
            if (page.StepTypeOptions.Any(o => o.Key == StepTypeKeys.Auxiliary))
            {
                await h.PressVerticalKeyAsync(ctx, "Vk_InsertStep");
                await h.PressVerticalKeyAsync(ctx, "StepType_" + StepTypeKeys.Auxiliary);
                ctx.Check(page.SelectedStep?.StepTypeKey == StepTypeKeys.Auxiliary, "the auxiliary action should be inserted and selected");
                ctx.Check(page.SelectedStep!.Parameters.Any(p => p.Key == StepParameterKeys.AuxAction && p.Choices.Count >= 2),
                    "the actions registered in machine.json should be offered");
                await h.PressVerticalKeyAsync(ctx, "Vk_DeleteStep");
            }
            else
            {
                ctx.Note("machine.json registers no auxiliary actions: the auxiliary step is not offered");
            }

            // 两层子菜单里按 Esc：一次只退一层，不离开本页。
            await h.PressVerticalKeyAsync(ctx, "Vk_InsertStep");
            await h.PressVerticalKeyAsync(ctx, "Vk_CatMeasuring");
            h.Shell.PressEscape();
            await h.SettleAsync();
            ctx.Check(page.VerticalMenuTitle == page.Localizer["Vk_InsertStepTitle"], "Esc should climb one level");
            h.Shell.PressEscape();
            await h.SettleAsync();
            ctx.Check(h.IndexOfVerticalKey("Vk_InsertStep") == 0 && h.Shell.CurrentPage.Key == PageKey.Steps,
                "a second Esc returns to the root keys and stays on the page");
            h.TryScreenshot("steps-vertical-keys");
        });

        // 跨工序检查与余量分配（修改稿 5.3）：总余量对不上时提示；"余量分配"按比例分好，提示消失；"默认值"把一道参数放回默认。
        await h.StepAsync("CrossStep", "AllocateStockAndDefaults", async ctx =>
        {
            StepRowViewModel? grinding = page.Steps.FirstOrDefault(step =>
                step.Parameters.Any(row => row.Key == StepParameterKeys.StockDiameterMicrometer)
                && step.StepTypeKey is StepTypeKeys.Rough or StepTypeKeys.SemiFinish or StepTypeKeys.Finish);
            if (grinding is null)
            {
                ctx.Skip("the program has no grinding step");
            }

            page.TotalStockText = "987";
            await h.SettleAsync(50);
            ctx.Check(page.ProgramHints.Count > 0, "a total that does not match the steps should give a hint");
            ctx.Note(string.Join(" | ", page.ProgramHints));

            await h.PressVerticalKeyAsync(ctx, "Vk_AllocateStock");
            double sum = page.Steps
                .SelectMany(step => step.Parameters.Where(row => row.Key == StepParameterKeys.StockDiameterMicrometer).Select(row => (step, row)))
                .Where(pair => pair.step.Parameters.Any(r => r.Key == StepParameterKeys.InfeedPerPassDiameterMicrometer
                    && double.TryParse(r.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double infeed) && infeed > 0)
                    || pair.step.Parameters.Any(r => r.Key == StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin
                    && double.TryParse(r.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double continuous) && continuous > 0))
                .Sum(pair => double.Parse(pair.row.Text, CultureInfo.InvariantCulture));
            ctx.Check(Math.Abs(sum - 987) < 0.2, Invariant($"allocated stock should add up to 987 µm, is {sum}"));
            ctx.Check(!page.ProgramHints.Any(hint => hint.Contains("987", StringComparison.Ordinal)), "the stock hint should be gone");
            h.TryScreenshot("steps-allocate-stock");

            page.SelectedStep = grinding;
            ParameterRowViewModel stock = grinding!.Parameters.First(row => row.Key == StepParameterKeys.StockDiameterMicrometer);
            string defaultStock = grinding.StepType.Schema.Get(StepParameterKeys.StockDiameterMicrometer).DefaultValue.ToInvariantString();
            await h.PressVerticalKeyAsync(ctx, "Vk_StepDefaults");
            ctx.Check(stock.Text == defaultStock, "defaults should put the stock back to " + defaultStock + ", is " + stock.Text);

            await h.PressVerticalKeyAsync(ctx, "Vk_ProgramOptions");
            page.TotalStockText = string.Empty;
            await h.SettleAsync(50);
        });

        await h.StepAsync("Diagram", "EveryParameterLightsUpWithAHelpLine", async ctx =>
        {
            // 每道工序、每个参数：简图亮对应的量，说明行写全（没有 "!键!"）。每种简图截一张。
            var shot = new HashSet<RollGrinder.App.Controls.StepDiagramKind>();
            foreach (StepRowViewModel step in page.Steps.ToList())
            {
                page.SelectedStep = step;
                await h.SettleAsync(20);
                foreach (ParameterRowViewModel row in step.Parameters)
                {
                    page.FocusedParameter = row;
                    ctx.Check(page.FocusedParameterKey == row.Key, "the diagram should follow the focused parameter");
                    ctx.Check(page.ParameterHelpText.Length > 0 && !page.ParameterHelpText.Contains('!'),
                        step.StepTypeKey + "." + row.Key + " has an incomplete help line: " + page.ParameterHelpText);
                }

                RollGrinder.App.Controls.StepDiagramKind kind = RollGrinder.App.Controls.StepDiagramMap.KindOf(step.StepTypeKey);
                if (shot.Add(kind) && step.Parameters.Count > 1)
                {
                    page.FocusedParameter = step.Parameters[1];
                    await h.SettleAsync(50);
                    h.TryScreenshot("steps-diagram-" + kind);
                }
            }

            ctx.Note(shot.Count + " diagram kinds shown");
        });

        await h.StepAsync("Validation", "ProgramValidates", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_Validate");
            ctx.Note("status=" + page.StatusResourceKey + ", violations=" + page.Violations.Count);
            ctx.Check(page.StatusResourceKey == "Program_Valid",
                "a program built from default parameters of every step type should validate; status " + page.StatusResourceKey
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

        await h.StepAsync("UseForJob", "UnsavedProgramRefused", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_UseForJob");
            ctx.Check(h.Shell.CurrentPage.Key == PageKey.Steps, "an unsaved program must not be handed to a job");
            ctx.Check(page.StatusResourceKey == "Program_SaveBeforeUse", "status should ask to save first, is " + page.StatusResourceKey);
        });

        await h.StepAsync("ProgramLibrary", "SaveWithoutNameRefused", async ctx =>
        {
            page.ProgramName = string.Empty;
            await h.PressKeyAsync(ctx, "Fn_SaveProgram");
            ctx.Check(page.IsDirty, "a nameless program must not be stored");
        }, StepOptions.Expect("Program_NeedsName"));

        await h.StepAsync("ProgramLibrary", "InvalidProgramNotSaved", async ctx =>
        {
            // 把一道工序的一格改到范围外：保存要被拒，原因列在校验结果里。
            ParameterRowViewModel cell = page.Steps
                .SelectMany(step => step.Parameters)
                .First(row => row.Kind == ParameterValueKind.Number && row.RangeText.Length > 0);
            string original = cell.Text;
            cell.Text = "999999";
            page.ProgramName = SelfTestNames.ProgramA;
            await h.PressKeyAsync(ctx, "Fn_SaveProgram");
            ctx.Check(page.ProgramId is null || page.IsDirty, "an out-of-range program must not be stored");
            ctx.Check(page.Violations.Count > 0, "the reason should be listed");
            h.TryScreenshot("steps-save-refused");
            cell.Text = original;
            await h.SettleAsync();
        }, StepOptions.Expect("Program_HasErrors"));

        await h.StepAsync("ProgramLibrary", "SaveAsNewProgramLoad", async ctx =>
        {
            int steps = page.Steps.Count;
            page.ProgramName = SelfTestNames.ProgramA;
            ctx.Check(await SelfTestNames.SaveAsAsync(h, page.SaveProgramAsCommand, page.NamePrompt, SelfTestNames.ProgramA),
                "save-as A should go through, error: " + page.NamePrompt.ErrorText);
            await h.PressKeyAsync(ctx, "Fn_NewProgram");
            ctx.Check(page.Steps.Count == 2, "a new program keeps only start and end");
            ctx.Check(page.ProgramId is null && string.IsNullOrEmpty(page.ProgramName), "a new program must not keep the old program's identity");
            await h.PressKeyAsync(ctx, "Fn_ProgramLibrary");
            ctx.Check(page.IsProgramLibraryOpen, "program library should open");
            h.TryScreenshot("steps-program-library");
            page.SelectedProgramEntry = page.ProgramLibraryEntries.FirstOrDefault(p => p.Name == SelfTestNames.ProgramA);
            ctx.Check(page.SelectedProgramEntry is not null, "saved program should be listed");
            await h.RunAsync(page.LoadProgramCommand);
            ctx.Check(page.Steps.Count == steps, Invariant($"loaded program should have {steps} steps, has {page.Steps.Count}"));
            ctx.Check(!page.IsDirty, "a freshly loaded program is clean");
        }, StepOptions.Expect("Program_Saved"));

        await h.StepAsync("ProgramLibrary", "DeleteSecondProgram", async ctx =>
        {
            ctx.Check(await SelfTestNames.SaveAsAsync(h, page.SaveProgramAsCommand, page.NamePrompt, SelfTestNames.ProgramB),
                "save-as B should go through, error: " + page.NamePrompt.ErrorText);
            await h.RunAsync(page.OpenProgramLibraryCommand);
            page.SelectedProgramEntry = page.ProgramLibraryEntries.First(p => p.Name == SelfTestNames.ProgramB);
            await h.RunAsync(page.DeleteProgramCommand);
            ctx.Check(page.ProgramLibraryEntries.All(p => p.Name != SelfTestNames.ProgramB), "deleted program should disappear");
            await h.RunAsync(page.CloseProgramLibraryCommand);
        }, StepOptions.Expect("Program_Saved"));

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

        await h.StepAsync("Wheel", "DataDressingAndHistory", async ctx =>
        {
            // 砂轮页（修改稿 5.7）：砂轮数据、修整参数各配简图，光标所在参数亮起、说明行写全；换砂轮记进记录。
            await h.PressKeyAsync(ctx, "Fn_Wheel");
            ctx.Check(page.ActiveSubViewKey == SettingsViewModel.WheelSubView, "the wheel sub view should open");
            ctx.Check(page.WheelRows.Count == 3 && page.DressRows.Count == 4, "wheel data and dressing rows should be listed");
            foreach (ParameterRowViewModel row in page.WheelRows.Concat(page.DressRows))
            {
                page.FocusedWheelKey = row.Key;
                ctx.Check(page.WheelHelpText.Length > 0 && !page.WheelHelpText.Contains('!'),
                    row.Key + " has an incomplete help line: " + page.WheelHelpText);
            }

            page.FocusedWheelKey = RollGrinder.Core.Calibration.CalibrationKeys.DressInfeedRadiusMicrometer;
            await h.WaitUntilAsync(() => page.WheelHistory.Count > 0, TimeSpan.FromSeconds(5));
            ctx.Note(page.WheelHistory.Count + " history rows, max surface speed " + page.MaxSurfaceSpeedText);
            if (!offline)
            {
                ctx.Check(page.WheelHistory.Any(entry => entry.KindText == page.Localizer["WheelEvent_Change"]),
                    "the wheel change finished above should be in the history");
            }

            h.TryScreenshot("wheel-page");
            await h.PressNavigationKeyAsync();
            ctx.Check(page.ActiveSubViewKey is null, "the navigation key should close the wheel sub view");
        });

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

        // 手动页分 6 页（修改稿 5.6）：每页一个功能键，页里的动作一个一个在竖键上按。
        foreach (ManualGroupViewModel group in page.Groups.ToList())
        {
            await h.StepAsync("Groups", group.Key, async ctx =>
            {
                await h.PressKeyAsync(ctx, "ManualGroup_" + group.Key);
                ctx.Check(page.SelectedGroup == group, "the page key should switch to " + group.Key);
                ctx.Check(group.VerticalKeys.Count is > 0 and <= 8, Invariant($"a page holds 1–8 vertical keys, has {group.VerticalKeys.Count}"));
                ctx.Check(h.Shell.CurrentPage.VerticalKeys.Take(group.VerticalKeys.Count)
                        .Select(k => k.LabelResourceKey).SequenceEqual(group.VerticalKeys.Select(k => k.LabelResourceKey)),
                    "the vertical bar should show this page's actions");
                ctx.Note(string.Join(", ", group.Lamps.Select(l => l.Label + "=" + l.StateText)));
            }, StepOptions.Shot);

            foreach (MachineActionViewModel action in group.Actions)
            {
                await h.StepAsync("Actions_" + group.Key, action.Descriptor.Key, async ctx =>
                {
                    await PressActionOnVerticalKeyAsync(h, ctx, page, action);
                });
            }
        }

        await h.StepAsync("Status", "QuillLampFollowsTheCommand", async ctx =>
        {
            ManualGroupViewModel tailstock = page.Groups.First(g => g.Key == "tailstock");
            await h.PressKeyAsync(ctx, "ManualGroup_tailstock");
            StatusLampViewModel quill = tailstock.Lamps.First(l => l.LabelResourceKey == "Status_quill");
            MachineActionViewModel extend = tailstock.Actions.First(a => a.Descriptor.Key == "quill.extend");
            if (!extend.IsEnabled)
            {
                ctx.Skip("quill.extend cannot be pressed in the current machine state");
            }

            await PressActionOnVerticalKeyAsync(h, ctx, page, extend);
            await h.SettleAsync(600);
            if (quill.IsUnknown)
            {
                ctx.Skip("the quill status bit is not mapped (Q7 address not given yet)");
            }

            ctx.Check(quill.IsOn, "after 'quill extend' the quill lamp should be on, is " + quill.StateText);
        }, StepOptions.Shot);

        await h.StepAsync("Keys", "AuxiliaryCyclesMenu", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_AuxCycles");
            foreach (MachineActionViewModel cycle in page.CycleActions)
            {
                ctx.Check(h.IndexOfVerticalKey(cycle.Descriptor.ResourceKey) >= 0, cycle.Descriptor.Key + " should be in the cycles menu");
            }

            ctx.Check(h.IndexOfVerticalKey("Fn_HmiReset") >= 0, "HMI reset should be in the cycles menu");
            h.TryScreenshot("manual-aux-cycles");
        });

        foreach (MachineActionViewModel action in page.CycleActions.ToList())
        {
            await h.StepAsync("Actions_Cycles", action.Descriptor.Key, async ctx =>
            {
                await PressActionOnVerticalKeyAsync(h, ctx, page, action);
            });
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
            if (h.IndexOfVerticalKey("Fn_HmiReset") < 0)
            {
                await h.PressKeyAsync(ctx, "Fn_AuxCycles");
            }

            await h.PressVerticalKeyAsync(ctx, "Fn_HmiReset");
            ctx.Check(h.Services.GetRequiredService<IAlarmLog>().Snapshot().Count == 0, "HMI reset should clear the alarm list");
            h.Shell.PressEscape();
            await h.SettleAsync();
        });
    }

    /// <summary>
    /// 在竖键上按一个动作：按不了就跳过（缺映射、机床在忙）；要按两下的，第一下之后键上换成"再按一次"，在同一格再按。
    /// </summary>
    private static async Task PressActionOnVerticalKeyAsync(
        SelfTestHarness h, StepContext ctx, ManualViewModel page, MachineActionViewModel action)
    {
        int index = h.IndexOfVerticalKey(action.Descriptor.ResourceKey);
        ctx.Check(index >= 0, "vertical key for " + action.Descriptor.Key + " should be on the bar");
        if (index < 0)
        {
            return;
        }

        if (!action.IsEnabled || !h.IsVerticalKeyUsable(index))
        {
            ctx.Skip(action.IsMapped ? "not allowed in the current machine state" : "not mapped in tagmap");
        }

        h.Shell.PressVerticalKey(index);
        await h.SettleAsync();
        if (action.IsAwaitingConfirmation)
        {
            ctx.Check(h.Shell.CurrentPage.VerticalKeys[index].LabelResourceKey == "Manual_ConfirmAgain",
                "the armed key should say 'press again'");
            ctx.Note("asked for confirmation, pressed again");
            h.Shell.PressVerticalKey(index);
            await h.SettleAsync();
        }

        ctx.Check(!action.IsAwaitingConfirmation, "action should not stay armed after the second press");
        ctx.Note("feedback: " + page.LastActionText);
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

        await h.StepAsync("Ledger", "RegisterEditAndRefuseDuplicate", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_RollLedger");

            // 登记一支新辊：尺寸、类型、当前直径、重量都在台账里填。
            await h.RunAsync(page.NewLedgerRollCommand);
            ctx.Check(page.IsNewLedgerRoll && page.LedgerRollId.Length == 0, "a new roll starts from an empty form");
            page.LedgerRollId = SelfTestNames.LedgerRollId;
            page.SetLedgerKindCommand.Execute(RollGrinder.Data.Model.RollKind.BackupRoll);
            page.LedgerBodyLengthText = "2000";
            page.LedgerDiameterText = "1200";
            page.LedgerCurrentDiameterText = "1188";
            page.LedgerNetWeightText = "30000";
            page.LedgerHeadBoxWeightText = "2500";
            page.LedgerTailBoxWeightText = "2400";
            ctx.Check(page.LedgerTotalWeightText.Length > 2, "the total lift weight should be summed");
            await h.RunAsync(page.SaveLedgerRollCommand);
            ctx.Check(page.LedgerProblems.Count == 0, "a valid roll should be saved: " + string.Join(" | ", page.LedgerProblems));
            ctx.Check(page.SelectedLedgerRow?.RollId == SelfTestNames.LedgerRollId && !page.IsNewLedgerRoll,
                "the saved roll should be selected for editing");
            h.TryScreenshot("records-ledger-edit");

            // 改当前直径：同一支辊，不算重号。
            page.LedgerCurrentDiameterText = "1180";
            await h.RunAsync(page.SaveLedgerRollCommand);
            ctx.Check(page.LedgerProblems.Count == 0 && page.SelectedLedgerRow?.CurrentDiameterText.StartsWith("1180", StringComparison.Ordinal) == true,
                "editing an existing roll should be saved");

            // 再登记一支同号的：拒绝并说明。
            await h.RunAsync(page.NewLedgerRollCommand);
            page.LedgerRollId = SelfTestNames.LedgerRollId;
            page.LedgerBodyLengthText = "2000";
            page.LedgerDiameterText = "650";
            await h.RunAsync(page.SaveLedgerRollCommand);
            ctx.Check(page.LedgerProblems.Count == 1, "a duplicate roll number must be refused with its reason");
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

        // 机床配置编辑（修改稿 5.8）：改错了就地标红、存不进去；改对了保存，原文件先备份、写改动记录；再恢复上一版存回去。
        await h.StepAsync("ConfigEditor", "MachineConfigEditSaveRestore", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_MachineConfig");
            ctx.Check(page.ActiveSubViewKey == DiagnosticsViewModel.MachineConfigSubView, "the machine config editor should open");
            ctx.Check(page.MachineGroups.Count >= 5, Invariant($"the form should be grouped, has {page.MachineGroups.Count} groups"));
            ctx.Check(page.ConfigIssueCount == 0, "the sample machine.json should be clean: " + string.Join(" | ", page.ConfigIssues));

            ConfigFieldViewModel weight = page.MachineGroups.SelectMany(g => g.Fields).First(f => f.Path == "workpiece.maxWeightKg");
            string original = weight.Text;
            weight.Text = "-1";
            await h.SettleAsync(50);
            ctx.Check(weight.HasIssue && page.ConfigIssueCount > 0, "a negative weight must be marked at once");
            weight.Text = "abc";
            await h.SettleAsync(50);
            ctx.Check(weight.HasIssue, "text in a number field must be marked");
            h.TryScreenshot("config-machine-errors");

            weight.Text = (double.Parse(original, CultureInfo.CurrentCulture) - 1000).ToString(CultureInfo.CurrentCulture);
            await h.SettleAsync(50);
            ctx.Check(!weight.HasIssue && page.ConfigIssueCount == 0, "a valid weight should clear the error");
            ctx.Check(page.IsDirty, "an edited config should count as unsaved");
            int backupsBefore = h.Services.GetRequiredService<ConfigDocumentStore>().ListBackups(ConfigFileKind.Machine).Count;
            await h.PressVerticalKeyAsync(ctx, "Vk_SaveConfig");
            await h.SettleAsync(300);
            ctx.Check(!page.IsDirty, "saving should clear the unsaved mark: " + page.ConfigStatusText);
            ctx.Check(h.Services.GetRequiredService<ConfigDocumentStore>().ListBackups(ConfigFileKind.Machine).Count == backupsBefore + 1,
                "the old file should have been backed up");
            ctx.Note(page.ConfigStatusText);

            await h.PressVerticalKeyAsync(ctx, "Vk_RestorePrevious");
            await h.SettleAsync(300);
            ConfigFieldViewModel restored = page.MachineGroups.SelectMany(g => g.Fields).First(f => f.Path == "workpiece.maxWeightKg");
            ctx.Check(restored.Text == original, "the previous version should be loaded into the editor, weight is " + restored.Text);
            await h.PressVerticalKeyAsync(ctx, "Vk_SaveConfig");
            await h.SettleAsync(300);
            ctx.Check(!page.IsDirty, "the restored version should be saved back");
            h.TryScreenshot("config-machine");
            await h.RecoverAsync();
        }, StepOptions.Expect("*"));

        await h.StepAsync("ConfigEditor", "TagMapSearchMissingTestRead", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_TagMapping");
            ctx.Check(page.ActiveSubViewKey == DiagnosticsViewModel.TagMappingSubView, "the tag map editor should open");
            ctx.Check(page.VisibleTags.Count > 50, Invariant($"the sample map should be listed, has {page.VisibleTags.Count} rows"));
            ctx.Check(page.ConfigIssueCount == 0, "the sample tagmap.json should be clean: " + page.ConfigStatusText);

            page.TagSearchText = "status.";
            await h.SettleAsync(50);
            ctx.Check(page.VisibleTags.Count > 0 && page.VisibleTags.All(row => row.Key.Contains("status.", StringComparison.OrdinalIgnoreCase)
                    || row.Address.Contains("status.", StringComparison.OrdinalIgnoreCase)
                    || row.Description.Contains("status.", StringComparison.OrdinalIgnoreCase)),
                "search should filter the rows");
            page.TagSearchText = string.Empty;

            await h.PressVerticalKeyAsync(ctx, "Vk_OnlyMissing");
            await h.SettleAsync(50);
            ctx.Check(page.VisibleTags.All(row => row.IsMissing || row.IssueText.Length > 0), "only missing or faulty rows should remain");
            ctx.Note(Invariant($"missing rows={page.VisibleTags.Count}"));
            await h.PressVerticalKeyAsync(ctx, "Vk_OnlyMissing");
            await h.SettleAsync(50);

            page.SelectedTag = page.VisibleTags.FirstOrDefault(row => row.Key == MachineTagKeys.ChannelState);
            ctx.Check(page.SelectedTag is not null, "the channel state should be in the map");
            await h.PressVerticalKeyAsync(ctx, "Vk_TestRead");
            await h.SettleAsync(200);
            ctx.Note("test read: " + page.ConfigStatusText);
            ctx.Check(page.ConfigStatusText.Length > 0, "the test read should say what came back");
            h.TryScreenshot("config-tagmap");
            await h.RecoverAsync();
        }, StepOptions.Expect("*"));

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
