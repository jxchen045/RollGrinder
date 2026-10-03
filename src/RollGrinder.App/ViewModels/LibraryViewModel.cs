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
using RollGrinder.Services.Library;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>哪一个库。</summary>
public enum LibraryKind
{
    /// <summary>辊形库（辊形区）。</summary>
    Profiles = 0,

    /// <summary>程序库（工艺区）。</summary>
    Programs = 1,
}

/// <summary>库里的一行。</summary>
/// <param name="Id">标识。</param>
/// <param name="Name">名字。</param>
/// <param name="VersionText">版本（v3）。</param>
/// <param name="Detail">一句话概况（设计长度、段数 / 工序数、适用类型）。</param>
/// <param name="UsersText">在用几支辊。</param>
/// <param name="ModifiedText">最后修改。</param>
/// <param name="IsDisabled">已停用（灰显，不出现在登记、换辊形 / 程序的列表里）。</param>
public sealed record LibraryEntryViewModel(
    string Id, string Name, string VersionText, string Detail, string UsersText, string ModifiedText, bool IsDisabled);

/// <summary>在用清单的一行。</summary>
public sealed record LibraryUserRowViewModel(string RollId, string Purpose, string CurrentText);

/// <summary>留档的一版。</summary>
public sealed record LibraryVersionRowViewModel(int Version, string VersionText, string SavedText, string SavedBy, string Payload);

/// <summary>
/// 辊形库 / 程序库（界面修订稿 v3 6.5.2、6.6.2）：库回到各自的区，横键"辊形库""程序库"就地打开。
/// 关系设计第 4 节的规则都在这一页：版本号、在用清单、停用（被引用的不能删）、旧版本只读留档、"以此为底另存为新的"。
///
/// 同一个类建两份：<see cref="LibraryKind.Profiles"/> 归辊形区，<see cref="LibraryKind.Programs"/> 归工艺区。
/// 竖键：打开 · 新建 · 复制… · 重命名… · 停用… / 启用… · 版本记录 · 删除…；横键：库 · 编辑 · 导入… · 导出… · 全部导出…。
/// </summary>
public sealed partial class LibraryViewModel : PageViewModelBase
{
    private const int ListLimit = 500;

    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IRollRepository rolls;
    private readonly ILibraryService library;
    private readonly IUserSession session;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly HmiSettings settings;
    private readonly JobDraft jobDraft;
    private readonly FunctionKeyViewModel libraryKey;
    private readonly FunctionKeyViewModel disableKey;
    private readonly FunctionKeyViewModel versionsKey;
    private readonly FunctionKeyViewModel saveAsFromVersionKey;
    private readonly IReadOnlyList<FunctionKeyViewModel?> entryKeys;
    private readonly IReadOnlyList<FunctionKeyViewModel?> versionKeys;

