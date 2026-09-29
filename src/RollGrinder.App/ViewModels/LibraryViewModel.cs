using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Records;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>库里的一行（辊形、程序或作业）。</summary>
/// <param name="Id">标识。</param>
/// <param name="Name">名字（辊形名、程序名、作业号）。</param>
/// <param name="Detail">一句话概况（段数与设计长度、工序数、辊号）。</param>
/// <param name="ModifiedText">最后修改 / 开始时刻。</param>
public sealed record LibraryEntryViewModel(string Id, string Name, string Detail, string ModifiedText);

/// <summary>
/// 库（界面最终稿 5.9）：辊形库、程序库、作业、轧辊台账、U 盘集中在一个区域，找东西只有一个地方（C5）；
/// 选中一行右边是预览——辊形画曲线，程序列工序，台账列磨削履历。长按或"打开"到编辑区。
///
/// 横键：辊形 · 工艺程序 · 作业 · 轧辊台账 · 空 · U 盘。
/// 竖键：用于作业 · 新建 · 打开 · 复制… · 重命名… · 删除… · 导出到 U 盘 · 从 U 盘导入（台账组换成它自己的）。
/// </summary>
public sealed partial class LibraryViewModel : PageViewModelBase
{
    public const string ProfilesGroup = "profiles";
    public const string ProgramsGroup = "programs";
    public const string JobsGroup = "jobs";
    public const string LedgerGroup = "ledger";
    public const string UsbGroup = "usb";

    private const int ListLimit = 500;

    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IRecordService recordService;
    private readonly IRollLedgerService ledgerService;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly HmiSettings settings;
    private readonly JobDraft jobDraft;
    private readonly Dictionary<string, FunctionKeyViewModel> groupKeys = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<FunctionKeyViewModel?> entryKeys;
    private readonly IReadOnlyList<FunctionKeyViewModel?> ledgerKeys;
    private readonly IReadOnlyList<FunctionKeyViewModel?> jobKeys;
    private readonly IReadOnlyList<FunctionKeyViewModel?> usbKeys;

