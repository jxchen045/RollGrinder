using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.App.Navigation;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Nc;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Audit;
using RollGrinder.Services.Monitoring;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 调试区（界面最终稿 5.13）：只从区域菜单进，给制造商装机、改映射用。横键 机床配置 · 标签映射 · 系统。
///
/// 所有人进得来都看得见（现场对着电话念值要用），只有制造商改得动（Q9）；权限不够时路径条写"只读 · 要制造商权限"。
/// 保存前自动备份到 config/backup，改动写入改动记录（诊断 › 改动记录），重启上位机后生效——
/// 这些文件决定机床怎么被读写，上位机不在运行中热换它们。
/// </summary>
public sealed partial class CommissioningViewModel : PageViewModelBase
{
    /// <summary>横键"机床配置"。</summary>
    public const string MachineConfigGroup = "machineConfig";

    /// <summary>横键"标签映射"。</summary>
    public const string TagMappingGroup = "tagMapping";

    /// <summary>横键"系统"。</summary>
    public const string SystemGroup = "system";

    private readonly MachineDescription machine;
    private readonly ITagMap tagMap;
    private readonly IMachineMonitor monitor;
    private readonly ConfigDocumentStore configStore;
    private readonly IChangeLog changeLog;
    private readonly IMachineGateway gateway;
    private readonly IUserSession userSession;
    private readonly HmiSettings settings;
    private readonly Dictionary<string, FunctionKeyViewModel> groupKeys = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<FunctionKeyViewModel?> systemKeys;
    private readonly FunctionKeyViewModel discardKey;
    private readonly FunctionKeyViewModel saveKey;

    public CommissioningViewModel(
        MachineDescription machine,
        ITagMap tagMap,
        IMachineMonitor monitor,
        ConfigDocumentStore configStore,
        IChangeLog changeLog,
        IMachineGateway gateway,
        IUserSession userSession,
        HmiSettings settings,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        this.changeLog = changeLog ?? throw new ArgumentNullException(nameof(changeLog));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.userSession = userSession ?? throw new ArgumentNullException(nameof(userSession));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

        foreach ((string group, string label) in new[]
        {
            (MachineConfigGroup, "Fn_MachineConfig"), (TagMappingGroup, "Fn_TagMapping"), (SystemGroup, "Fn_System"),
        })
        {
            string target = group;
            this.groupKeys[group] = FunctionKeyViewModel.ForAction(label, localizer, () => ShowGroup(target));
        }

        SetFunctionKeys(new FunctionKeyViewModel?[]
        {
            this.groupKeys[MachineConfigGroup], this.groupKeys[TagMappingGroup], this.groupKeys[SystemGroup],
        });

        this.systemKeys = new FunctionKeyViewModel?[]
        {
            new FunctionKeyViewModel("Vk_ViewMachineJson", new AsyncRelayCommand(() => ViewRawAsync(ConfigFileKind.Machine)), localizer),
            new FunctionKeyViewModel("Vk_ViewTagMapJson", new AsyncRelayCommand(() => ViewRawAsync(ConfigFileKind.TagMap)), localizer),
        };
        this.discardKey = new FunctionKeyViewModel(
            "Vk_DiscardEdits",
            new RelayCommand(() =>
            {
                if (CurrentFile is { } kind)
                {
                    DiscardConfig(kind);
                }
            }),
            localizer,
            FunctionKeyKind.Cancel);
        this.saveKey = new FunctionKeyViewModel("Vk_SaveAsk", new RelayCommand(AskSaveConfig), localizer, FunctionKeyKind.Confirm)
        {
            RequiredPermission = Permission.EditMachineConfig,
        };

        BuildSystemRows();
        PropertyChanged += OnConfigEditorPropertyChanged;
        ShowGroup(MachineConfigGroup);
    }

    public override PageKey Key => PageKey.Commissioning;

    public override string TitleResourceKey => "Page_Commissioning";

    /// <summary>离线可用：只和配置文件打交道（试读除外）。</summary>
    public override bool WorksOffline => true;

    /// <summary>改机床配置、标签映射归制造商（Q9）。</summary>
    public override Permission? EditPermission => Permission.EditMachineConfig;

    /// <summary>当前是哪一组。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMachineConfigGroup), nameof(IsTagMappingGroup), nameof(IsSystemGroup))]
    private string group = MachineConfigGroup;

