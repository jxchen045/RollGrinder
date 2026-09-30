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
using RollGrinder.Services.Manual;
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

    /// <summary>到库区某一组，等列表载完，选中名为 <paramref name="name"/> 的一条。没有返回 false。</summary>
    public static async Task<bool> SelectAsync(SelfTestHarness h, string group, string name)
    {
        LibraryViewModel library = h.Page<LibraryViewModel>();
        await h.GoToAsync(PageKey.Library, groupKey: group);
        if (!await h.WaitUntilAsync(() => library.Entries.Any(e => e.Name == name), TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        library.SelectedEntry = library.Entries.First(e => e.Name == name);
        await h.SettleAsync();
        return true;
    }

    /// <summary>从库区打开一条（竖键"打开"），到辊形区 / 工艺区编辑；等它载完。</summary>
    public static async Task OpenFromLibraryAsync(SelfTestHarness h, StepContext ctx, string group, string name)
    {
        ctx.Check(await SelectAsync(h, group, name), name + " should be listed in the library");
        await h.PressVerticalKeyAsync(ctx, "Vk_Open");
        await h.SettleAsync(400);
        PageKey expected = group == LibraryViewModel.ProfilesGroup ? PageKey.Profile : PageKey.Steps;
        ctx.Check(h.Shell.CurrentPage.Key == expected, "'open' should go to " + expected);
    }

    /// <summary>从库区删一条（竖键"删除这一条…"，问一句、确认）。</summary>
    public static async Task DeleteFromLibraryAsync(SelfTestHarness h, StepContext ctx, string group, string name)
    {
        ctx.Check(await SelectAsync(h, group, name), name + " should be listed in the library");
        await h.PressVerticalKeyAsync(ctx, "Vk_Delete");
        await h.ConfirmAsync(ctx);
        LibraryViewModel library = h.Page<LibraryViewModel>();
        ctx.Check(await h.WaitUntilAsync(() => library.Entries.All(e => e.Name != name), TimeSpan.FromSeconds(5)), name + " should disappear");
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
                int insertSlot = type == ProfileTypeKeys.PointTable ? 4 : 0;
                ctx.Check(h.IndexOfVerticalKey("Vk_InsertSegment") == insertSlot,
                    "choosing a type should return to the root keys (a point table puts add / remove point first)");
                if (type == ProfileTypeKeys.PointTable)
                {
                    ctx.Check(h.IndexOfVerticalKey("Vk_AddPoint") == 0 && h.IndexOfVerticalKey("Vk_NextPage") == PageViewModelBase.VerticalKeyCount - 1,
                        "a point table shows add point first and pages the rest behind ≡▸");
                }
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

            // Ctrl+C / V（最终稿 4.6）：同一件事走剪贴板，粘在选中段之后；Ctrl+X 剪回去。
            page.SelectedSegment = page.Segments.Last();
            ctx.Check(h.Shell.Clipboard(ClipboardAction.Copy), "the segment table takes Ctrl+C");
            h.Shell.Clipboard(ClipboardAction.Paste);
            await h.SettleAsync();
            ctx.Check(page.Segments.Count == count + 1 && page.SelectedSegment?.Order == count + 1, "Ctrl+V pastes after the selection and selects it");
            h.Shell.Clipboard(ClipboardAction.Cut);
            await h.SettleAsync();
            ctx.Check(page.Segments.Count == count && !page.HasErrors, "Ctrl+X removes it again: " + Issues(page));
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
            ctx.Check(!h.IsKeyUsable(saveKey), "save must be unavailable while there are errors");
            ctx.Check(!page.SaveAsCommand.CanExecute(null), "save-as must be disabled while there are errors");
            h.TryScreenshot("profile-live-errors");

            // 正在输入、还不成立的内容也要说出来。
            page.SegmentLengthText = "-";
            await h.SettleAsync(50);
            ctx.Check(page.HasErrors, "an unfinished length should be reported while typing");

            page.SegmentLengthText = originalLength;
            await h.SettleAsync(50);
            ctx.Check(!page.HasErrors, "restoring the length should clear the error: " + Issues(page));
            ctx.Check(h.IsKeyUsable(saveKey), "save should be available again");

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
            await SelfTestNames.OpenFromLibraryAsync(h, ctx, LibraryViewModel.ProfilesGroup, SelfTestNames.ProfileA);
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

            await SelfTestNames.OpenFromLibraryAsync(h, ctx, LibraryViewModel.ProfilesGroup, SelfTestNames.OldProfile);
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
            await SelfTestNames.DeleteFromLibraryAsync(h, ctx, LibraryViewModel.ProfilesGroup, SelfTestNames.ProfileB);
            ctx.Check(h.Page<LibraryViewModel>().Entries.Any(e => e.Name == SelfTestNames.ProfileA), "the other profile must stay");

            // 把 A 调回编辑器，后面工序页要从库里选它。
            await SelfTestNames.OpenFromLibraryAsync(h, ctx, LibraryViewModel.ProfilesGroup, SelfTestNames.ProfileA);
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

        await h.StepAsync("Clipboard", "CopyCutPaste", async ctx =>
        {
            // Ctrl+C / X / V（最终稿 4.6）：走外壳那条路，和窗口按键同一个入口。
            page.SelectedStep = page.Steps[0];
            ctx.Check(h.Shell.Clipboard(ClipboardAction.Copy) && page.StatusResourceKey == "Program_FrameFixed", "the start cannot be copied");

            StepRowViewModel source = page.Steps[1];
            page.SelectedStep = source;
            int count = page.Steps.Count;
            h.Shell.Clipboard(ClipboardAction.Copy);
            h.Shell.Clipboard(ClipboardAction.Paste);
            await h.SettleAsync();
            ctx.Check(page.Steps.Count == count + 1, Invariant($"paste should add one step, has {page.Steps.Count}"));
            ctx.Check(page.Steps[2].StepTypeKey == source.StepTypeKey && page.SelectedStep == page.Steps[2], "the copy lands right after and is selected");
            ctx.Check(page.Steps[2].Parameters.Select(r => r.Text).SequenceEqual(source.Parameters.Select(r => r.Text)), "the copy keeps the parameters");

            h.Shell.Clipboard(ClipboardAction.Cut);
            await h.SettleAsync();
            ctx.Check(page.Steps.Count == count, "cut removes the selected step");

            page.SelectedStep = page.Steps[^1];
            h.Shell.Clipboard(ClipboardAction.Paste);
            await h.SettleAsync();
            ctx.Check(page.Steps[^1].StepTypeKey == StepTypeKeys.End && page.Steps[^2].StepTypeKey == source.StepTypeKey,
                "pasting with the end selected goes in before the end");
            page.RemoveStepCommand.Execute(page.Steps[^2]);
            await h.SettleAsync();
            ctx.Check(page.Steps.Count == count, "back to where it started");
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
            ctx.Check(h.Shell.CurrentPage.Key == PageKey.Library, "'program library' should open the library area");
            h.TryScreenshot("steps-program-library");
            await SelfTestNames.OpenFromLibraryAsync(h, ctx, LibraryViewModel.ProgramsGroup, SelfTestNames.ProgramA);
            ctx.Check(page.Steps.Count == steps, Invariant($"loaded program should have {steps} steps, has {page.Steps.Count}"));
            ctx.Check(!page.IsDirty, "a freshly loaded program is clean");
        }, StepOptions.Expect("Program_Saved"));

        await h.StepAsync("ProgramLibrary", "DeleteSecondProgram", async ctx =>
        {
            ctx.Check(await SelfTestNames.SaveAsAsync(h, page.SaveProgramAsCommand, page.NamePrompt, SelfTestNames.ProgramB),
                "save-as B should go through, error: " + page.NamePrompt.ErrorText);
            await SelfTestNames.DeleteFromLibraryAsync(h, ctx, LibraryViewModel.ProgramsGroup, SelfTestNames.ProgramB);
            await h.GoToAsync(PageKey.Steps, ctx);
        }, StepOptions.Expect("Program_Saved"));

        page.DiscardChanges();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>设置：标定值的改/存/重载；换砂轮向导走完五步，再走一次中途取消。</summary>
internal sealed class ParametersSuite : ISelfTestSuite
{
    public string Name => "Parameters";

    public async Task RunAsync(SelfTestHarness h)
    {
        ParametersViewModel page = h.Page<ParametersViewModel>();
        bool offline = h.Services.GetRequiredService<RollGrinder.Contracts.IAppOptions>().IsOffline;
        await h.GoToAsync(PageKey.Parameters, groupKey: ParametersViewModel.CalibrationGroup);

        await h.StepAsync("Calibration", "ValuesListed", ctx =>
        {
            ctx.Check(page.IsCalibrationGroup, "the 'calibration' group should be shown");
            ctx.Check(page.CalibrationGroups.Count >= 5, "calibration values should be grouped by part");
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
            ctx.Check(h.Shell.VerticalKeys[6].LabelResourceKey == "Vk_DiscardEdits" && h.Shell.VerticalKeys[7].LabelResourceKey == "Vk_Save",
                "vertical keys 7 / 8 should become 'discard / save' while dirty");
            await h.PressVerticalKeyAsync(ctx, "Vk_Save");
            ctx.Check(!page.IsDirty, "save should clear the dirty flag");
            ctx.Check(h.IndexOfVerticalKey("Vk_Save") < 0, "the commit pair should go away after saving");
            await h.PressVerticalKeyAsync(ctx, "Vk_Reload");
            ParameterRowViewModel reloaded = page.Values.First(r => r.Key == row.Key);
            ctx.Check(
                double.Parse(reloaded.Text, NumberStyles.Float, CultureInfo.CurrentCulture) == double.Parse(edited, NumberStyles.Float, CultureInfo.CurrentCulture),
                "reloaded value should equal the saved one: " + reloaded.Text + " vs " + edited);
            ctx.Note(row.Key + ": " + value.ToString(CultureInfo.InvariantCulture) + " -> " + edited);
        }, StepOptions.Expect("*"));

        await h.StepAsync("Audit", "AuditGroupListsChanges", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_CalibrationAudit");
            await h.SettleAsync(300);
            ctx.Check(page.IsAuditGroup, "the audit group should be shown");
            ctx.Check(page.Audit.Count > 0, "the calibration value saved above should be in the audit");
        });

        string[] machineWrites = offline ? new[] { "*" } : Array.Empty<string>();
        await h.StepAsync("WheelChange", "FullWizard", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_Wheel");
            await h.PressVerticalKeyAsync(ctx, "Vk_ChangeWheel");
            ctx.Check(page.ActiveSubViewKey == ParametersViewModel.WheelChangeSubView, "wheel change wizard should open");
            ctx.Check(h.IndexOfVerticalKey("Vk_WizardNext") == 0, "the wizard's 'next' should be vertical key 1");
            ctx.Check(page.WheelChangeStage == WheelChangeStage.EnterNewWheel, "wizard should start at 'enter new wheel'");
            h.TryScreenshot("wheel-1-enter");

            page.NewWheelDiameterText = "900";
            await h.RunAsync(page.WheelChangeNextCommand);
            ctx.Check(page.WheelChangeStage == WheelChangeStage.SwitchToManualTouch, "stage 2 expected, got " + page.WheelChangeStage);

            await h.RunAsync(page.WheelChangeNextCommand);
            if (offline && page.WheelChangeStage == WheelChangeStage.SwitchToManualTouch)
            {
                ctx.Note("offline: switching touch mode needs the machine, wizard stays at stage 2 as designed");
                await h.PressVerticalKeyAsync(ctx, "Vk_WizardCancel");
                await h.ConfirmAsync(ctx);
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
            ctx.Check(h.IndexOfVerticalKey("Vk_ChangeWheel") == 0, "the wheel group's keys should be back");
            ctx.Note("done: " + h.DialogLineText);
        }, new StepOptions(ExpectedAlarms: machineWrites));

        await h.StepAsync("Wheel", "DataDressingAndHistory", async ctx =>
        {
            // 砂轮（最终稿 5.10）：砂轮数据、修整参数各配简图，每一格的说明行（对话行）写全；换砂轮记进记录。
            await h.GoToAsync(PageKey.Parameters, ctx, ParametersViewModel.WheelGroup);
            ctx.Check(page.IsWheelGroup, "the wheel group should be shown");
            ctx.Check(page.WheelRows.Count == 3 && page.DressRows.Count == 4, "wheel data and dressing rows should be listed");
            foreach (ParameterRowViewModel row in page.WheelRows.Concat(page.DressRows))
            {
                ctx.Check(row.HintText.Length > 0 && !row.HintText.Contains('!'), row.Key + " has an incomplete hint: " + row.HintText);
            }

            await h.PressVerticalKeyAsync(ctx, "Vk_RegisterWheel");
            ctx.Check(page.FocusedWheelKey == RollGrinder.Core.Calibration.CalibrationKeys.NewWheelDiameterMm, "'register new wheel' should point at the new-wheel diameter");

            page.FocusedWheelKey = RollGrinder.Core.Calibration.CalibrationKeys.DressInfeedRadiusMicrometer;
            await h.WaitUntilAsync(() => page.WheelHistory.Count > 0, TimeSpan.FromSeconds(5));
            ctx.Note(page.WheelHistory.Count + " history rows, max surface speed " + page.MaxSurfaceSpeedText);
            if (!offline)
            {
                ctx.Check(page.WheelHistory.Any(entry => entry.KindText == page.Localizer["WheelEvent_Change"]),
                    "the wheel change finished above should be in the history");
            }

            h.TryScreenshot("wheel-page");
        });

        await h.StepAsync("WheelChange", "CancelHalfway", async ctx =>
        {
            await h.PressVerticalKeyAsync(ctx, "Vk_ChangeWheel");
            page.NewWheelDiameterText = "880";
            await h.PressVerticalKeyAsync(ctx, "Vk_WizardNext");
            await h.PressVerticalKeyAsync(ctx, "Vk_WizardCancel");
            ctx.Check(h.HasPendingConfirmation, "giving up the wheel change should ask first");
            await h.ConfirmAsync(ctx);
            ctx.Check(page.ActiveSubViewKey is null, "cancel should close the wizard");
        }, new StepOptions(ExpectedAlarms: machineWrites));
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 手动动作页（最终稿 5.4、5.5）：测量臂、尾架、头架拨盘、托瓦、测量对中、辅助循环——每页的横键切过去，
/// 竖键上的动作一个一个按（要确认的：对话行问、按"✓ 确认"）；测点采集与归档、对中比对。
/// </summary>
internal sealed class ManualSuite : ISelfTestSuite
{
    public string Name => "Manual";

    public async Task RunAsync(SelfTestHarness h)
    {
        ManualViewModel page = h.Page<ManualViewModel>();
        await h.GoToAsync(PageKey.Manual, groupKey: ManualPageLayout.Pages[0].Key);

        await h.StepAsync("Page", "GroupsAndReadouts", ctx =>
        {
            ctx.Check(page.Groups.Count == ManualPageLayout.Pages.Count + 1, "every manual page plus 'auxiliary cycles' should be a group");
            ctx.Check(page.Groups.All(group => group.VerticalKeys.Count <= PageViewModelBase.VerticalKeyCount), "a page holds at most 8 vertical keys");
            ctx.Note(string.Join(", ", page.Groups.Select(group => group.Key + ":" + group.Axes.Count + " axes")));
            return Task.CompletedTask;
        }, StepOptions.Shot);

        foreach (ManualGroupViewModel group in page.Groups.ToList())
        {
            string label = group.Key == MachineAreaKeys.Cycles ? "Fn_AuxCycles" : "ManualPage_" + group.Key;
            await h.StepAsync("Groups", group.Key, async ctx =>
            {
                if (h.Shell.CurrentPage.Key != PageKey.Manual)
                {
                    await h.GoToAsync(PageKey.Manual, ctx, group.Key);
                }

                await h.PressKeyAsync(ctx, label);
                ctx.Check(page.SelectedGroup == group, "the horizontal key should switch to " + group.Key);
                ctx.Check(h.Shell.VerticalKeys.Take(group.VerticalKeys.Count)
                        .Zip(group.VerticalKeys, (shown, own) => own is null ? shown.IsPlaceholder : ReferenceEquals(shown, own))
                        .All(same => same),
                    "the vertical bar should show this page's actions in place");
                ctx.Note(string.Join(", ", group.Lamps.Select(l => l.Label + "=" + l.StateText)));
            }, StepOptions.Shot);

            if (group.Key == ManualPageLayout.MeasureAndCentringKey)
            {
                continue;
            }

            for (int index = 0; index < group.VerticalKeys.Count; index++)
            {
                FunctionKeyViewModel? key = group.VerticalKeys[index];
                if (key is null)
                {
                    continue;
                }

                int slot = index;
                await h.StepAsync("Actions_" + group.Key, Invariant($"V{slot + 1}_{key.Label}"), async ctx =>
                {
                    await h.GoToAsync(PageKey.Manual, ctx, group.Key);
                    await PressActionAsync(h, ctx, key);
                }, new StepOptions(Tolerant: true));
            }
        }

        await h.StepAsync("Status", "QuillLampFollowsTheCommand", async ctx =>
        {
            await h.GoToAsync(PageKey.Manual, ctx, "tailstock");
            ManualGroupViewModel tailstock = page.Groups.First(g => g.Key == "tailstock");
            StatusLampViewModel quill = tailstock.Lamps.First(l => l.LabelResourceKey == "Status_quill");
            FunctionKeyViewModel? extend = FindAction(h, tailstock, "quill.extend");
            if (extend is null || !extend.IsUsable)
            {
                ctx.Skip("quill.extend cannot be pressed now: " + extend?.ReasonText);
            }

            await PressActionAsync(h, ctx, extend!);
            await h.SettleAsync(600);
            if (quill.IsUnknown)
            {
                ctx.Skip("the quill status bit is not mapped");
            }

            ctx.Check(quill.IsOn, "after 'quill extend' the quill lamp should be on, is " + quill.StateText);
        }, StepOptions.Shot);

        await h.StepAsync("Measurement", "CaptureSaveClear", async ctx =>
        {
            await h.GoToAsync(PageKey.Manual, ctx, ManualPageLayout.MeasureAndCentringKey);
            ctx.Check(page.IsCentring, "the measure-and-centring page should be shown");
            await h.PressVerticalKeyAsync(ctx, "Measurement_ClearButton");
            if (h.HasPendingConfirmation)
            {
                await h.ConfirmAsync(ctx);
            }

            for (int i = 0; i < 3; i++)
            {
                await h.PressVerticalKeyAsync(ctx, "Measurement_CaptureButton");
            }

            ctx.Check(page.Points.Count == 3, Invariant($"3 captured points expected, got {page.Points.Count}"));
            h.TryScreenshot("manual-points");
            await h.PressVerticalKeyAsync(ctx, "Measurement_SaveButton");
            ctx.Note("save status=" + page.StatusResourceKey);
            await h.PressVerticalKeyAsync(ctx, "Measurement_ClearButton");
            ctx.Check(h.HasPendingConfirmation, "clearing the points should ask first");
            await h.ConfirmAsync(ctx);
            ctx.Check(page.Points.Count == 0, "clear should remove points");
        }, StepOptions.Expect("*"));

        await h.StepAsync("Centring", "HeadTailClear", async ctx =>
        {
            await h.PressVerticalKeyAsync(ctx, "Centring_CaptureHead");
            await h.PressVerticalKeyAsync(ctx, "Centring_CaptureTail");
            ctx.Note("difference=" + page.CentringDifferenceText + ", hint=" + page.AlignmentHintText);
            h.TryScreenshot("manual-centring");
            await h.PressVerticalKeyAsync(ctx, "Centring_ClearButton");
            await h.ConfirmAsync(ctx);
        }, new StepOptions(Tolerant: true));
    }

    /// <summary>按手动目录的动作键在某一页的竖键里找到它（标签可能带"…"或换成停止，按命令对象认）。</summary>
    internal static FunctionKeyViewModel? FindAction(SelfTestHarness h, ManualGroupViewModel group, string actionKey)
    {
        ManualCommandDescriptor descriptor = ManualCommandCatalog.All.Single(command => command.Key == actionKey);
        IStringLocalizer localizer = h.Services.GetRequiredService<IStringLocalizer>();
        return group.VerticalKeys.FirstOrDefault(key => key is not null
            && (key.LabelResourceKey == descriptor.ResourceKey || key.LabelArgument == localizer[descriptor.ResourceKey]));
    }

    /// <summary>
    /// 按一个动作竖键：按不了就跳过（缺映射、机床在忙、急停——原因在对话行）；问了一句就按"✓ 确认"。
    /// </summary>
    internal static async Task PressActionAsync(SelfTestHarness h, StepContext ctx, FunctionKeyViewModel key)
    {
        if (!key.IsUsable)
        {
            ctx.Skip("unavailable: " + key.ReasonText);
        }

        await h.PressAsync(key);
        if (h.HasPendingConfirmation)
        {
            ctx.Note("asked: " + h.PendingQuestion);
            await h.ConfirmAsync(ctx);
        }

        ctx.Check(!h.HasPendingConfirmation, "nothing should stay pending after confirming");
        ctx.Note("dialog: " + h.DialogLineText);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 手动磨削（最终稿 5.1）：给定与倍率先标红、问一句、确认才写，取消恢复原值；往复行程；位置图取点；
/// 竖键动作（砂轮启动、冷却液、带启动装置、定位 ▸、各轴归位……）。
/// </summary>
internal sealed class ManualGrindingSuite : ISelfTestSuite
{
    public string Name => "ManualGrinding";

    public async Task RunAsync(SelfTestHarness h)
    {
        ManualGrindingViewModel page = h.Page<ManualGrindingViewModel>();
        await h.GoToAsync(PageKey.ManualGrinding);

        await h.StepAsync("Page", "WindowsAndKeys", ctx =>
        {
            ctx.Check(page.Axes.Count > 0, "the position window should list axes");
            ctx.Check(page.Mechanism.Count > 0, "the mechanism window should list lamps");
            ctx.Check(h.Shell.VerticalKeys.Count == PageViewModelBase.VerticalKeyCount, "the vertical bar keeps 8 slots");
            ctx.Note("wheel " + page.WheelSurfaceText + ", headstock " + page.HeadstockSpeedText + ", carriage " + page.CarriageStateText);
            return Task.CompletedTask;
        }, StepOptions.Shot);

        await h.StepAsync("Setpoint", "OverrideCancelReverts", async ctx =>
        {
            SetpointViewModel feed = page.FeedOverride;
            string before = feed.Text;
            feed.StepDownCommand.Execute(null);
            await h.SettleAsync();
            if (!h.HasPendingConfirmation)
            {
                ctx.Skip("the override is not mapped: " + h.DialogLineText);
            }

            ctx.Check(feed.IsPending, "a changed override should be marked red until confirmed");
            await h.CancelConfirmationAsync();
            ctx.Check(!feed.IsPending && feed.Text == before, "cancel should restore the written value");
        });

        await h.StepAsync("Setpoint", "OverrideConfirmWrites", async ctx =>
        {
            SetpointViewModel feed = page.FeedOverride;
            feed.StepDownCommand.Execute(null);
            await h.SettleAsync();
            if (!h.HasPendingConfirmation)
            {
                ctx.Skip("the override is not mapped: " + h.DialogLineText);
            }

            string asked = feed.Text;
            await h.ConfirmAsync(ctx);
            ctx.Check(!feed.IsPending, "after confirming the value is no longer pending");
            ctx.Note("feed override " + asked + " written: " + h.DialogLineText);
            feed.StepUpCommand.Execute(null);
            await h.SettleAsync();
            if (h.HasPendingConfirmation)
            {
                await h.ConfirmAsync(ctx);
            }
        }, new StepOptions(Tolerant: true));

        await h.StepAsync("Stroke", "PickTargetAndStrokeValues", async ctx =>
        {
            page.PickPositionTarget(1234.56);
            await h.SettleAsync();
            ctx.Check(page.PositionTarget.Text == "1234.6", "picking on the strip should fill the target, got " + page.PositionTarget.Text);
            page.StrokeStart.Text = "100.0";
            page.StrokeEnd.Text = "1900.0";
            await h.SettleAsync();
            ctx.Check(!page.StrokeStart.IsPending && !page.StrokeEnd.IsPending, "stroke values do not write the machine, so they are never pending");
            ctx.Check(!h.HasPendingConfirmation, "stroke values do not ask");
        });

        for (int index = 0; index < page.VerticalKeys.Count; index++)
        {
            FunctionKeyViewModel key = page.VerticalKeys[index];
            if (key.IsPlaceholder || key.Kind == FunctionKeyKind.Navigation)
            {
                continue;
            }

            int slot = index;
            await h.StepAsync("Keys", Invariant($"V{slot + 1}_{key.Label}"), async ctx =>
            {
                await h.GoToAsync(PageKey.ManualGrinding, ctx);
                page.ResetVerticalMenu();
                FunctionKeyViewModel current = page.VerticalKeys[slot];
                if (current.LabelResourceKey == "MG_Position")
                {
                    await h.PressAsync(current);
                    ctx.Check(page.VerticalMenuTitle.Length > 0, "'position ▸' should open its sub menu");
                    h.TryScreenshot("manual-grinding-position");
                    ctx.Note(string.Join(" | ", page.VerticalKeys.Where(k => !k.IsPlaceholder).Select(k => k.Label + (k.IsUsable ? string.Empty : " (" + k.ReasonText + ")"))));
                    page.ResetVerticalMenu();
                    return;
                }

                await ManualSuite.PressActionAsync(h, ctx, current);
                if (current.LabelResourceKey is "Action_WheelStop" or "MG_CarriageStop")
                {
                    // 刚启动的砂轮 / 拖板停下来（停止不问）。
                    await h.PressAsync(current);
                }
            }, new StepOptions(Tolerant: true));
        }

        await h.StepAsync("Keys", "HelpTopicIsManualGrinding", async ctx =>
        {
            await h.RunAsync(h.Shell.ToggleHelpCommand);
            ctx.Check(h.Shell.Help.TopicTitle == h.Services.GetRequiredService<IStringLocalizer>()["Help_ManualGrinding_Title"],
                "help on this page should open the manual grinding topic");
            h.Shell.Help.Close();
            await h.SettleAsync();
        });
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>磨削记录（最终稿 5.11）：查询…、打开、12 项指标、竖键 1–4 选曲线、日 / 月汇总、磨前 / 磨后报表预览与打印、导出。</summary>
internal sealed class RecordsSuite : ISelfTestSuite
{
    public string Name => "Records";

    public async Task RunAsync(SelfTestHarness h)
    {
        RecordsViewModel page = h.Page<RecordsViewModel>();
        await h.GoToAsync(PageKey.Records);

        await h.StepAsync("Query", "LastThirtyDays", async ctx =>
        {
            await h.PressVerticalKeyAsync(ctx, "Vk_QueryAsk");
            ctx.Check(page.IsQueryOpen, "'query…' should open the query panel");
            ctx.Check(h.Shell.VerticalKeys[7].LabelResourceKey == "Vk_Query", "vertical key 8 should read 'query'");
            page.SetRangeCommand.Execute("30");
            h.TryScreenshot("records-query");
            await h.PressVerticalKeyAsync(ctx, "Vk_Query");
            await h.SettleAsync(300);
            ctx.Check(!page.IsQueryOpen, "querying should close the panel");
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
            foreach (string key in new[] { "Curve_BeforeAfter", "Curve_Error", "Curve_Roundness", "Curve_Convergence" })
            {
                await h.PressVerticalKeyAsync(ctx, key);
                await h.SettleAsync(200);
                ctx.Check(h.Shell.VerticalKeys[h.IndexOfVerticalKey(key)].IsActive, key + " should be marked as shown");
                ctx.Note(key + (page.CurveHasData ? "=data" : "=empty") + " · " + page.CurveTitle);
                h.TryScreenshot("records-" + key);
            }
        });

        await h.StepAsync("Summary", "DailyAndMonthly", async ctx =>
        {
            await h.PressKeyAsync(ctx, "Fn_DailyReport");
            ctx.Note("daily: " + page.SummaryLineText);
            await h.PressKeyAsync(ctx, "Fn_MonthlyReport");
            ctx.Note("monthly: " + page.SummaryLineText);
            ctx.Check(page.SummaryLineText.Length > 0, "summary should produce text");
        });

        foreach ((string key, bool vertical, string name) in new[] { ("Fn_PreGrindReport", false, "PreGrind"), ("Vk_Print", true, "PostGrind") })
        {
            await h.StepAsync("Report", name + "PreviewAndPrint", async ctx =>
            {
                if (!hasRecords)
                {
                    ctx.Skip("no record to report on");
                }

                await h.RecoverAsync();
                if (vertical)
                {
                    await h.PressVerticalKeyAsync(ctx, key);
                }
                else
                {
                    await h.PressKeyAsync(ctx, key);
                }

                ctx.Check(page.Report is not null, "a report should be composed");
                ctx.Check(page.ActiveSubViewKey == RecordsViewModel.ReportSubView, "report preview should open as a sub view");
                h.TryScreenshot("records-report-" + name);
                int before = h.Interaction.Produced.Count;
                await h.PressVerticalKeyAsync(ctx, "Vk_PrintNow");
                ctx.Check(h.Interaction.Produced.Count == before + 1, "printing should produce one document");
                ctx.Note("printed " + Path.GetFileName(h.Interaction.LastProduced));
                await h.BackAsync();
            });
        }

        await h.StepAsync("Export", "Csv", async ctx =>
        {
            await h.RecoverAsync();
            await h.PressVerticalKeyAsync(ctx, "Vk_ExportExcel");
            string? file = h.Interaction.LastProduced;
            ctx.Check(file is not null && File.Exists(file) && file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), "a CSV export should be written");
            ctx.Note(Invariant($"{Path.GetFileName(file)} lines={File.ReadAllLines(file!).Length}"));
        });
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 库（最终稿 5.9）：辊形库、程序库、作业、轧辊台账、U 盘。条目的新建、打开、复制、重命名、删除（问一句），
/// 导出到 U 盘再导入（撞名加"(2)"），台账登记与编辑、重号拒绝。
/// </summary>
internal sealed class LibrarySuite : ISelfTestSuite
{
    public string Name => "Library";

    public async Task RunAsync(SelfTestHarness h)
    {
        LibraryViewModel page = h.Page<LibraryViewModel>();

        foreach (string group in new[] { LibraryViewModel.ProfilesGroup, LibraryViewModel.ProgramsGroup, LibraryViewModel.JobsGroup })
        {
            await h.StepAsync("Groups", group, async ctx =>
            {
                await h.GoToAsync(PageKey.Library, ctx, group);
                ctx.Check(page.Group == group && page.IsListGroup, "the group key should show the " + group + " list");
                await h.WaitUntilAsync(() => !page.IsBusy, TimeSpan.FromSeconds(5));
                if (page.Entries.Count > 0)
                {
                    page.SelectedEntry = page.Entries[0];
                    await h.SettleAsync(200);
                    ctx.Note(Invariant($"{page.Entries.Count} entries, preview rows {page.PreviewRows.Count}, curve points {page.PreviewCurve.Count}"));
                }

                h.TryScreenshot("library-" + group);
            }, StepOptions.Shot);
        }

        await h.StepAsync("Entries", "CopyRenameDelete", async ctx =>
        {
            if (!await SelfTestNames.SelectAsync(h, LibraryViewModel.ProfilesGroup, SelfTestNames.ProfileA))
            {
                ctx.Skip("profile A was not saved by the profile suite");
            }

            await h.PressVerticalKeyAsync(ctx, "Vk_Copy");
            ctx.Check(page.NamePrompt.IsOpen, "copy should ask for the new name");
            page.NamePrompt.Name = SelfTestNames.ProfileB;
            await h.RunAsync(page.NamePrompt.ConfirmCommand);
            if (page.NamePrompt.IsOpen && page.NamePrompt.CanOverwrite)
            {
                await h.RunAsync(page.NamePrompt.OverwriteCommand);
            }

            ctx.Check(await h.WaitUntilAsync(() => page.Entries.Any(e => e.Name == SelfTestNames.ProfileB), TimeSpan.FromSeconds(5)),
                "the copy should be listed");

            page.SelectedEntry = page.Entries.First(e => e.Name == SelfTestNames.ProfileB);
            await h.PressVerticalKeyAsync(ctx, "Vk_Rename");
            page.NamePrompt.Name = SelfTestNames.ProfileB + " R";
            await h.RunAsync(page.NamePrompt.ConfirmCommand);
            ctx.Check(await h.WaitUntilAsync(() => page.Entries.Any(e => e.Name == SelfTestNames.ProfileB + " R"), TimeSpan.FromSeconds(5)),
                "the renamed entry should be listed");

            page.SelectedEntry = page.Entries.First(e => e.Name == SelfTestNames.ProfileB + " R");
            await h.PressVerticalKeyAsync(ctx, "Vk_Delete");
            ctx.Check(h.HasPendingConfirmation, "delete should ask first");
            await h.ConfirmAsync(ctx);
            ctx.Check(await h.WaitUntilAsync(() => page.Entries.All(e => e.Name != SelfTestNames.ProfileB + " R"), TimeSpan.FromSeconds(5)),
                "the deleted entry should disappear");
            ctx.Check(page.Entries.Any(e => e.Name == SelfTestNames.ProfileA), "the other profile must stay");
        });

        await h.StepAsync("Usb", "ExportAndImportRenamesClashes", async ctx =>
        {
            if (!await SelfTestNames.SelectAsync(h, LibraryViewModel.ProfilesGroup, SelfTestNames.ProfileA))
            {
                ctx.Skip("profile A was not saved by the profile suite");
            }

            int produced = h.Interaction.Produced.Count;
            await h.PressVerticalKeyAsync(ctx, "Vk_ExportUsb");
            ctx.Check(await h.WaitUntilAsync(() => h.Interaction.Produced.Count > produced, TimeSpan.FromSeconds(5)), "an exchange file should be written");
            string file = h.Interaction.LastProduced!;
            ctx.Check(File.Exists(file) && file.EndsWith(RollGrinder.Data.LibraryExchangeFile.Extension, StringComparison.OrdinalIgnoreCase),
                "the exchange file should carry the " + RollGrinder.Data.LibraryExchangeFile.Extension + " extension");

            int before = page.Entries.Count;
            h.Interaction.OpenAnswers.Enqueue(file);
            await h.PressVerticalKeyAsync(ctx, "Vk_ImportUsb");
            ctx.Check(await h.WaitUntilAsync(() => page.Entries.Count == before + 1, TimeSpan.FromSeconds(5)), "the imported profile should be added");
            LibraryEntryViewModel? imported = page.Entries.FirstOrDefault(e => e.Name == SelfTestNames.ProfileA + " (2)");
            ctx.Check(imported is not null, "a clashing name should get a '(2)' suffix");
            page.SelectedEntry = imported;
            await h.PressVerticalKeyAsync(ctx, "Vk_Delete");
            await h.ConfirmAsync(ctx);
        }, StepOptions.Expect("*"));

        await h.StepAsync("Ledger", "RegisterEditAndRefuseDuplicate", async ctx =>
        {
            await h.GoToAsync(PageKey.Library, ctx, LibraryViewModel.LedgerGroup);
            await h.PressVerticalKeyAsync(ctx, "Vk_NewRoll");
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
            await h.PressVerticalKeyAsync(ctx, "Vk_SaveRoll");
            ctx.Check(page.LedgerProblems.Count == 0, "a valid roll should be saved: " + string.Join(" | ", page.LedgerProblems));
            ctx.Check(page.SelectedLedgerRow?.RollId == SelfTestNames.LedgerRollId && !page.IsNewLedgerRoll,
                "the saved roll should be selected for editing");
            h.TryScreenshot("library-ledger-edit");

            page.LedgerCurrentDiameterText = "1180";
            await h.PressVerticalKeyAsync(ctx, "Vk_SaveRoll");
            ctx.Check(page.LedgerProblems.Count == 0 && page.SelectedLedgerRow?.CurrentDiameterText.StartsWith("1180", StringComparison.Ordinal) == true,
                "editing an existing roll should be saved");

            await h.PressVerticalKeyAsync(ctx, "Vk_NewRoll");
            page.LedgerRollId = SelfTestNames.LedgerRollId;
            page.LedgerBodyLengthText = "2000";
            page.LedgerDiameterText = "650";
            await h.PressVerticalKeyAsync(ctx, "Vk_SaveRoll");
            ctx.Check(page.LedgerProblems.Count == 1, "a duplicate roll number must be refused with its reason");
        });
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>诊断（最终稿 5.12）：七个横键组都渲染；报警详情与消除方法；清除上位机报警…（问一句）；改动记录；运行日志；导出快照；整机备份。</summary>
internal sealed class DiagnosticsSuite : ISelfTestSuite
{
    public string Name => "Diagnostics";

    public async Task RunAsync(SelfTestHarness h)
    {
        DiagnosticsViewModel page = h.Page<DiagnosticsViewModel>();

        await h.StepAsync("Connection", "ConnectionAndTagMap", async ctx =>
        {
            await h.GoToAsync(PageKey.Diagnostics, ctx, DiagnosticsViewModel.ConnectionGroup);
            ctx.Check(page.ConnectionRows.Count > 0, "connection rows should be listed");
            ctx.Note("tagmap: " + page.TagMapCheckText + Invariant($", missing={page.MissingTags.Count}, degradation={page.DegradationLevel}"));
            ctx.Check(page.TagMapIsValid, "the sample tagmap should be complete: missing " + string.Join(", ", page.MissingTags.Take(5)));
        }, StepOptions.Shot);

        await h.StepAsync("Alarms", "DetailAndClearAsks", async ctx =>
        {
            h.Services.GetRequiredService<IAlarmSink>().Raise(AlarmSeverity.Warning, "Alarm_ValueOutOfRange", "self-test marker");
            await h.GoToAsync(PageKey.Diagnostics, ctx, DiagnosticsViewModel.AlarmsGroup);
            await h.SettleAsync(300);
            ctx.Check(page.Events.Count > 0 && page.SelectedAlarm is not null, "the alarm list should show the marker, selected");
            ctx.Check(page.AlarmResetText.Length > 0, "the detail should say how to clear it");
            h.TryScreenshot("diag-alarms");
            await h.PressVerticalKeyAsync(ctx, "Vk_ClearHmiAlarms");
            ctx.Check(h.HasPendingConfirmation, "clearing HMI alarms should ask first");
            await h.ConfirmAsync(ctx);
            ctx.Check(h.Services.GetRequiredService<IAlarmLog>().Snapshot().Count == 0, "confirming should clear the alarm list");
        }, StepOptions.Expect("*"));

        foreach (string group in new[] { DiagnosticsViewModel.TagMonitorGroup, DiagnosticsViewModel.AuditGroup, DiagnosticsViewModel.RunLogGroup })
        {
            await h.StepAsync("Groups", group, async ctx =>
            {
                await h.GoToAsync(PageKey.Diagnostics, ctx, group);
                await h.SettleAsync(400);
                ctx.Check(page.Group == group, "the group should switch to " + group);
                ctx.Note(Invariant($"monitorRows={page.TagMonitorRows.Count}, changeLog={page.ChangeLogRows.Count}, logChars={page.InspectorText.Length}"));
                h.TryScreenshot("diag-" + group);
            });
        }

        await h.StepAsync("Export", "Snapshot", async ctx =>
        {
            await h.GoToAsync(PageKey.Diagnostics, ctx, DiagnosticsViewModel.AlarmsGroup);
            await h.PressVerticalKeyAsync(ctx, "Vk_ExportSnapshot");
            string? file = h.Interaction.LastProduced;
            ctx.Check(file is not null && File.Exists(file) && new FileInfo(file).Length > 0, "a snapshot file should be written");
            ctx.Note(Invariant($"{Path.GetFileName(file)} {new FileInfo(file!).Length} bytes"));
        });

        await h.StepAsync("Export", "Backup", async ctx =>
        {
            await h.GoToAsync(PageKey.Diagnostics, ctx, DiagnosticsViewModel.BackupGroup);
            await h.PressVerticalKeyAsync(ctx, "Vk_Backup", TimeSpan.FromSeconds(60));
            await h.WaitUntilAsync(() => h.Interaction.LastProduced?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true, TimeSpan.FromSeconds(60));
            string? file = h.Interaction.LastProduced;
            ctx.Check(file is not null && File.Exists(file), "a backup zip should be written");
            using ZipArchive zip = ZipFile.OpenRead(file!);
            ctx.Check(zip.Entries.Count > 0, "backup should not be empty");
            ctx.Note(Invariant($"{Path.GetFileName(file)} entries={zip.Entries.Count}: ") + string.Join(", ", zip.Entries.Take(8).Select(e => e.FullName)));
        }, new StepOptions(TimeoutSeconds: 90));
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 调试（最终稿 5.13，制造商）：机床配置改错就地标红、存不进去；改对了"✓ 保存…"问一句，原文件先备份、写改动记录；
/// "恢复上一版…"再存回去。标签映射搜索、只看缺失、试读。系统组只读。
/// </summary>
internal sealed class CommissioningSuite : ISelfTestSuite
{
    public string Name => "Commissioning";

    public async Task RunAsync(SelfTestHarness h)
    {
        CommissioningViewModel page = h.Page<CommissioningViewModel>();

        await h.StepAsync("ConfigEditor", "MachineConfigEditSaveRestore", async ctx =>
        {
            await h.GoToAsync(PageKey.Commissioning, ctx, CommissioningViewModel.MachineConfigGroup);
            if (h.Shell.CurrentPage.Key != PageKey.Commissioning)
            {
                ctx.Skip("commissioning is not reachable for this user");
            }

            await h.WaitUntilAsync(() => page.MachineGroups.Count > 0, TimeSpan.FromSeconds(5));
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
            ctx.Check(h.Shell.VerticalKeys[7].LabelResourceKey == "Vk_SaveAsk", "vertical key 8 should become 'save…' while dirty");
            int backupsBefore = h.Services.GetRequiredService<ConfigDocumentStore>().ListBackups(ConfigFileKind.Machine).Count;
            await h.PressVerticalKeyAsync(ctx, "Vk_SaveAsk");
            await h.ConfirmAsync(ctx);
            ctx.Check(!page.IsDirty, "saving should clear the unsaved mark: " + h.DialogLineText);
            ctx.Check(h.Services.GetRequiredService<ConfigDocumentStore>().ListBackups(ConfigFileKind.Machine).Count == backupsBefore + 1,
                "the old file should have been backed up");
            ctx.Note(h.DialogLineText);

            await h.PressVerticalKeyAsync(ctx, "Vk_RestorePrevious");
            await h.ConfirmAsync(ctx);
            ConfigFieldViewModel restored = page.MachineGroups.SelectMany(g => g.Fields).First(f => f.Path == "workpiece.maxWeightKg");
            ctx.Check(restored.Text == original, "the previous version should be loaded into the editor, weight is " + restored.Text);
            await h.PressVerticalKeyAsync(ctx, "Vk_SaveAsk");
            await h.ConfirmAsync(ctx);
            ctx.Check(!page.IsDirty, "the restored version should be saved back");
            h.TryScreenshot("config-machine");
        }, StepOptions.Expect("*"));

        await h.StepAsync("ConfigEditor", "TagMapSearchMissingTestRead", async ctx =>
        {
            await h.GoToAsync(PageKey.Commissioning, ctx, CommissioningViewModel.TagMappingGroup);
            if (h.Shell.CurrentPage.Key != PageKey.Commissioning)
            {
                ctx.Skip("commissioning is not reachable for this user");
            }

            await h.WaitUntilAsync(() => page.VisibleTags.Count > 0, TimeSpan.FromSeconds(5));
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
            ctx.Note("test read: " + h.DialogLineText);
            ctx.Check(h.DialogLineText.Length > 0, "the test read should say what came back");
            h.TryScreenshot("config-tagmap");
        }, StepOptions.Expect("*"));

        await h.StepAsync("System", "ReadOnlyRowsAndRawView", async ctx =>
        {
            await h.GoToAsync(PageKey.Commissioning, ctx, CommissioningViewModel.SystemGroup);
            if (h.Shell.CurrentPage.Key != PageKey.Commissioning)
            {
                ctx.Skip("commissioning is not reachable for this user");
            }

            ctx.Check(page.SystemRows.Count >= 5, "the system group should list language, refresh, quick bar …");
            await h.PressVerticalKeyAsync(ctx, "Vk_ViewMachineJson");
            ctx.Check(await h.WaitUntilAsync(() => page.InspectorText.Length > 0, TimeSpan.FromSeconds(5)), "the raw machine.json should be shown");
            h.TryScreenshot("commissioning-system");
        });
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