    public LibraryViewModel(
        LibraryKind kind,
        IRollProfileRepository profiles,
        IProgramRepository programs,
        IRollRepository rolls,
        ILibraryService library,
        IUserSession session,
        RollProfileTypeRegistry profileTypes,
        HmiSettings settings,
        JobDraft jobDraft,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        Kind = kind;
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.jobDraft = jobDraft ?? throw new ArgumentNullException(nameof(jobDraft));
        NamePrompt = new NamePromptViewModel(localizer);
        AttachPrompt(NamePrompt);

        bool isProfiles = kind == LibraryKind.Profiles;
        this.libraryKey = FunctionKeyViewModel.ForAction(isProfiles ? "Fn_ProfileLibrary" : "Fn_ProgramLibrary", localizer, () => { });
        SetFunctionKeys(new FunctionKeyViewModel?[]
        {
            this.libraryKey,
            FunctionKeyViewModel.ForAction(isProfiles ? "Fn_ProfileEditor" : "Fn_ProgramEditor", localizer,
                () => Navigator.GoTo(isProfiles ? PageKey.Profile : PageKey.Steps)),
            null,
            new FunctionKeyViewModel("Fn_ImportFile", new AsyncRelayCommand(ImportAsync), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Fn_ExportFile", new AsyncRelayCommand(ExportSelectedAsync, HasSelection), localizer)
            {
                PreconditionResourceKey = "Lib_NothingSelected",
            },
            new FunctionKeyViewModel("Fn_ExportAll", new AsyncRelayCommand(ExportAllAsync), localizer),
        });
        MarkActiveFunctionKey(this.libraryKey);

        this.disableKey = new FunctionKeyViewModel("Vk_Disable", new RelayCommand(AskToggleDisabled, HasSelection), localizer, requiresEditable: true)
        {
            PreconditionResourceKey = "Lib_NothingSelected",
        };
        this.versionsKey = new FunctionKeyViewModel("Vk_Versions", new RelayCommand(ToggleVersions, HasSelection), localizer)
        {
            PreconditionResourceKey = "Lib_NothingSelected",
        };
        this.saveAsFromVersionKey = new FunctionKeyViewModel("Vk_SaveVersionAsNew", new RelayCommand(AskSaveVersionAsNew, () => SelectedVersion is not null), localizer, requiresEditable: true)
        {
            PreconditionResourceKey = "Lib_NoVersionSelected",
        };
        this.entryKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_Open", new RelayCommand(OpenSelected, HasSelection), localizer) { PreconditionResourceKey = "Lib_NothingSelected" },
            new FunctionKeyViewModel("Vk_New", new RelayCommand(CreateNew), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_Copy", new RelayCommand(AskCopy, HasSelection), localizer, requiresEditable: true) { PreconditionResourceKey = "Lib_NothingSelected" },
            new FunctionKeyViewModel("Vk_Rename", new RelayCommand(AskRename, HasSelection), localizer, requiresEditable: true) { PreconditionResourceKey = "Lib_NothingSelected" },
            this.disableKey,
            this.versionsKey,
            new FunctionKeyViewModel("Vk_Delete", new RelayCommand(AskDelete, HasSelection), localizer, requiresEditable: true) { PreconditionResourceKey = "Lib_NothingSelected" },
        };
        this.versionKeys = new FunctionKeyViewModel?[] { this.saveAsFromVersionKey, null, null, null, null, this.versionsKey };
        SetVerticalKeys(this.entryKeys);
    }

    public LibraryKind Kind { get; }

    public bool IsProfiles => Kind == LibraryKind.Profiles;

    public override PageKey Key => IsProfiles ? PageKey.ProfileLibrary : PageKey.ProgramLibrary;

    public override string TitleResourceKey => IsProfiles ? "Page_ProfileLibrary" : "Page_ProgramLibrary";

    /// <summary>离线可用：只和数据库、U 盘打交道。</summary>
    public override bool WorksOffline => true;

    /// <summary>库里的增删改、停用归管理员（关系设计第 8 节）。</summary>
    public override Permission? EditPermission => IsProfiles ? Permission.EditProfiles : Permission.EditPrograms;

    /// <summary>窗口标题：辊形库 · 23 条。</summary>
    public string ListTitle => Localizer.Format("Lib_TitleFormat", Localizer[IsProfiles ? "Lib_Profiles" : "Lib_Programs"], Entries.Count);

    public ObservableCollection<LibraryEntryViewModel> Entries { get; } = new();

    [ObservableProperty]
    private LibraryEntryViewModel? selectedEntry;

    /// <summary>预览要点（程序：适用类型、标准余量、工序；辊形：设计长度、公差、段数）。</summary>
    public ObservableCollection<LabelValueViewModel> PreviewRows { get; } = new();

    /// <summary>辊形预览：沿辊身的直径量 µm（只有辊形库有）。</summary>
    public IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> PreviewCurve { get; private set; } =
        Array.Empty<(double, double)>();

    /// <summary>预览曲线变了。</summary>
    public event EventHandler? PreviewChanged;

    /// <summary>在用清单：计划里用选中这一条的辊。</summary>
    public ObservableCollection<LibraryUserRowViewModel> Users { get; } = new();

    /// <summary>右下角显示的是在用清单（假）还是版本记录（真）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LowerTitle))]
    private bool showingVersions;

    /// <summary>选中那一条留档的各版。</summary>
    public ObservableCollection<LibraryVersionRowViewModel> Versions { get; } = new();

    [ObservableProperty]
    private LibraryVersionRowViewModel? selectedVersion;

    /// <summary>右下角的标题：在用清单（12 支）/ 版本记录（3 版）。</summary>
    public string LowerTitle => ShowingVersions
        ? Localizer.Format("Lib_VersionsTitleFormat", Versions.Count)
        : Localizer.Format("Lib_UsersTitleFormat", Users.Count);

    /// <summary>复制、重命名、另存为新的用的命名框。</summary>
    public NamePromptViewModel NamePrompt { get; }

    public override bool HasModalPrompt => NamePrompt.IsOpen;

    /// <summary>最近一次刷新列表的任务。自检等它读完。</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public override bool TryDismissPrompt()
    {
        if (!NamePrompt.IsOpen)
        {
            return false;
        }

        NamePrompt.CancelCommand.Execute(null);
        return true;
    }

    public override void OnActivated() => Loading = RunGuardedAsync(ReloadAsync, CancellationToken.None);

    /// <summary>长按一行 = 打开。</summary>
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

    partial void OnSelectedEntryChanged(LibraryEntryViewModel? value)
    {
        foreach (FunctionKeyViewModel? key in this.entryKeys)
        {
            (key?.Command as IRelayCommand)?.NotifyCanExecuteChanged();
        }

        this.disableKey.LabelResourceKey = value?.IsDisabled == true ? "Vk_Enable" : "Vk_Disable";
        _ = RunRefreshAsync(LoadPreviewAsync, CancellationToken.None);
    }

    partial void OnSelectedVersionChanged(LibraryVersionRowViewModel? value) =>
        (this.saveAsFromVersionKey.Command as IRelayCommand)?.NotifyCanExecuteChanged();

    partial void OnShowingVersionsChanged(bool value)
    {
        SetVerticalKeys(value ? this.versionKeys : this.entryKeys);
        this.versionsKey.IsActive = value;
    }

    private bool HasSelection() => SelectedEntry is not null;

    private string UserName => this.session.CurrentUser?.UserName ?? string.Empty;

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        string? keep = SelectedEntry?.Id;
        Entries.Clear();
        Dictionary<string, int> usage = (await this.rolls.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(true))
            .Where(roll => !roll.Retired)
            .Select(roll => IsProfiles ? roll.TargetProfileId : roll.ProgramId)
            .Where(id => id is not null)
            .GroupBy(id => id!)
            .ToDictionary(group => group.Key, group => group.Count());

        if (IsProfiles)
        {
            foreach (RollProfileSummary entry in (await this.profiles.ListAsync(ListLimit, cancellationToken).ConfigureAwait(true))
                         .OrderBy(entry => entry.Disabled))
            {
                Entries.Add(new LibraryEntryViewModel(
                    entry.ProfileId,
                    entry.Name,
                    Localizer.Format("Lib_VersionFormat", entry.Version),
                    Localizer.Format("Lib_ProfileDetailFormat", entry.SegmentCount, entry.BodyLengthMm.ToString("F0", CultureInfo.InvariantCulture)),
                    usage.TryGetValue(entry.ProfileId, out int used) ? used.ToString(CultureInfo.InvariantCulture) : "0",
                    Time(entry.ModifiedAtUtc),
                    entry.Disabled));
            }
        }
        else
        {
            foreach (ProgramSummary entry in (await this.programs.ListAsync(ListLimit, cancellationToken).ConfigureAwait(true))
                         .OrderBy(entry => entry.Disabled))
            {
                Entries.Add(new LibraryEntryViewModel(
                    entry.ProgramId,
                    entry.Name,
                    Localizer.Format("Lib_VersionFormat", entry.Version),
                    Localizer.Format("Lib_ProgramDetailFormat", entry.StepCount, Localizer["RollKindFilter_" + entry.ApplicableRollKind]),
                    usage.TryGetValue(entry.ProgramId, out int used) ? used.ToString(CultureInfo.InvariantCulture) : "0",
                    Time(entry.ModifiedAtUtc),
                    entry.Disabled));
            }
        }

        OnPropertyChanged(nameof(ListTitle));
        SelectedEntry = Entries.FirstOrDefault(entry => entry.Id == keep) ?? Entries.FirstOrDefault();
        if (SelectedEntry is null)
        {
            await LoadPreviewAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        PreviewRows.Clear();
        Users.Clear();
        Versions.Clear();
        PreviewCurve = Array.Empty<(double, double)>();
        if (SelectedEntry is { } entry)
        {
            if (IsProfiles && await this.profiles.GetAsync(entry.Id, cancellationToken).ConfigureAwait(true) is { } profile)
            {
                RollGeometry geometry = RollGeometry.FromDiameter(profile.BodyLengthMm, profile.NominalDiameterMm ?? 1000.0);
                PreviewCurve = profile.Profile.Compose(geometry, this.profileTypes, this.settings.ProfileSampleCount).Points
                    .Select(point => (point.BodyPositionMm, UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm)))
                    .ToArray();
                PreviewRows.Add(new LabelValueViewModel("Lib_PreviewBodyLength", profile.BodyLengthMm.ToString("F0", CultureInfo.InvariantCulture), Localizer));
                PreviewRows.Add(new LabelValueViewModel("Lib_PreviewTolerance",
                    profile.ToleranceMicrometer is double tolerance ? tolerance.ToString("F1", CultureInfo.CurrentCulture) : "--", Localizer));
                PreviewRows.Add(new LabelValueViewModel("Lib_PreviewSegments", profile.SegmentCount.ToString(CultureInfo.InvariantCulture), Localizer));
            }
            else if (!IsProfiles && await this.programs.GetAsync(entry.Id, cancellationToken).ConfigureAwait(true) is { } program)
            {
                PreviewRows.Add(new LabelValueViewModel("Lib_PreviewApplicable",
                    Localizer["RollKindFilter_" + program.ApplicableRollKind]
                    + (string.IsNullOrWhiteSpace(program.ApplicableMaterial) ? string.Empty : " · " + program.ApplicableMaterial), Localizer));
                PreviewRows.Add(new LabelValueViewModel("Lib_PreviewStandardStock",
                    program.StandardStockMicrometer is double stock ? (stock / 1000.0).ToString("F3", CultureInfo.CurrentCulture) + " mm" : "--", Localizer));
                PreviewRows.Add(new LabelValueViewModel("Lib_PreviewSteps",
                    string.Join(" · ", program.Steps.Select(step => Localizer["StepType_" + step.StepTypeKey])), Localizer));
            }

            IReadOnlyList<RollRecord> users = IsProfiles
                ? await this.library.ProfileUsersAsync(entry.Id, cancellationToken).ConfigureAwait(true)
                : await this.library.ProgramUsersAsync(entry.Id, cancellationToken).ConfigureAwait(true);
            foreach (RollRecord roll in users)
            {
                Users.Add(new LibraryUserRowViewModel(roll.RollId, roll.Purpose ?? "--", roll.StartDiameterMm.ToString("F2", CultureInfo.CurrentCulture)));
            }

            foreach (LibraryVersionEntry version in await this.library
                         .VersionsAsync(IsProfiles ? SqliteLibraryVersionRepository.ProfileKind : SqliteLibraryVersionRepository.ProgramKind, entry.Id, cancellationToken)
                         .ConfigureAwait(true))
            {
                Versions.Add(new LibraryVersionRowViewModel(
                    version.Version, Localizer.Format("Lib_VersionFormat", version.Version), Time(version.SavedAtUtc), version.SavedBy, version.Payload));
            }
        }

        OnPropertyChanged(nameof(LowerTitle));
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>新建：到编辑页开一张空的。</summary>
    private void CreateNew()
    {
        if (IsProfiles)
        {
            this.jobDraft.ProfileToOpen = JobDraft.NewEntry;
            Navigator.GoTo(PageKey.Profile);
        }
        else
        {
            this.jobDraft.ProgramToOpen = JobDraft.NewEntry;
            Navigator.GoTo(PageKey.Steps);
        }
    }

    /// <summary>打开：到编辑页。</summary>
    private void OpenSelected()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        if (IsProfiles)
        {
            this.jobDraft.ProfileToOpen = entry.Id;
            Navigator.GoTo(PageKey.Profile);
        }
        else
        {
            this.jobDraft.ProgramToOpen = entry.Id;
            Navigator.GoTo(PageKey.Steps);
        }
    }