    public bool IsMachineConfigGroup => Group == MachineConfigGroup;

    public bool IsTagMappingGroup => Group == TagMappingGroup;

    public bool IsSystemGroup => Group == SystemGroup;

    /// <summary>系统组：语言、刷新、左栏条目、按钮板动作等（只读，改在配置文件里）。</summary>
    public ObservableCollection<LabelValueViewModel> SystemRows { get; } = new();

    /// <summary>配置原文（只读查看）。</summary>
    [ObservableProperty]
    private string inspectorText = string.Empty;

    public override bool ShowGroup(string groupKey)
    {
        if (!this.groupKeys.TryGetValue(groupKey, out FunctionKeyViewModel? key))
        {
            return false;
        }

        if (IsDirty && groupKey != Group)
        {
            // 两份文件各有各的"保存"：带着一份没存的改动去看另一份，保存键就说不清存的是哪份。
            Interaction.Refuse(Localizer["Cfg_SaveOrDiscardFirst"]);
            return false;
        }

        Group = groupKey;
        MarkActiveFunctionKey(key);
        SetVerticalKeys(groupKey switch
        {
            MachineConfigGroup => EditorKeys(ConfigFileKind.Machine),
            TagMappingGroup => EditorKeys(ConfigFileKind.TagMap),
            _ => this.systemKeys,
        });
        ConfigStatusText = string.Empty;
        _ = groupKey switch
        {
            MachineConfigGroup => LoadMachineConfigAsync(CancellationToken.None),
            TagMappingGroup => LoadTagMappingAsync(CancellationToken.None),
            _ => Task.CompletedTask,
        };
        RefreshCommitPair();
        return true;
    }

    public override void OnActivated()
    {
        OnPropertyChanged(nameof(CanEditMachineConfig));
        OnPropertyChanged(nameof(CanEditTagMap));
    }

    public override void OnTick(DateTimeOffset nowUtc)
    {
        if (IsTagMappingGroup)
        {
            RefreshTagValues(this.monitor.Current);
        }
    }

    protected override void OnAccessChanged()
    {
        OnPropertyChanged(nameof(CanEditMachineConfig));
        OnPropertyChanged(nameof(CanEditTagMap));
    }

    private void RefreshCommitPair()
    {
        bool show = IsDirty && CurrentFile is not null;
        SetCommitPair(show ? this.discardKey : null, show ? this.saveKey : null);
    }

    private void BuildSystemRows()
    {
        IReadOnlyList<QuickBarEntry> quick = QuickBarCatalog.Resolve(this.machine.QuickBar, out _);
        SystemRows.Clear();
        SystemRows.Add(new LabelValueViewModel("Sys_Language", CultureInfo.GetCultureInfo(this.settings.Culture).NativeName, Localizer));
        SystemRows.Add(new LabelValueViewModel(
            "Sys_Refresh",
            string.Create(CultureInfo.InvariantCulture, $"{this.settings.PollIntervalMs} ms · {this.settings.UiRefreshHz} Hz"),
            Localizer));
        SystemRows.Add(new LabelValueViewModel(
            "Sys_QuickBar", string.Join(" · ", quick.Select(entry => Localizer[entry.LabelResourceKey])), Localizer));
        SystemRows.Add(new LabelValueViewModel(
            "Sys_PanelActions",
            this.machine.PanelActions is { Count: > 0 } actions ? string.Join(" · ", actions) : Localizer["Common_NotConfigured"],
            Localizer));
        SystemRows.Add(new LabelValueViewModel(
            "Sys_Machine", string.Create(CultureInfo.InvariantCulture, $"{this.machine.MachineId} · v{this.machine.SchemaVersion}"), Localizer));
        SystemRows.Add(new LabelValueViewModel(
            "Sys_TagCount", this.tagMap.Tags.Count.ToString(CultureInfo.InvariantCulture), Localizer));
    }

    /// <summary>配置原文只读查看：格式化后的 JSON。</summary>
    private Task ViewRawAsync(ConfigFileKind kind) =>
        RunGuardedAsync(
            async token =>
            {
                JsonObject document = await this.configStore.LoadAsync(kind, token).ConfigureAwait(true);
                InspectorText = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            },
            CancellationToken.None);
}
