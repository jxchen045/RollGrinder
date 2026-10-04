using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data.Model;
using RollGrinder.Nc;
using RollGrinder.Services.Audit;
using RollGrinder.Services.Manual;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 机床配置与标签映射的结构化编辑（调试区，最终稿 5.13）：机床配置按分组表单，标签映射按表格（可搜索、只看缺失、单点试读）。
/// 所有人可看，只有制造商能改（Q9）；改动即校验，不成立存不进去；保存前自动备份原文件，每次改动写改动记录，
/// 保存后提示"重启上位机后生效"；"恢复上一版…"把最近一份备份载入编辑区，按"✓ 保存…"才生效。
/// </summary>
public sealed partial class CommissioningViewModel
{
    private JsonObject? machineLoaded;
    private JsonObject? machineWorking;
    private JsonObject? tagLoaded;
    private JsonObject? tagWorking;
    private readonly List<ConfigFieldViewModel> machineFields = new();
    private readonly List<TagRowViewModel> allTags = new();

    /// <summary>机床配置表单的分组。</summary>
    public ObservableCollection<ConfigGroupViewModel> MachineGroups { get; } = new();

    /// <summary>标签映射表里现在显示的行（按搜索与"只看缺失"过滤）。</summary>
    public ObservableCollection<TagRowViewModel> VisibleTags { get; } = new();

    /// <summary>对不到某一格上的问题（整份文件的结构问题）。</summary>
    public ObservableCollection<string> ConfigIssues { get; } = new();

    /// <summary>选中那一行的数据类型（分段键）。</summary>
    public ObservableCollection<ParameterChoiceViewModel> TagDataTypeChoices { get; } = new();

    /// <summary>选中那一行的读写方向（分段键）。</summary>
    public ObservableCollection<ParameterChoiceViewModel> TagAccessChoices { get; } = new();

    [ObservableProperty]
    private TagRowViewModel? selectedTag;

    [ObservableProperty]
    private string tagSearchText = string.Empty;

    [ObservableProperty]
    private bool showMissingTagsOnly;

    /// <summary>编辑区顶上的状态行：有几处问题、保存结果、试读结果。</summary>
    [ObservableProperty]
    private string configStatusText = string.Empty;

    [ObservableProperty]
    private int configIssueCount;

    public bool CanEditMachineConfig => Can(Permission.EditMachineConfig);

    public bool CanEditTagMap => Can(Permission.EditTagMap);