    public LibraryViewModel(
        IRollProfileRepository profiles,
        IProgramRepository programs,
        IRecordService recordService,
        IRollLedgerService ledgerService,
        RollProfileTypeRegistry profileTypes,
        HmiSettings settings,
        JobDraft jobDraft,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
        this.ledgerService = ledgerService ?? throw new ArgumentNullException(nameof(ledgerService));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.jobDraft = jobDraft ?? throw new ArgumentNullException(nameof(jobDraft));
        NamePrompt = new NamePromptViewModel(localizer);

        foreach ((string group, string label) in new[]
        {
            (ProfilesGroup, "Lib_Profiles"), (ProgramsGroup, "Lib_Programs"), (JobsGroup, "Lib_Jobs"), (LedgerGroup, "Lib_Ledger"),
            (UsbGroup, "Lib_Usb"),
        })
        {
            string target = group;
            this.groupKeys[group] = FunctionKeyViewModel.ForAction(label, localizer, () => ShowGroup(target));
        }

        SetFunctionKeys(new FunctionKeyViewModel?[]
        {
            this.groupKeys[ProfilesGroup],
            this.groupKeys[ProgramsGroup],
            this.groupKeys[JobsGroup],
            this.groupKeys[LedgerGroup],
            null,
            this.groupKeys[UsbGroup],
        });

        var hasSelection = new Func<bool>(() => SelectedEntry is not null);
        this.entryKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_UseForJob", new RelayCommand(UseForJob, hasSelection), localizer) { PreconditionResourceKey = "Lib_NothingSelected" },
            new FunctionKeyViewModel("Vk_New", new RelayCommand(CreateNew), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_Open", new RelayCommand(OpenSelected, hasSelection), localizer) { PreconditionResourceKey = "Lib_NothingSelected" },
            new FunctionKeyViewModel("Vk_Copy", new RelayCommand(AskCopy, hasSelection), localizer, requiresEditable: true) { PreconditionResourceKey = "Lib_NothingSelected" },
            new FunctionKeyViewModel("Vk_Rename", new RelayCommand(AskRename, hasSelection), localizer, requiresEditable: true) { PreconditionResourceKey = "Lib_NothingSelected" },
            new FunctionKeyViewModel("Vk_Delete", new RelayCommand(AskDelete, hasSelection), localizer, requiresEditable: true) { PreconditionResourceKey = "Lib_NothingSelected" },
            new FunctionKeyViewModel("Vk_ExportUsb", new AsyncRelayCommand(ExportSelectedAsync, hasSelection), localizer) { PreconditionResourceKey = "Lib_NothingSelected" },
            new FunctionKeyViewModel("Vk_ImportUsb", new AsyncRelayCommand(ImportAsync), localizer, requiresEditable: true),
        };
        this.jobKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_UseForJob", new RelayCommand(UseForJob, hasSelection), localizer) { PreconditionResourceKey = "Lib_NothingSelected" },
            new FunctionKeyViewModel("Vk_NewJob", new RelayCommand(() => Navigator.GoTo(PageKey.Job, MachineAreaKeys.Job)), localizer),
            new FunctionKeyViewModel("Vk_OpenRecord", new RelayCommand(OpenSelected, hasSelection), localizer) { PreconditionResourceKey = "Lib_NothingSelected" },
        };
        this.ledgerKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_UseForJob", new RelayCommand(UseLedgerRollForJob, () => SelectedLedgerRollId is not null), localizer)
            {
                PreconditionResourceKey = "Lib_NothingSelected",
            },
            new FunctionKeyViewModel("Vk_NewRoll", NewLedgerRollCommand, localizer) { RequiredPermission = Permission.EditJobs },
            new FunctionKeyViewModel("Vk_SaveRoll", SaveLedgerRollCommand, localizer) { RequiredPermission = Permission.EditJobs },
        };
        this.usbKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_ExportAll", new AsyncRelayCommand(ExportAllAsync), localizer),
            new FunctionKeyViewModel("Vk_ImportUsb", new AsyncRelayCommand(ImportAsync), localizer, requiresEditable: true),
        };

        ShowGroup(ProfilesGroup);
        this.loadOnShow = true;
    }

    public override PageKey Key => PageKey.Library;

    public override string TitleResourceKey => "Page_Library";

    /// <summary>离线可用：只和数据库、U 盘打交道。</summary>
    public override bool WorksOffline => true;

    /// <summary>库里的增删改归管理员（辊形、程序；Q9）。台账键自己点名 EditJobs。</summary>
    public override Permission? EditPermission => Permission.EditProfiles;

    /// <summary>当前是哪一组。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListGroup), nameof(IsLedgerGroup), nameof(IsUsbGroup), nameof(GroupTitle))]
    private string group = ProfilesGroup;

    public bool IsListGroup => Group is ProfilesGroup or ProgramsGroup or JobsGroup;

    public bool IsLedgerGroup => Group == LedgerGroup;

    public bool IsUsbGroup => Group == UsbGroup;

    /// <summary>当前窗口标题：辊形库 · 23 条。</summary>
    public string GroupTitle => Localizer.Format("Lib_TitleFormat", Localizer["Lib_" + char.ToUpperInvariant(Group[0]) + Group[1..]], Entries.Count);

    /// <summary>当前组的条目（辊形、程序、作业）。</summary>
    public ObservableCollection<LibraryEntryViewModel> Entries { get; } = new();

    [ObservableProperty]
    private LibraryEntryViewModel? selectedEntry;

    /// <summary>预览里的要点（程序的工序序列、作业的辊形 / 程序）。</summary>
    public ObservableCollection<LabelValueViewModel> PreviewRows { get; } = new();

    /// <summary>辊形预览：沿辊身的直径量 µm。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> PreviewCurve { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>预览曲线变了。</summary>
    public event EventHandler? PreviewChanged;

    /// <summary>轧辊台账的行。</summary>
    public ObservableCollection<RollLedgerRowViewModel> Ledger { get; } = new();

    /// <summary>复制、重命名用的命名框。</summary>
    public NamePromptViewModel NamePrompt { get; }

    public override bool HasModalPrompt => NamePrompt.IsOpen;

    public override bool TryDismissPrompt()
    {
        if (!NamePrompt.IsOpen)
        {
            return false;
        }

        NamePrompt.CancelCommand.Execute(null);
        return true;
    }

    /// <summary>长按一行 = 打开（最终稿 F8）。</summary>
    [RelayCommand]
    private void OpenEntry(LibraryEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        SelectedEntry = entry;
        OpenSelected();
    }

    public override void OnActivated()
    {
        if (this.jobDraft.RegisterNewRollRequested)
        {
            // 作业向导派来登记一支新辊：直接开台账、给一张新表；存好后"« 返回"回作业向导就选上它。
            this.jobDraft.RegisterNewRollRequested = false;
            this.registeringForJob = true;
            ShowGroup(LedgerGroup);
            _ = RunGuardedAsync(
                async token =>
                {
                    await ReloadLedgerAsync(null, token).ConfigureAwait(true);
                    NewLedgerRoll();
                },
                CancellationToken.None);
            return;
        }

        _ = RunGuardedAsync(ReloadAsync, CancellationToken.None);
    }

    /// <summary>构造时只摆好键、不读库 / 文件（那时数据库和配置可能还没就绪）；读在切到本页时做。</summary>
    private readonly bool loadOnShow;

    public override bool ShowGroup(string groupKey)
    {
        if (!this.groupKeys.ContainsKey(groupKey))
        {
            return false;
        }

        Group = groupKey;
        MarkActiveFunctionKey(this.groupKeys[groupKey]);
        SetVerticalKeys(groupKey switch
        {
            LedgerGroup => this.ledgerKeys,
            JobsGroup => this.jobKeys,
            UsbGroup => this.usbKeys,
            _ => this.entryKeys,
        });
        if (this.loadOnShow)
        {
            _ = RunGuardedAsync(ReloadAsync, CancellationToken.None);
        }

        return true;
    }

    partial void OnSelectedEntryChanged(LibraryEntryViewModel? value)
    {
        foreach (FunctionKeyViewModel? key in this.entryKeys.Concat(this.jobKeys))
        {
            (key?.Command as IRelayCommand)?.NotifyCanExecuteChanged();
        }

        _ = RunGuardedAsync(LoadPreviewAsync, CancellationToken.None);
    }

    partial void OnSelectedLedgerRowChanging(RollLedgerRowViewModel? value) =>
        (this.ledgerKeys[0]?.Command as IRelayCommand)?.NotifyCanExecuteChanged();

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        string? keep = SelectedEntry?.Id;
        Entries.Clear();
        switch (Group)
        {
            case ProfilesGroup:
                foreach (RollProfileSummary entry in await this.profiles.ListAsync(ListLimit, cancellationToken).ConfigureAwait(true))
                {
                    Entries.Add(new LibraryEntryViewModel(
                        entry.ProfileId,
                        entry.Name,
                        Localizer.Format("Lib_ProfileDetailFormat", entry.SegmentCount, entry.BodyLengthMm.ToString("F0", CultureInfo.InvariantCulture)),
                        Time(entry.ModifiedAtUtc)));
                }

                break;

            case ProgramsGroup:
                foreach (ProgramSummary entry in await this.programs.ListAsync(ListLimit, cancellationToken).ConfigureAwait(true))
                {
                    Entries.Add(new LibraryEntryViewModel(
                        entry.ProgramId, entry.Name, Localizer.Format("Lib_ProgramDetailFormat", entry.StepCount), Time(entry.ModifiedAtUtc)));
                }

                break;

            case JobsGroup:
                foreach (GrindingRecordView record in await this.recordService
                    .QueryAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), ListLimit, cancellationToken).ConfigureAwait(true))
                {
                    Entries.Add(new LibraryEntryViewModel(
                        record.JobId, record.JobId, record.RollCode + " · " + Localizer["JobState_" + record.State], Time(record.StartedAtUtc)));
                }

                break;

            case LedgerGroup:
                await ReloadLedgerAsync(SelectedLedgerRow?.RollId, cancellationToken).ConfigureAwait(true);
                break;

            default:
                break;
        }

        OnPropertyChanged(nameof(GroupTitle));
        SelectedEntry = Entries.FirstOrDefault(entry => entry.Id == keep) ?? Entries.FirstOrDefault();
    }

    private async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        PreviewRows.Clear();
        PreviewCurve = Array.Empty<(double, double)>();
        LibraryEntryViewModel? entry = SelectedEntry;
        if (entry is not null && Group == ProfilesGroup
            && await this.profiles.GetAsync(entry.Id, cancellationToken).ConfigureAwait(true) is { } profile)
        {
            RollGeometry geometry = RollGeometry.FromDiameter(profile.BodyLengthMm, 1000.0);
            PreviewCurve = profile.Profile.Compose(geometry, this.profileTypes, this.settings.ProfileSampleCount).Points
                .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
                .ToArray();
            PreviewRows.Add(new LabelValueViewModel("Lib_PreviewBodyLength", profile.BodyLengthMm.ToString("F0", CultureInfo.InvariantCulture), Localizer));
            PreviewRows.Add(new LabelValueViewModel("Lib_PreviewSegments", profile.SegmentCount.ToString(CultureInfo.InvariantCulture), Localizer));
        }
        else if (entry is not null && Group == ProgramsGroup
            && await this.programs.GetAsync(entry.Id, cancellationToken).ConfigureAwait(true) is { } program)
        {
            foreach (GrindingJobStep step in program.Steps)
            {
                PreviewRows.Add(new LabelValueViewModel(
                    "Lib_PreviewStepFormat", step.Order.ToString("00", CultureInfo.InvariantCulture) + "  " + Localizer["StepType_" + step.StepTypeKey], Localizer));
            }
        }
        else if (entry is not null && Group == JobsGroup)
        {
            PreviewRows.Add(new LabelValueViewModel("Lib_PreviewJob", entry.Detail, Localizer));
        }

        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>用于作业：辊形、程序带进作业向导（台账组另有）。</summary>
    private void UseForJob()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        switch (Group)
        {
            case ProfilesGroup:
                this.jobDraft.PendingProfileId = entry.Id;
                break;
            case ProgramsGroup:
                this.jobDraft.PendingProgramId = entry.Id;
                break;
            default:
                break;
        }

        Navigator.StartTask(PageKey.Job, PageKey.Library);
    }

    private void UseLedgerRollForJob()
    {
        if (SelectedLedgerRollId is { } rollId)
        {
            this.jobDraft.RegisteredRollId = rollId;
            Navigator.StartTask(PageKey.Job, PageKey.Library);
        }
    }

    /// <summary>新建：到辊形区 / 工艺区开一张空的。</summary>
    private void CreateNew()
    {
        if (Group == ProfilesGroup)
        {
            this.jobDraft.ProfileToOpen = JobDraft.NewEntry;
            Navigator.GoTo(PageKey.Profile);
        }
        else if (Group == ProgramsGroup)
        {
            this.jobDraft.ProgramToOpen = JobDraft.NewEntry;
            Navigator.GoTo(PageKey.Steps);
        }
    }

    /// <summary>打开：到辊形区 / 工艺区编辑；作业打开到磨削记录。</summary>
    private void OpenSelected()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        switch (Group)
        {
            case ProfilesGroup:
                this.jobDraft.ProfileToOpen = entry.Id;
                Navigator.GoTo(PageKey.Profile);
                break;
            case ProgramsGroup:
                this.jobDraft.ProgramToOpen = entry.Id;
                Navigator.GoTo(PageKey.Steps);
                break;
            case JobsGroup:
                this.jobDraft.RecordsJobId = entry.Id;
                Navigator.GoTo(PageKey.Records);
                break;
            default:
                break;
        }
    }

    private void AskCopy()
    {
        if (SelectedEntry is { } entry)
        {
            NamePrompt.Open(
                Localizer["Lib_CopyTitle"],
                Localizer.Format("Library_CopyNameFormat", entry.Name),
                (name, overwrite, token) => StoreCopyAsync(entry, name, rename: false, token));
        }
    }

    private void AskRename()
    {
        if (SelectedEntry is { } entry)
        {
            NamePrompt.Open(
                Localizer["Lib_RenameTitle"],
                entry.Name,
                (name, overwrite, token) => StoreCopyAsync(entry, name, rename: true, token));
        }
    }

    /// <summary>复制或改名。名字在库里唯一（与"另存为"同一条规矩）；改名保留标识，作业里引用的快照不受影响。</summary>
    private async Task<NamePromptOutcome> StoreCopyAsync(LibraryEntryViewModel entry, string name, bool rename, CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (Group == ProfilesGroup && await this.profiles.GetAsync(entry.Id, cancellationToken).ConfigureAwait(true) is { } profile)
            {
                string id = rename ? profile.ProfileId : string.Create(CultureInfo.InvariantCulture, $"P{DateTimeOffset.Now:yyyyMMddHHmmssfff}");
                if (await this.profiles.FindIdByNameAsync(name, id, cancellationToken).ConfigureAwait(true) is not null)
                {
                    return NamePromptOutcome.Refused(Localizer.Format("Library_NameTakenFormat", name));
                }

                await this.profiles.SaveAsync(
                    profile with { ProfileId = id, Name = name, ModifiedAtUtc = now, CreatedAtUtc = rename ? profile.CreatedAtUtc : now },
                    cancellationToken).ConfigureAwait(true);
            }
            else if (Group == ProgramsGroup && await this.programs.GetAsync(entry.Id, cancellationToken).ConfigureAwait(true) is { } program)
            {
                string id = rename ? program.ProgramId : string.Create(CultureInfo.InvariantCulture, $"G{DateTimeOffset.Now:yyyyMMddHHmmssfff}");
                if (await this.programs.FindIdByNameAsync(name, id, cancellationToken).ConfigureAwait(true) is not null)
                {
                    return NamePromptOutcome.Refused(Localizer.Format("Library_NameTakenFormat", name));
                }

                await this.programs.SaveAsync(
                    program with { ProgramId = id, Name = name, ModifiedAtUtc = now, CreatedAtUtc = rename ? program.CreatedAtUtc : now },
                    cancellationToken).ConfigureAwait(true);
            }

            Say(rename ? "Lib_Renamed" : "Lib_Copied", name);
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
            return NamePromptOutcome.Done;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
            return NamePromptOutcome.Refused(Localizer["Library_SaveFailed"]);
        }
    }

    /// <summary>删除…：问一句。已经用过它的作业不受影响——作业存的是快照。</summary>
    private void AskDelete()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        Ask(
            "Lib_AskDelete",
            async () =>
            {
                await RunGuardedAsync(
                    async token =>
                    {
                        if (Group == ProfilesGroup)
                        {
                            await this.profiles.DeleteAsync(entry.Id, token).ConfigureAwait(true);
                        }
                        else if (Group == ProgramsGroup)
                        {
                            await this.programs.DeleteAsync(entry.Id, token).ConfigureAwait(true);
                        }

                        Say("Lib_Deleted", entry.Name);
                        await ReloadAsync(token).ConfigureAwait(true);
                    },
                    CancellationToken.None).ConfigureAwait(true);
            },
            entry.Name);
    }

    /// <summary>导出选中的一条到 U 盘（交换文件）。</summary>
    private async Task ExportSelectedAsync()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        string? path = InteractionScope.FileDialogs.PickSavePath(
            SafeFileName(entry.Name) + LibraryExchangeFile.Extension, LibraryExchangeFile.Extension, Localizer["Lib_FileFilter"]);
        if (path is null)
        {
            return;
        }

        await RunGuardedAsync(
            async token =>
            {
                var profileList = new List<RollProfileDefinition>();
                var programList = new List<GrindingProgram>();
                if (Group == ProfilesGroup && await this.profiles.GetAsync(entry.Id, token).ConfigureAwait(true) is { } profile)
                {
                    profileList.Add(profile);
                }
                else if (Group == ProgramsGroup && await this.programs.GetAsync(entry.Id, token).ConfigureAwait(true) is { } program)
                {
                    programList.Add(program);
                }

                await LibraryExchangeFile.ExportAsync(path, profileList, programList, token).ConfigureAwait(true);
                Say("Lib_ExportedFormat", profileList.Count + programList.Count, path);
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>U 盘组：整库导出（全部辊形与程序）。</summary>
    private async Task ExportAllAsync()
    {
        string? path = InteractionScope.FileDialogs.PickSavePath(
            "RGX-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + LibraryExchangeFile.Extension,
            LibraryExchangeFile.Extension,
            Localizer["Lib_FileFilter"]);
        if (path is null)
        {
            return;
        }

        await RunGuardedAsync(
            async token =>
            {
                var profileList = new List<RollProfileDefinition>();
                foreach (RollProfileSummary summary in await this.profiles.ListAsync(int.MaxValue, token).ConfigureAwait(true))
                {
                    if (await this.profiles.GetAsync(summary.ProfileId, token).ConfigureAwait(true) is { } profile)
                    {
                        profileList.Add(profile);
                    }
                }

                var programList = new List<GrindingProgram>();
                foreach (ProgramSummary summary in await this.programs.ListAsync(int.MaxValue, token).ConfigureAwait(true))
                {
                    if (await this.programs.GetAsync(summary.ProgramId, token).ConfigureAwait(true) is { } program)
                    {
                        programList.Add(program);
                    }
                }

                await LibraryExchangeFile.ExportAsync(path, profileList, programList, token).ConfigureAwait(true);
                Say("Lib_ExportedFormat", profileList.Count + programList.Count, path);
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>从 U 盘导入：辊形、程序全部加进库；同名的加"(2)"，不覆盖现场已有的。</summary>
    private async Task ImportAsync()
    {
        string? path = InteractionScope.FileDialogs.PickOpenPath(LibraryExchangeFile.Extension, Localizer["Lib_FileFilter"]);
        if (path is null)
        {
            return;
        }

        await RunGuardedAsync(
            async token =>
            {
                LibraryExchangeContent content = await LibraryExchangeFile.ReadAsync(path, token).ConfigureAwait(true);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                int stamp = 0;

                var profileNames = (await this.profiles.ListAsync(int.MaxValue, token).ConfigureAwait(true))
                    .Select(summary => summary.Name).ToHashSet(StringComparer.Ordinal);
                foreach (RollProfileDefinition profile in content.Profiles)
                {
                    string name = LibraryExchangeFile.UniqueName(profile.Name, profileNames);
                    profileNames.Add(name);
                    string id = string.Create(CultureInfo.InvariantCulture, $"P{now:yyyyMMddHHmmssfff}{stamp++:000}");
                    await this.profiles.SaveAsync(profile with { ProfileId = id, Name = name, ModifiedAtUtc = now }, token).ConfigureAwait(true);
                }

                var programNames = (await this.programs.ListAsync(int.MaxValue, token).ConfigureAwait(true))
                    .Select(summary => summary.Name).ToHashSet(StringComparer.Ordinal);
                foreach (GrindingProgram program in content.Programs)
                {
                    string name = LibraryExchangeFile.UniqueName(program.Name, programNames);
                    programNames.Add(name);
                    string id = string.Create(CultureInfo.InvariantCulture, $"G{now:yyyyMMddHHmmssfff}{stamp++:000}");
                    await this.programs.SaveAsync(program with { ProgramId = id, Name = name, ModifiedAtUtc = now }, token).ConfigureAwait(true);
                }

                Say("Lib_ImportedFormat", content.Profiles.Count, content.Programs.Count);
                await ReloadAsync(token).ConfigureAwait(true);
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    private static string Time(DateTimeOffset utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