    private void ToggleVersions() => ShowingVersions = !ShowingVersions;

    private void AskCopy()
    {
        if (SelectedEntry is { } entry)
        {
            NamePrompt.Open(
                Localizer["Lib_CopyTitle"],
                Localizer.Format("Library_CopyNameFormat", entry.Name),
                (name, overwrite, token) => StoreCopyAsync(entry.Id, null, name, rename: false, token));
        }
    }

    private void AskRename()
    {
        if (SelectedEntry is { } entry)
        {
            NamePrompt.Open(
                Localizer["Lib_RenameTitle"],
                entry.Name,
                (name, overwrite, token) => StoreCopyAsync(entry.Id, null, name, rename: true, token));
        }
    }

    /// <summary>以留档的某一版为底另存为新的（旧版本只读，不能直接改回）。</summary>
    private void AskSaveVersionAsNew()
    {
        if (SelectedEntry is { } entry && SelectedVersion is { } version)
        {
            NamePrompt.Open(
                Localizer["Lib_SaveVersionAsNewTitle"],
                Localizer.Format("Library_VersionCopyNameFormat", entry.Name, version.Version),
                (name, overwrite, token) => StoreCopyAsync(entry.Id, version.Payload, name, rename: false, token));
        }
    }

    /// <summary>
    /// 复制、改名、以旧版为底另存。名字在库里唯一；改名保留标识（版本 +1、留档），复制与另存是新条目（从 v1 起）。
    /// </summary>
    private async Task<NamePromptOutcome> StoreCopyAsync(
        string id, string? fromPayload, string name, bool rename, CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (IsProfiles)
            {
                RollProfileDefinition? source = fromPayload is not null
                    ? LibrarySnapshotJson.ReadProfile(fromPayload)
                    : await this.profiles.GetAsync(id, cancellationToken).ConfigureAwait(true);
                if (source is null)
                {
                    return NamePromptOutcome.Done;
                }

                string newId = rename ? source.ProfileId : string.Create(CultureInfo.InvariantCulture, $"P{DateTimeOffset.Now:yyyyMMddHHmmssfff}");
                if (await this.profiles.FindIdByNameAsync(name, newId, cancellationToken).ConfigureAwait(true) is not null)
                {
                    return NamePromptOutcome.Refused(Localizer.Format("Library_NameTakenFormat", name));
                }

                await this.library.SaveProfileAsync(
                    source with { ProfileId = newId, Name = name, Disabled = false, CreatedAtUtc = rename ? source.CreatedAtUtc : now },
                    UserName,
                    cancellationToken).ConfigureAwait(true);
            }
            else
            {
                GrindingProgram? source = fromPayload is not null
                    ? LibrarySnapshotJson.ReadProgram(fromPayload)
                    : await this.programs.GetAsync(id, cancellationToken).ConfigureAwait(true);
                if (source is null)
                {
                    return NamePromptOutcome.Done;
                }

                string newId = rename ? source.ProgramId : string.Create(CultureInfo.InvariantCulture, $"G{DateTimeOffset.Now:yyyyMMddHHmmssfff}");
                if (await this.programs.FindIdByNameAsync(name, newId, cancellationToken).ConfigureAwait(true) is not null)
                {
                    return NamePromptOutcome.Refused(Localizer.Format("Library_NameTakenFormat", name));
                }

                await this.library.SaveProgramAsync(
                    source with { ProgramId = newId, Name = name, Disabled = false, CreatedAtUtc = rename ? source.CreatedAtUtc : now },
                    UserName,
                    cancellationToken).ConfigureAwait(true);
            }

            Say(rename ? "Lib_Renamed" : "Lib_Copied", name);
            ShowingVersions = false;
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
            return NamePromptOutcome.Done;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
            return NamePromptOutcome.Refused(Localizer["Library_SaveFailed"]);
        }
    }

    /// <summary>停用…（被引用的提醒先改别的）/ 启用…：问一句。</summary>
    private void AskToggleDisabled()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        bool disable = !entry.IsDisabled;
        Ask(
            disable ? (Users.Count > 0 ? "Lib_AskDisableInUse" : "Lib_AskDisable") : "Lib_AskEnable",
            async () => await RunGuardedAsync(
                async token =>
                {
                    if (IsProfiles)
                    {
                        await this.library.SetProfileDisabledAsync(entry.Id, disable, UserName, token).ConfigureAwait(true);
                    }
                    else
                    {
                        await this.library.SetProgramDisabledAsync(entry.Id, disable, UserName, token).ConfigureAwait(true);
                    }

                    Say(disable ? "Lib_Disabled" : "Lib_Enabled", entry.Name);
                    await ReloadAsync(token).ConfigureAwait(true);
                },
                CancellationToken.None).ConfigureAwait(true),
            entry.Name,
            Users.Count);
    }

    /// <summary>删除…：被计划引用的不能删，只能停用；作业存的是快照，删了不影响已磨过的记录。</summary>
    private void AskDelete()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        if (Users.Count > 0)
        {
            Interaction.Refuse(Localizer.Format("Lib_DeleteRefusedInUse", entry.Name, Users.Count));
            return;
        }

        Ask(
            "Lib_AskDelete",
            async () => await RunGuardedAsync(
                async token =>
                {
                    bool deleted = IsProfiles
                        ? await this.library.TryDeleteProfileAsync(entry.Id, UserName, token).ConfigureAwait(true)
                        : await this.library.TryDeleteProgramAsync(entry.Id, UserName, token).ConfigureAwait(true);
                    if (deleted)
                    {
                        Say("Lib_Deleted", entry.Name);
                    }
                    else
                    {
                        Interaction.Refuse(Localizer.Format("Lib_DeleteRefusedInUse", entry.Name, Users.Count));
                    }

                    await ReloadAsync(token).ConfigureAwait(true);
                },
                CancellationToken.None).ConfigureAwait(true),
            entry.Name);
    }

    /// <summary>导出选中的一条（交换文件，U 盘）。</summary>
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
                if (IsProfiles && await this.profiles.GetAsync(entry.Id, token).ConfigureAwait(true) is { } profile)
                {
                    profileList.Add(profile);
                }
                else if (!IsProfiles && await this.programs.GetAsync(entry.Id, token).ConfigureAwait(true) is { } program)
                {
                    programList.Add(program);
                }

                await LibraryExchangeFile.ExportAsync(path, profileList, programList, token).ConfigureAwait(true);
                Say("Lib_ExportedFormat", profileList.Count + programList.Count, path);
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>全部导出：整库（辊形与程序都带上，换机、备份用）。</summary>
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

    /// <summary>从交换文件导入：全部加进库，同名的加"(2)"，不覆盖现场已有的（现场那一条可能正被辊的计划用着）。</summary>
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
                    await this.library.SaveProfileAsync(profile with { ProfileId = id, Name = name }, UserName, token).ConfigureAwait(true);
                }

                var programNames = (await this.programs.ListAsync(int.MaxValue, token).ConfigureAwait(true))
                    .Select(summary => summary.Name).ToHashSet(StringComparer.Ordinal);
                foreach (GrindingProgram program in content.Programs)
                {
                    string name = LibraryExchangeFile.UniqueName(program.Name, programNames);
                    programNames.Add(name);
                    string id = string.Create(CultureInfo.InvariantCulture, $"G{now:yyyyMMddHHmmssfff}{stamp++:000}");
                    await this.library.SaveProgramAsync(program with { ProgramId = id, Name = name }, UserName, token).ConfigureAwait(true);
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