    private void OnConfigEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsDirty):
                RefreshCommitPair();
                break;
            case nameof(TagSearchText):
            case nameof(ShowMissingTagsOnly):
                RefreshVisibleTags();
                break;
            case nameof(SelectedTag):
                BuildTagChoices();
                foreach (FunctionKeyViewModel key in VerticalKeys)
                {
                    (key.Command as IRelayCommand)?.NotifyCanExecuteChanged();
                }

                break;
        }
    }

    // ── 机床配置 ──────────────────────────────────────────────────────────────

    /// <summary>机床配置：按分组的表单，不再是 JSON 原文。编辑区里有没存的改动时不重读（切组回来改动还在）。</summary>
    private Task LoadMachineConfigAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            if (this.machineLoaded is not null && IsDirty)
            {
                return;
            }

            this.machineLoaded = await this.configStore.LoadAsync(ConfigFileKind.Machine, token).ConfigureAwait(true);
            this.machineWorking = (JsonObject)this.machineLoaded.DeepClone();
            BuildMachineGroups();
            RevalidateMachine();
        }, cancellationToken);

    private void BuildMachineGroups()
    {
        MachineGroups.Clear();
        this.machineFields.Clear();
        JsonObject document = this.machineWorking!;

        AddGroup("CfgGroup_identity", string.Empty, document, new[] { "machineId", "displayName", "schemaVersion" }, new[] { "schemaVersion" });
        AddObjectGroup("controller", document["controller"] as JsonObject);
        if (document["axes"] is JsonArray axes)
        {
            for (int i = 0; i < axes.Count; i++)
            {
                if (axes[i] is JsonObject axis)
                {
                    AddGroup(
                        Localizer.Format("CfgGroup_AxisFormat", Leaf(axis, "name"), Localizer["AxisRole_" + Leaf(axis, "role")]),
                        string.Create(CultureInfo.InvariantCulture, $"axes[{i}]."),
                        axis,
                        axis.Select(pair => pair.Key).ToArray(),
                        new[] { "role" },
                        titleIsText: true);
                }
            }
        }

        if (document["measurementChannels"] is JsonArray channels)
        {
            for (int i = 0; i < channels.Count; i++)
            {
                if (channels[i] is JsonObject channel)
                {
                    AddGroup(
                        Localizer.Format("CfgGroup_ChannelFormat", Leaf(channel, "name")),
                        string.Create(CultureInfo.InvariantCulture, $"measurementChannels[{i}]."),
                        channel,
                        channel.Select(pair => pair.Key).ToArray(),
                        new[] { "quantity" },
                        titleIsText: true);
                }
            }
        }

        foreach (string name in new[] { "options", "workpiece" })
        {
            AddObjectGroup(name, document[name] as JsonObject);
        }

        // 作业核对（关系设计第 6 节）：长度容差、工件线速度上下限单列一组，其余阈值照旧。
        if (document["thresholds"] is JsonObject thresholds)
        {
            string[] jobChecks = JobCheckThresholdKeys.Where(thresholds.ContainsKey).ToArray();
            if (jobChecks.Length > 0)
            {
                AddGroup("CfgGroup_jobChecks", "thresholds.", thresholds, jobChecks, Array.Empty<string>());
            }

            AddGroup("CfgGroup_thresholds", "thresholds.", thresholds,
                thresholds.Select(pair => pair.Key).Where(key => !JobCheckThresholdKeys.Contains(key)).ToArray(), Array.Empty<string>());
        }

        foreach (string name in new[] { "stepTypeCodes", "auxiliaryActionCodes" })
        {
            AddObjectGroup(name, document[name] as JsonObject);
        }
    }

    private static readonly string[] JobCheckThresholdKeys =
    {
        MachineDescription.LengthTolerancePercentKey,
        MachineDescription.MinWorkpieceSurfaceSpeedKey,
        MachineDescription.MaxWorkpieceSurfaceSpeedKey,
    };

    private void AddObjectGroup(string name, JsonObject? group)
    {
        if (group is not null)
        {
            AddGroup("CfgGroup_" + name, name + ".", group, group.Select(pair => pair.Key).ToArray(), Array.Empty<string>());
        }
    }

    private void AddGroup(
        string title,
        string pathPrefix,
        JsonObject owner,
        IReadOnlyList<string> properties,
        IReadOnlyList<string> readOnly,
        bool titleIsText = false)
    {
        var fields = new List<ConfigFieldViewModel>();
        foreach (string property in properties)
        {
            if (owner[property] is not JsonValue value)
            {
                continue;
            }

            ConfigFieldKind kind = value.GetValueKind() switch
            {
                JsonValueKind.True or JsonValueKind.False => ConfigFieldKind.Flag,
                JsonValueKind.Number => ConfigFieldKind.Number,
                _ => ConfigFieldKind.Text,
            };
            IReadOnlyList<string> choices = property == "closedLoop" ? Enum.GetNames<AxisClosedLoopKind>() : Array.Empty<string>();
            if (choices.Count > 0)
            {
                kind = ConfigFieldKind.Choice;
            }

            var field = new ConfigFieldViewModel(
                pathPrefix + property,
                owner,
                property,
                kind,
                FieldLabel(property),
                UnitOf(property),
                choices,
                readOnly.Contains(property),
                OnMachineFieldChanged,
                choice => Localizer["Cfg_ClosedLoop_" + choice]);
            fields.Add(field);
            this.machineFields.Add(field);
        }

        if (fields.Count > 0)
        {
            MachineGroups.Add(new ConfigGroupViewModel(titleIsText ? title : Localizer[title], fields));
        }
    }

    private void OnMachineFieldChanged()
    {
        RevalidateMachine();
        UpdateConfigDirty();
    }

    private void RevalidateMachine()
    {
        IReadOnlyList<ConfigIssue> issues = ConfigDocumentStore.Validate(ConfigFileKind.Machine, this.machineWorking!);
        int count = 0;
        foreach (ConfigFieldViewModel field in this.machineFields)
        {
            ConfigIssue? issue = issues.FirstOrDefault(candidate => candidate.Path == field.Path);
            field.IssueText = !field.IsParseable
                ? Localizer["Cfg_Issue_NotANumber"]
                : issue is null ? string.Empty : Localizer[issue.ReasonResourceKey];
            count += field.HasIssue ? 1 : 0;
        }

        ShowGeneralIssues(issues.Where(issue => this.machineFields.All(field => field.Path != issue.Path)).ToArray());
        ConfigIssueCount = count + ConfigIssues.Count;
        ConfigStatusText = ConfigIssueCount == 0 ? Localizer["Cfg_Valid"] : Localizer.Format("Cfg_IssueCountFormat", ConfigIssueCount);
    }

    // ── 标签映射 ──────────────────────────────────────────────────────────────

    /// <summary>标签映射：表格，可搜索、只看缺失、单点试读。</summary>
    private Task LoadTagMappingAsync(CancellationToken cancellationToken) =>
        RunGuardedAsync(async token =>
        {
            if (this.tagLoaded is not null && IsDirty)
            {
                return;
            }

            this.tagLoaded = await this.configStore.LoadAsync(ConfigFileKind.TagMap, token).ConfigureAwait(true);
            this.tagWorking = (JsonObject)this.tagLoaded.DeepClone();
            BuildTagRows();
            RevalidateTags();
        }, cancellationToken);

    private void BuildTagRows()
    {
        this.allTags.Clear();
        var present = new HashSet<string>(StringComparer.Ordinal);
        if (this.tagWorking!["tags"] is JsonArray tags)
        {
            foreach (JsonObject tag in tags.OfType<JsonObject>())
            {
                string key = Leaf(tag, "key");
                present.Add(key);
                this.allTags.Add(new TagRowViewModel(key, tag, NcJobTranslator.RequiredTagKeys.Contains(key), OnTagChanged));
            }
        }

        // 没登记的：下发必需的、监视要读的、手动动作要写的。数组变量（报警号、工序数组）按基名算。
        foreach (string key in KnownTagKeys().Where(key => !present.Contains(key)))
        {
            this.allTags.Add(new TagRowViewModel(key, null, NcJobTranslator.RequiredTagKeys.Contains(key), OnTagChanged));
        }

        RefreshVisibleTags();
    }

    private IEnumerable<string> KnownTagKeys() =>
        NcJobTranslator.RequiredTagKeys
            .Concat(MachineTagKeys.MonitoringKeys(this.machine))
            .Concat(ManualCommandCatalog.All
                .Where(command => command.Kind != ManualCommandKind.Local)
                .Select(command => MachineTagKeys.ManualCommand(command.Key)))
            .Select(key => TagKeySyntax.TrySplit(key, out string baseKey, out _) ? baseKey : key)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal);

    private void RefreshVisibleTags()
    {
        string search = TagSearchText.Trim();
        TagRowViewModel? keep = SelectedTag;
        VisibleTags.Clear();
        foreach (TagRowViewModel row in this.allTags)
        {
            if (ShowMissingTagsOnly && !row.IsMissing && row.IssueText.Length == 0)
            {
                continue;
            }

            if (search.Length > 0
                && !row.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !row.Address.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !row.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            VisibleTags.Add(row);
        }

        SelectedTag = keep is not null && VisibleTags.Contains(keep) ? keep : VisibleTags.FirstOrDefault();
    }

    private void BuildTagChoices()
    {
        TagDataTypeChoices.Clear();
        TagAccessChoices.Clear();
        if (SelectedTag is not { IsMissing: false } row)
        {
            return;
        }

        foreach (string name in Enum.GetNames<TagDataType>())
        {
            string chosen = name;
            TagDataTypeChoices.Add(new ParameterChoiceViewModel(name, name, new RelayCommand(() => SetTagChoice(row, chosen, isAccess: false)))
            {
                IsSelected = string.Equals(row.DataType, name, StringComparison.OrdinalIgnoreCase),
            });
        }

        foreach (string name in Enum.GetNames<TagAccess>())
        {
            string chosen = name;
            TagAccessChoices.Add(new ParameterChoiceViewModel(name, name, new RelayCommand(() => SetTagChoice(row, chosen, isAccess: true)))
            {
                IsSelected = string.Equals(row.Access, name, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    private void SetTagChoice(TagRowViewModel row, string value, bool isAccess)
    {
        if (!CanEditTagMap)
        {
            return;
        }

        if (isAccess)
        {
            row.Access = value;
        }
        else
        {
            row.DataType = value;
        }

        BuildTagChoices();
    }

    private void OnTagChanged()
    {
        RevalidateTags();
        UpdateConfigDirty();
    }

    private void RevalidateTags()
    {
        IReadOnlyList<ConfigIssue> issues = ConfigDocumentStore.Validate(ConfigFileKind.TagMap, this.tagWorking!);
        foreach (TagRowViewModel row in this.allTags)
        {
            ConfigIssue? issue = issues.FirstOrDefault(candidate => candidate.Path.StartsWith("tags[" + row.Key + "]", StringComparison.Ordinal));
            row.IssueText = issue is null ? string.Empty : Localizer[issue.ReasonResourceKey];
        }

        ShowGeneralIssues(issues.Where(issue => !issue.Path.StartsWith("tags[", StringComparison.Ordinal)).ToArray());
        ConfigIssueCount = this.allTags.Count(row => row.IssueText.Length > 0) + ConfigIssues.Count;
        int missingRequired = this.allTags.Count(row => row.IsMissing && row.IsRequired);
        ConfigStatusText = ConfigIssueCount == 0
            ? Localizer.Format("Cfg_TagSummaryFormat", this.allTags.Count(row => !row.IsMissing), this.allTags.Count(row => row.IsMissing), missingRequired)
            : Localizer.Format("Cfg_IssueCountFormat", ConfigIssueCount);
    }

    /// <summary>登记选中的缺失变量：加进文件，地址留空（校验会要求填上）。</summary>
    private void RegisterSelectedTag()
    {
        if (SelectedTag is not { IsMissing: true } row || this.tagWorking!["tags"] is not JsonArray tags)
        {
            return;
        }

        bool isCommand = row.Key.StartsWith(MachineTagKeys.ManualCommandPrefix, StringComparison.Ordinal)
            && !row.Key.EndsWith(".state", StringComparison.Ordinal);
        tags.Add(row.Register(
            row.Key.StartsWith(MachineTagKeys.StatusPrefix, StringComparison.Ordinal) || isCommand ? nameof(TagDataType.Boolean) : nameof(TagDataType.Double),
            isCommand ? nameof(TagAccess.ReadWrite) : nameof(TagAccess.Read)));
        BuildTagChoices();
        OnTagChanged();
    }

    /// <summary>删掉选中的一行（从文件里去掉；它若是必需的，会重新作为"缺失"列出来）。</summary>
    private void DeleteSelectedTag()
    {
        if (SelectedTag is not { IsMissing: false } row || this.tagWorking!["tags"] is not JsonArray tags || row.Node is null)
        {
            return;
        }

        tags.Remove(row.Node);
        BuildTagRows();
        OnTagChanged();
    }

    /// <summary>
    /// 单点试读：按**正在生效**的映射去机床读这一个变量（改过的地址要保存并重启后才按新地址读）。
    /// </summary>
    private async Task TestReadSelectedTagAsync(CancellationToken cancellationToken)
    {
        if (SelectedTag is not { } row)
        {
            return;
        }

        if (!this.tagMap.TryResolve(row.Key, out _))
        {
            Interaction.Refuse(Localizer.Format("Cfg_TestReadNotActiveFormat", row.Key));
            return;
        }

        try
        {
            TagValue value = await this.gateway.ReadTagAsync(row.Key, cancellationToken).ConfigureAwait(true);
            row.ValueText = value.Raw?.ToString() ?? "--";
            row.QualityText = Localizer[value.IsGood ? "Cfg_QualityGood" : "Cfg_QualityBad"];
            Say("Cfg_TestReadFormat", row.Key, row.ValueText, row.QualityText);
        }
        catch (GatewayException ex)
        {
            row.QualityText = Localizer["Cfg_QualityBad"];
            Interaction.Fail(Localizer.Format("Cfg_TestReadFailedFormat", row.Key, ex.Message));
        }
    }

    /// <summary>标签映射开着时：表里各行的当前值跟着快照走（监视在读的变量才有）。</summary>
    private void RefreshTagValues(MachineStateSnapshot snapshot)
    {
        foreach (TagRowViewModel row in VisibleTags)
        {
            if (snapshot.TryGet(row.Key, out TagValue? value) && value is not null)
            {
                row.ValueText = value.Raw?.ToString() ?? "--";
                row.QualityText = Localizer[value.IsGood ? "Cfg_QualityGood" : "Cfg_QualityBad"];
            }
        }
    }

    // ── 两份共用：竖键、保存、恢复上一版 ─────────────────────────────────────

    /// <summary>
    /// 竖键（最终稿 5.13）：标签映射 = 只看缺失、登记、删除这一行…、试读、空、恢复上一版…；
    /// 机床配置只有"恢复上一版…"（第 6 格，与标签映射同位）。有改动时 7 / 8 = ✕ 放弃改动 / ✓ 保存…。
    /// </summary>
    private IReadOnlyList<FunctionKeyViewModel?> EditorKeys(ConfigFileKind kind)
    {
        Permission permission = kind == ConfigFileKind.Machine ? Permission.EditMachineConfig : Permission.EditTagMap;
        var restore = new FunctionKeyViewModel(
            "Vk_RestorePrevious",
            new RelayCommand(() => Ask("Cfg_AskRestore", () => RestorePreviousAsync(kind, CancellationToken.None))),
            Localizer)
        {
            RequiredPermission = permission,
        };

        if (kind == ConfigFileKind.Machine)
        {
            return new FunctionKeyViewModel?[] { null, null, null, null, null, restore };
        }

        return new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_OnlyMissing", new RelayCommand(() => ShowMissingTagsOnly = !ShowMissingTagsOnly), Localizer),
            new FunctionKeyViewModel("Vk_RegisterTag", new RelayCommand(RegisterSelectedTag, () => SelectedTag is { IsMissing: true }), Localizer)
            {
                RequiredPermission = permission,
                PreconditionResourceKey = "Cfg_SelectMissingRow",
            },
            new FunctionKeyViewModel(
                "Vk_DeleteTag",
                new RelayCommand(
                    () => Ask("Cfg_AskDeleteTag", DeleteSelectedTag, SelectedTag?.Key),
                    () => SelectedTag is { IsMissing: false }),
                Localizer,
                FunctionKeyKind.Danger)
            {
                RequiredPermission = permission,
                PreconditionResourceKey = "Cfg_SelectMappedRow",
            },
            new FunctionKeyViewModel(
                "Vk_TestRead", new AsyncRelayCommand(() => TestReadSelectedTagAsync(CancellationToken.None), () => SelectedTag is not null), Localizer)
            {
                PreconditionResourceKey = "Lib_NothingSelected",
            },
            null,
            restore,
        };
    }

    /// <summary>当前组对应的文件（系统组没有）。</summary>
    private ConfigFileKind? CurrentFile => Group switch
    {
        MachineConfigGroup => ConfigFileKind.Machine,
        TagMappingGroup => ConfigFileKind.TagMap,
        _ => null,
    };

    /// <summary>"✓ 保存…"：先把改了几处说出来，确认才写文件。</summary>
    private void AskSaveConfig()
    {
        if (CurrentFile is not { } kind)
        {
            return;
        }

        JsonObject? working = kind == ConfigFileKind.Machine ? this.machineWorking : this.tagWorking;
        JsonObject? loaded = kind == ConfigFileKind.Machine ? this.machineLoaded : this.tagLoaded;
        int count = loaded is null ? 0 : ConfigDocumentStore.Diff(loaded, working).Count;
        Ask("Cfg_AskSaveFormat", () => SaveConfigAsync(kind, CancellationToken.None), count, kind == ConfigFileKind.Machine ? "machine.json" : "tagmap.json");
    }

    private async Task SaveConfigAsync(ConfigFileKind kind, CancellationToken cancellationToken)
    {
        Permission permission = kind == ConfigFileKind.Machine ? Permission.EditMachineConfig : Permission.EditTagMap;
        if (!Can(permission))
        {
            Interaction.Refuse(Localizer["Cfg_NeedsManufacturer"]);
            return;
        }

        JsonObject working = kind == ConfigFileKind.Machine ? this.machineWorking! : this.tagWorking!;
        JsonObject loaded = kind == ConfigFileKind.Machine ? this.machineLoaded! : this.tagLoaded!;
        if (ConfigIssueCount > 0 || ConfigDocumentStore.Validate(kind, working).Count > 0)
        {
            Interaction.Refuse(Localizer.Format("Cfg_IssueCountFormat", Math.Max(1, ConfigIssueCount)));
            return;
        }

        IReadOnlyList<ConfigDiff> diffs = ConfigDocumentStore.Diff(loaded, working);
        if (diffs.Count == 0)
        {
            Say("Cfg_NoChanges");
            return;
        }

        await RunGuardedAsync(async token =>
        {
            string backup = await this.configStore.SaveAsync(kind, working, token).ConfigureAwait(true);
            await this.changeLog.RecordAsync(
                kind == ConfigFileKind.Machine ? ChangeLogAreas.MachineConfig : ChangeLogAreas.TagMap,
                diffs.Select(diff => new ChangedItem(diff.Path, diff.OldValue, diff.NewValue)),
                this.userSession.CurrentUser?.UserName ?? string.Empty,
                token).ConfigureAwait(true);

            JsonObject saved = (JsonObject)working.DeepClone();
            if (kind == ConfigFileKind.Machine)
            {
                this.machineLoaded = saved;
            }
            else
            {
                this.tagLoaded = saved;
            }

            UpdateConfigDirty();
            Say("Cfg_SavedFormat", diffs.Count, Path.GetFileName(backup));
        }, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>恢复上一版：把最近一份备份载入编辑区（还没写回文件），看过没问题再按"保存"。</summary>
    private async Task RestorePreviousAsync(ConfigFileKind kind, CancellationToken cancellationToken)
    {
        await RunGuardedAsync(async token =>
        {
            JsonObject? previous = await this.configStore.LoadLatestBackupAsync(kind, token).ConfigureAwait(true);
            if (previous is null)
            {
                Interaction.Refuse(Localizer["Cfg_NoBackup"]);
                return;
            }

            if (kind == ConfigFileKind.Machine)
            {
                this.machineWorking = previous;
                BuildMachineGroups();
                RevalidateMachine();
            }
            else
            {
                this.tagWorking = previous;
                BuildTagRows();
                RevalidateTags();
            }

            UpdateConfigDirty();
            Say("Cfg_PreviousLoaded");
        }, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>放弃编辑区里的改动，回到文件里现在的样子。</summary>
    private void DiscardConfig(ConfigFileKind kind)
    {
        if (kind == ConfigFileKind.Machine && this.machineLoaded is not null)
        {
            this.machineWorking = (JsonObject)this.machineLoaded.DeepClone();
            BuildMachineGroups();
            RevalidateMachine();
        }
        else if (kind == ConfigFileKind.TagMap && this.tagLoaded is not null)
        {
            this.tagWorking = (JsonObject)this.tagLoaded.DeepClone();
            BuildTagRows();
            RevalidateTags();
        }

        UpdateConfigDirty();
    }

    public override void DiscardChanges()
    {
        DiscardConfig(ConfigFileKind.Machine);
        DiscardConfig(ConfigFileKind.TagMap);
        base.DiscardChanges();
    }

    private void UpdateConfigDirty()
    {
        bool machineDirty = this.machineLoaded is not null && ConfigDocumentStore.Diff(this.machineLoaded, this.machineWorking).Count > 0;
        bool tagDirty = this.tagLoaded is not null && ConfigDocumentStore.Diff(this.tagLoaded, this.tagWorking).Count > 0;
        if (machineDirty || tagDirty)
        {
            MarkDirty();
        }
        else
        {
            MarkClean();
        }
    }

    private void ShowGeneralIssues(IReadOnlyList<ConfigIssue> issues)
    {
        ConfigIssues.Clear();
        foreach (ConfigIssue issue in issues)
        {
            string reason = Localizer[issue.ReasonResourceKey];
            string where = issue.Path.Length == 0 ? string.Empty : issue.Path + "：";
            ConfigIssues.Add(issue.Detail is null ? where + reason : where + reason + " — " + issue.Detail);
        }
    }

    private string FieldLabel(string property)
    {
        string localized = Localizer["CfgField_" + property];
        return localized.StartsWith('!') ? property : localized;
    }

    /// <summary>单位按字段名的后缀认（文件里的命名约定：…Mm、…MmPerMin、…Rpm、…Ms…）。</summary>
    private static string UnitOf(string property)
    {
        (string Suffix, string Unit)[] units =
        {
            ("MmPerMin", "mm/min"), ("MPerSec", "m/s"), ("Micrometer", "µm"), ("Rpm", "r/min"),
            ("Ms", "ms"), ("Mm", "mm"), ("Kg", "kg"),
        };
        return units.FirstOrDefault(unit => property.EndsWith(unit.Suffix, StringComparison.Ordinal)).Unit ?? string.Empty;
    }

    private static string Leaf(JsonObject owner, string property) =>
        owner[property] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;
}
