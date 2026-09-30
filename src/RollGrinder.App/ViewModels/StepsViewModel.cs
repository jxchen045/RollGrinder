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
using RollGrinder.Core;
using RollGrinder.Core.Calibration;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Units;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Session;
using RollGrinder.Services.Calibration;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Jobs;

namespace RollGrinder.App.ViewModels;

/// <summary>工序列表里的一道工序，参数行由该工序类型的 schema 生成。</summary>
public sealed partial class StepRowViewModel : ObservableObject
{
    public StepRowViewModel(int order, IGrindingStepType stepType, ParameterSet parameters, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        ArgumentNullException.ThrowIfNull(localizer);

        this.order = order;
        StepType = stepType;
        StepTypeKey = stepType.Key;
        DisplayName = localizer["StepType_" + stepType.Key];
        Parameters = new ObservableCollection<ParameterRowViewModel>(
            stepType.Schema.Descriptors.Select(descriptor => new ParameterRowViewModel(
                descriptor,
                parameters.TryGet(descriptor.Key, out ParameterValue? value) && value is not null
                    ? value
                    : descriptor.DefaultValue,
                localizer)));

        foreach (ParameterRowViewModel row in Parameters)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ParameterRowViewModel.Text))
                {
                    RefreshApplicability();
                }
            };
        }

        RefreshApplicability();
    }

    /// <summary>
    /// 按依赖关系点亮/压暗参数格：变速幅度与周期只在开了变速时才有意义。
    ///
    /// 连续进给与周期进给**不在这里互斥**——两路分量可以同时给值（说明书的磨削实例
    /// 粗磨一列两者都非零），哪一路不用就填 0。
    ///
    /// 格子始终留在原位，只是按不动——键位不跳动，操作员的手不用重新找。
    /// </summary>
    private void RefreshApplicability()
    {
        string? variation = ValueOf(StepParameterKeys.SpeedVariationTarget);
        bool isVarying = variation is null || variation != SpeedVariationChoices.Off;
        SetApplicable(StepParameterKeys.SpeedVariationPercent, isVarying);
        SetApplicable(StepParameterKeys.SpeedVariationPeriodRevolutions, isVarying);
    }

    private string? ValueOf(string key) =>
        Parameters.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.Ordinal))?.Text;

    private void SetApplicable(string key, bool isApplicable)
    {
        ParameterRowViewModel? row = Parameters
            .FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        if (row is not null)
        {
            row.IsApplicable = isApplicable;
        }
    }

    [ObservableProperty]
    private int order;

    public string StepTypeKey { get; }

    public IGrindingStepType StepType { get; }

    /// <summary>开始、结束固定在首尾：没有上移、下移、删除按钮。</summary>
    public bool CanBeMoved => !ProgramFrame.IsFixed(StepTypeKey);

    public string DisplayName { get; }

    public ObservableCollection<ParameterRowViewModel> Parameters { get; }

    /// <summary>预计时长，例如"约 211 min"。参数改了要重算。</summary>
    [ObservableProperty]
    private string durationText = string.Empty;

    /// <summary>当前选中的工序：右栏显示它的简图与参数，竖向软键对它操作。</summary>
    [ObservableProperty]
    private bool isSelected;
}

/// <summary>
/// "插入工序"下拉里的一项。本台机床装不了的工序照样列出来，但标上"（未配置）"并禁掉——
/// 藏起来只会让人找不到，标出来才知道是机床没装，不是软件少做。
/// </summary>
public sealed class StepTypeOptionViewModel
{
    public StepTypeOptionViewModel(IGrindingStepType stepType, bool isAvailable, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        ArgumentNullException.ThrowIfNull(localizer);

        Key = stepType.Key;
        IsAvailable = isAvailable;
        RequiredOptionKey = stepType.RequiredOptionKey;
        SlotKey = stepType.SlotKey;
        SlotOrder = StepSlotKeys.OrderOf(stepType.SlotKey);
        SlotName = localizer[StepSlotKeys.ResourceKeyOf(stepType.SlotKey)];

        string name = localizer["StepType_" + stepType.Key];
        DisplayName = isAvailable ? name : name + localizer["Steps_StepTypeNotAvailable"];
    }

    public string Key { get; }

    /// <summary>界面上归到哪个工序槽。</summary>
    public string SlotKey { get; }

    /// <summary>槽的呈现顺序，就是实机屏幕上的顺序。</summary>
    public int SlotOrder { get; }

    /// <summary>槽名，下拉里的分组头写它。</summary>
    public string SlotName { get; }

    public string DisplayName { get; }

    /// <summary>本台机床能不能做这道工序。</summary>
    public bool IsAvailable { get; }

    /// <summary>缺的是哪一项装置；不需要装置时为 null。</summary>
    public string? RequiredOptionKey { get; }
}

/// <summary>
/// 程序步骤（自动磨削前取舍）里的一行。
/// 本台机床做不了的那几项压暗并禁掉，ToolTip 说明缺什么——
/// 藏起来只会让人以为软件少做，标出来才知道是机床没装。
/// </summary>
public sealed partial class ProgramOptionRowViewModel : ObservableObject
{
    public ProgramOptionRowViewModel(
        ProgramOptionDescriptor descriptor,
        bool isEnabled,
        bool isAvailable,
        IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(localizer);

        Descriptor = descriptor;
        Label = localizer[descriptor.ResourceKey];
        IsAvailable = isAvailable;
        UnavailableHint = isAvailable ? null : localizer["Steps_OptionNotAvailable"];

        // 机床做不了的项一律按"关"处理，免得存进程序里再到下发时被打回来。
        this.isOn = isEnabled && isAvailable;
    }

    public ProgramOptionDescriptor Descriptor { get; }

    public string Label { get; }

    /// <summary>本台机床做不做得了。</summary>
    public bool IsAvailable { get; }

    /// <summary>做不了时的说明。</summary>
    public string? UnavailableHint { get; }

    /// <summary>开关状态。</summary>
    [ObservableProperty]
    private bool isOn;
}

/// <summary>校验失败的一行，文案由原因与参数键组合而成。</summary>
public sealed class ViolationRowViewModel
{
    public ViolationRowViewModel(ParameterViolation violation, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(violation);
        ArgumentNullException.ThrowIfNull(localizer);

        // 违规项可能是一个参数，也可能是一整道工序（机床没装那个装置时）。
        // 两个命名空间都试一遍，都没有才退回原始键——界面上不留 "!Key!"。
        ParameterText = FirstLocalized(
            localizer,
            violation.ParameterKey,
            "Parameter_" + violation.ParameterKey,
            "StepType_" + violation.ParameterKey);
        ReasonText = violation.Limit is null
            ? localizer["Violation_" + violation.Kind]
            : localizer.Format("Violation_" + violation.Kind + "_WithLimit", violation.Limit.Value);
    }

    public string ParameterText { get; }

    public string ReasonText { get; }

    private static string FirstLocalized(IStringLocalizer localizer, string fallback, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            string text = localizer[candidate];
            if (!text.StartsWith('!'))
            {
                return text;
            }
        }

        return fallback;
    }
}

/// <summary>程序的一份快照，"放弃修改"用它回退。参数按界面文本原样存，回填时不做二次解析。</summary>
/// <param name="TypeKey">工序类型键。</param>
/// <param name="ParameterTexts">参数行文本，顺序与 schema 一致。</param>
internal sealed record StepSnapshot(string TypeKey, IReadOnlyList<string> ParameterTexts);

/// <summary>整支程序的快照。</summary>
internal sealed record StepsSnapshot(
    string? ProgramId,
    string ProgramName,
    IReadOnlyList<StepSnapshot> Steps,
    IReadOnlyList<bool> ProgramOptions)
{
    /// <summary>空快照：还没进过本页时用。</summary>
    public static StepsSnapshot Empty { get; } = new(
        null,
        string.Empty,
        Array.Empty<StepSnapshot>(),
        Array.Empty<bool>());
}

/// <summary>
/// 工艺程序（阶段 1，修改稿 5.3；原"工序编程"）：开始 → 若干工序 → 结束，每道的参数，
/// 程序步骤开关的默认值。不含辊号、尺寸、辊形——那些属于作业（作业页）与轧辊（台账）。
/// 界面按注册表与 schema 生成，新增一类工序不改这里。
/// </summary>
public sealed partial class StepsViewModel : PageViewModelBase
{
    private readonly GrindingStepTypeRegistry stepTypes;
    private readonly IProgramRepository programs;
    private readonly GrindingJobValidator validator;
    private readonly MachineCapability capability;
    private readonly MachineDescription machine;
    private readonly ICalibrationService calibration;
    private readonly JobDraft jobDraft;

    /// <summary>进入本页时的程序快照，供"放弃修改"回退。</summary>
    private StepsSnapshot committed = StepsSnapshot.Empty;

    /// <summary>回退期间不要把恢复动作本身算成修改。</summary>
    private bool suppressDirty;

    public StepsViewModel(
        GrindingStepTypeRegistry stepTypes,
        IProgramRepository programs,
        ICalibrationService calibration,
        JobDraft jobDraft,
        GrindingJobValidator validator,
        MachineDescription machine,
        MachineCapability capability,
        HmiSettings settings,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        INavigator navigator,
        ShellInteraction interaction)
        : base(alarms, localizer, navigator, interaction)
    {
        this.capability = capability ?? throw new ArgumentNullException(nameof(capability));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.stepTypes = stepTypes ?? throw new ArgumentNullException(nameof(stepTypes));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        this.jobDraft = jobDraft ?? throw new ArgumentNullException(nameof(jobDraft));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        NamePrompt = new NamePromptViewModel(localizer);
        AttachPrompt(NamePrompt);
        Violations.CollectionChanged += (_, _) => SetIssueCount(Violations.Count);

        // 按槽排序，让下拉里的分组按实机屏幕上的顺序出现——
        // 不排的话粗磨会跟在精磨后面，和操作工脑子里的顺序对不上。
        StepTypeOptions = new ObservableCollection<StepTypeOptionViewModel>(
            stepTypes.All
                .Select(type => new StepTypeOptionViewModel(type, capability.Supports(type), localizer))
                .OrderBy(option => option.SlotOrder));

        // 估算时长与存程序前的校验要一支参考辊来展开；程序本身不带辊。默认取机床的最大辊身与最大直径。
        this.bodyLengthMmText = machine.Workpiece.MaxBodyLengthMm.ToString("F0", CultureInfo.CurrentCulture);
        this.nominalDiameterMmText = machine.Workpiece.MaxDiameterMm.ToString("F0", CultureInfo.CurrentCulture);
        this.selectedStepType = StepTypeOptions.FirstOrDefault(option => option.IsAvailable && !ProgramFrame.IsFixed(option.Key))
            ?? StepTypeOptions.FirstOrDefault();

        ProgramOptions = new ObservableCollection<ProgramOptionRowViewModel>(
            ProgramOptionCatalog.All.Select(option => new ProgramOptionRowViewModel(
                option, option.DefaultEnabled, capability.Supports(option), localizer)));

        foreach (ProgramOptionRowViewModel row in ProgramOptions)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProgramOptionRowViewModel.IsOn))
                {
                    MarkEdited();
                }
            };
        }

        // 竖向软键：对选中的工序操作。插入工序 ▸ 先选类别、再选工序，插在选中工序之后（修改稿 5.3）。
        SetVerticalKeys(new[]
        {
            new FunctionKeyViewModel("Vk_InsertStep", new RelayCommand(OpenInsertStepMenu), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_DeleteStep", new RelayCommand(() => RemoveStep(SelectedStep)), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_MoveUp", new RelayCommand(() => MoveStepUp(SelectedStep)), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_MoveDown", new RelayCommand(() => MoveStepDown(SelectedStep)), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_CopyStep", new RelayCommand(() => CopyStep(SelectedStep)), localizer, requiresEditable: true),

            // 阶段 2 线框上的另外三个键（修改稿 5.3）：余量分配、默认值、程序步骤。
            new FunctionKeyViewModel("Vk_AllocateStock", new RelayCommand(AllocateStock), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_StepDefaults", new RelayCommand(() => Ask("Steps_AskDefaults", () => ResetStepToDefaults(SelectedStep))), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Vk_ProgramOptions", new RelayCommand(() => ProgramOptionsFocusRequested?.Invoke(this, EventArgs.Empty)), localizer),
        });

        Steps.CollectionChanged += (_, _) =>
        {
            if (SelectedStep is not null && !Steps.Contains(SelectedStep))
            {
                SelectedStep = null;
            }
        };

        // 横键（最终稿 5.8）：程序库 · 保存 · 另存为… · 空 · 新建程序 · 校验 · 空 · 用于作业。和辊形编辑同构（C5）。
        SetFunctionKeys(new FunctionKeyViewModel?[]
        {
            FunctionKeyViewModel.ForAction("Fn_ProgramLibrary", localizer, () => Navigator.GoToArea(AreaKey.Library, "programs")),
            new FunctionKeyViewModel("Fn_SaveProgram", new AsyncRelayCommand(
                () => SaveAsync(CancellationToken.None)), localizer, requiresEditable: true),
            new FunctionKeyViewModel("Fn_SaveAs", SaveProgramAsCommand, localizer, requiresEditable: true),
            null,
            new FunctionKeyViewModel("Fn_NewProgram", NewProgramCommand, localizer, requiresEditable: true),
            new FunctionKeyViewModel("Fn_Validate", ValidateCommand, localizer),
            null,

            // 用这支程序去拼一份作业：派到作业向导，路径条上"« 返回 工艺程序"。
            new FunctionKeyViewModel("Fn_UseForJob", UseForJobCommand, localizer),
        });

        ResetToEmptyProgram();
        Capture();
    }

    public override PageKey Key => PageKey.Steps;

    public override string TitleResourceKey => "Page_Steps";

    /// <summary>黄色帮助：工序参数（最终稿 F9）。</summary>
    public override string? HelpTopicKey => "Help_StepParameters";


    /// <summary>编辑页：自动循环挂着程序时落只读锁。</summary>
    public override bool LocksDuringRun => true;

    /// <summary>工艺程序库归管理员（Q9）。</summary>
    public override Permission? EditPermission => Permission.EditPrograms;

    /// <summary>离线可用：只和数据库与配置打交道，不碰机床。</summary>
    public override bool WorksOffline => true;

    public ObservableCollection<StepTypeOptionViewModel> StepTypeOptions { get; }

    public ObservableCollection<StepRowViewModel> Steps { get; } = new();

    public ObservableCollection<ViolationRowViewModel> Violations { get; } = new();

    /// <summary>程序步骤（自动磨削前取舍）的八个开关。</summary>
    public ObservableCollection<ProgramOptionRowViewModel> ProgramOptions { get; }

    [ObservableProperty]
    private string totalDurationText = "--";

    /// <summary>估算用的参考辊身长度（mm）。不属于程序，不存、不算修改。</summary>
    [ObservableProperty]
    private string bodyLengthMmText;

    /// <summary>估算用的参考直径（mm）。</summary>
    [ObservableProperty]
    private string nominalDiameterMmText;

    [ObservableProperty]
    private StepTypeOptionViewModel? selectedStepType;

    /// <summary>选中的工序：右栏只显示它（简图 + 参数），竖向软键删除 / 上移 / 下移 / 复制都对它。</summary>
    [ObservableProperty]
    private StepRowViewModel? selectedStep;

    partial void OnSelectedStepChanged(StepRowViewModel? value)
    {
        foreach (StepRowViewModel row in Steps)
        {
            row.IsSelected = ReferenceEquals(row, value);
        }

        // 换了一道工序：先亮它的第一个参数，简图与说明行不会空着。
        FocusedParameter = value?.Parameters.FirstOrDefault();
    }

    /// <summary>光标所在的参数：简图上亮它对应的量，说明行写它的含义、单位、范围（修改稿原则 2）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusedParameterKey))]
    [NotifyPropertyChangedFor(nameof(ParameterHelpText))]
    private ParameterRowViewModel? focusedParameter;

    /// <summary>简图要亮的参数键。</summary>
    public string FocusedParameterKey => FocusedParameter?.Key ?? string.Empty;

    /// <summary>简图下面那一行：参数名 — 说明　单位　范围。</summary>
    public string ParameterHelpText
    {
        get
        {
            if (FocusedParameter is not { } row)
            {
                return string.Empty;
            }

            var parts = new List<string> { Localizer.Format("Steps_HelpHeadFormat", row.Label, Localizer["ParamHelp_" + row.Key]) };
            if (row.UnitText.Length > 0)
            {
                parts.Add(Localizer.Format("Steps_HelpUnitFormat", row.UnitText));
            }

            if (row.RangeText.Length > 0)
            {
                parts.Add(Localizer.Format("Steps_HelpRangeFormat", row.RangeText));
            }

            return string.Join(Localizer["Steps_HelpSeparator"], parts);
        }
    }

    /// <summary>默认选中第一道真正的工序（没有就选"开始"）。换了一整支程序后调。</summary>
    private void SelectDefaultStep() =>
        SelectedStep = Steps.FirstOrDefault(step => !ProgramFrame.IsFixed(step.StepTypeKey)) ?? Steps.FirstOrDefault();

    /// <summary>"插入工序 ▸"：先选类别（磨削 ▸、测量 ▸），只有一种工序的类别直接插。</summary>
    private void OpenInsertStepMenu()
    {
        var byKey = StepTypeOptions.ToDictionary(option => option.Key, StringComparer.Ordinal);
        var items = new List<FunctionKeyViewModel>();
        foreach (StepCategory category in StepCategories.For(StepTypeOptions.Select(option => option.Key)))
        {
            StepTypeOptionViewModel[] options = category.StepTypeKeys.Select(key => byKey[key]).ToArray();
            if (options.Length == 1)
            {
                items.Add(InsertChoice(options[0]));
                continue;
            }

            items.Add(new FunctionKeyViewModel(
                category.LabelResourceKey,
                new RelayCommand(() => OpenVerticalMenu(category.LabelResourceKey, options.Select(InsertChoice))),
                Localizer,
                requiresEditable: true));
        }

        OpenVerticalMenu("Vk_InsertStepTitle", items);
    }

    /// <summary>子菜单里的一种工序。本机装不了的照样列出、置灰，悬停说明原因。</summary>
    private FunctionKeyViewModel InsertChoice(StepTypeOptionViewModel option)
    {
        FunctionKeyViewModel key = MenuChoice(
            "StepType_" + option.Key,
            () =>
            {
                SelectedStepType = option;
                AddStep();
            },
            canChoose: () => option.IsAvailable);
        key.Blocker = option.IsAvailable ? null : option.DisplayName;
        return key;
    }

    /// <summary>
    /// 新插一道工序的默认参数。砂轮修整照砂轮页设的修整参数填（修改稿 5.7）——
    /// 这台机床修砂轮的常用切深、道次、走刀速度在那里定一次，编程时不用每次重填。
    /// </summary>
    private ParameterSet DefaultsFor(IGrindingStepType stepType)
    {
        ParameterSet defaults = stepType.Schema.CreateDefaults();
        if (!string.Equals(stepType.Key, StepTypeKeys.WheelDress, StringComparison.Ordinal))
        {
            return defaults;
        }

        foreach (string key in new[]
        {
            CalibrationKeys.DressInfeedRadiusMicrometer, CalibrationKeys.DressPassCount, CalibrationKeys.DressFeedMmPerMin,
        })
        {
            if (this.calibration.Current.Values.TryGet(key, out ParameterValue? value) && value is not null)
            {
                defaults = defaults.With(key, value);
            }
        }

        return defaults;
    }

    /// <summary>复制选中的工序（连参数），接在它后面。开始 / 结束不复制。</summary>
    private void CopyStep(StepRowViewModel? step)
    {
        if (step is null)
        {
            return;
        }

        if (ProgramFrame.IsFixed(step.StepTypeKey))
        {
            StatusResourceKey = "Program_FrameFixed";
            return;
        }

        int position = Steps.IndexOf(step) + 1;
        var copy = new StepRowViewModel(position + 1, step.StepType, step.StepType.Schema.CreateDefaults(), Localizer);
        ApplyTexts(copy.Parameters, step.Parameters.Select(row => row.Text).ToArray());
        Steps.Insert(position, Track(copy));
        Renumber();
        RefreshDurations();
        MarkEdited();
        SelectedStep = copy;
    }

    /// <summary>Ctrl+C / X 记下的那一道：类型与各参数格的字（本页自己的剪贴板）。</summary>
    private (IGrindingStepType Type, string[] Texts)? clipboardStep;

    public override bool SupportsClipboard => true;

    /// <summary>
    /// Ctrl+C 复制选中工序、Ctrl+X 剪下、Ctrl+V 粘在选中工序之后（界面最终稿 4.6）。
    /// 开始 / 结束不能复制、剪切；粘贴总落在开始之后、结束之前。
    /// </summary>
    public override void Clipboard(ClipboardAction action)
    {
        if (action == ClipboardAction.Paste)
        {
            if (this.clipboardStep is not { } clip)
            {
                Interaction.Refuse(Localizer["Clip_Empty"]);
                return;
            }

            int position = SelectedStep is null ? Steps.Count : Steps.IndexOf(SelectedStep) + 1;
            if (Steps.Count > 0 && position >= Steps.Count && ProgramFrame.IsFixed(Steps[^1].StepTypeKey))
            {
                position = Steps.Count - 1;
            }

            if (Steps.Count > 0 && position == 0 && ProgramFrame.IsFixed(Steps[0].StepTypeKey))
            {
                position = 1;
            }

            var pasted = new StepRowViewModel(position + 1, clip.Type, clip.Type.Schema.CreateDefaults(), Localizer);
            ApplyTexts(pasted.Parameters, clip.Texts);
            Steps.Insert(position, Track(pasted));
            Renumber();
            RefreshDurations();
            MarkEdited();
            SelectedStep = pasted;
            Say("Clip_Pasted");
            return;
        }

        if (SelectedStep is not { } step)
        {
            Interaction.Refuse(Localizer["Clip_NothingSelected"]);
            return;
        }

        if (ProgramFrame.IsFixed(step.StepTypeKey))
        {
            StatusResourceKey = "Program_FrameFixed";
            return;
        }

        this.clipboardStep = (step.StepType, step.Parameters.Select(row => row.Text).ToArray());
        if (action == ClipboardAction.Cut)
        {
            RemoveStep(step);
            Say("Clip_Cut");
        }
        else
        {
            Say("Clip_Copied");
        }
    }

    [ObservableProperty]
    private string statusResourceKey = string.Empty;

    /// <summary>状态文字，按资源键取。</summary>
    public string StatusText => string.IsNullOrEmpty(StatusResourceKey) ? string.Empty : Localizer[StatusResourceKey];

    partial void OnStatusResourceKeyChanged(string value) => OnPropertyChanged(nameof(StatusText));

    /// <summary>
    /// 总余量（直径量 µm，估算用，和辊身、直径两格一样不存进程序）：填了就对账——
    /// 各道磨削量合计对不上时提示，"余量分配"按比例分到各道。
    /// </summary>
    [ObservableProperty]
    private string totalStockText = string.Empty;

    partial void OnTotalStockTextChanged(string value) => RefreshHints();

    /// <summary>跨工序检查的提示（修改稿 5.3）：不挡保存，工艺是人定的。</summary>
    public ObservableCollection<string> ProgramHints { get; } = new();

    /// <summary>"程序步骤"键：视图把键盘焦点移到程序步骤开关上（按键优先：Tab、回车就能切）。</summary>
    public event EventHandler? ProgramOptionsFocusRequested;

    /// <summary>重算跨工序提示。参数没填成立的那一道先跳过（它自己会报错）。</summary>
    private void RefreshHints()
    {
        ProgramHints.Clear();
        var steps = new List<GrindingJobStep>(Steps.Count);
        foreach (StepRowViewModel step in Steps)
        {
            if (Collect(step.Parameters) is ParameterSet parameters)
            {
                steps.Add(new GrindingJobStep(step.Order, step.StepTypeKey, parameters));
            }
        }

        double? total = TryParseDouble(TotalStockText, out double parsed) && parsed > 0.0 ? parsed : null;
        foreach (ProgramFinding finding in ProgramChecks.Find(steps, this.stepTypes, total))
        {
            ProgramHints.Add(finding.Kind switch
            {
                ProgramFindingKind.StockDoesNotAddUp => Localizer.Format("Program_HintStockFormat", finding.Value, finding.Reference),
                ProgramFindingKind.NoMeasurementAfterGrinding => Localizer.Format("Program_HintNoMeasurementFormat", finding.StepOrder ?? 0),
                _ => Localizer.Format("Program_HintStockReversedFormat", finding.StepOrder ?? 0, finding.Value, finding.Reference),
            });
        }
    }

    /// <summary>
    /// 余量分配：把"总余量"按各道现在的比例分到去量的工序上（全是 0 就按粗多精少的常用比例），合计正好等于总余量。
    /// </summary>
    private void AllocateStock()
    {
        if (!TryParseDouble(TotalStockText, out double total) || total <= 0.0)
        {
            StatusResourceKey = "Program_AllocateNeedsTotal";
            return;
        }

        var targets = new List<(StepRowViewModel Row, ParameterRowViewModel Stock)>();
        foreach (StepRowViewModel step in Steps)
        {
            ParameterRowViewModel? stock = step.Parameters.FirstOrDefault(row => row.Key == StepParameterKeys.StockDiameterMicrometer);
            if (stock is not null
                && Collect(step.Parameters) is ParameterSet parameters
                && ProgramChecks.RemovesStock(new GrindingJobStep(step.Order, step.StepTypeKey, parameters), this.stepTypes))
            {
                targets.Add((step, stock));
            }
        }

        if (targets.Count == 0)
        {
            StatusResourceKey = "Program_AllocateNoGrinding";
            return;
        }

        IReadOnlyList<double> shares = ProgramChecks.Distribute(
            targets.Select(target => TryParseDouble(target.Stock.Text, out double value) ? value : 0.0).ToArray(),
            targets.Select(target => target.Row.StepTypeKey).ToArray(),
            total);
        for (int i = 0; i < targets.Count; i++)
        {
            targets[i].Stock.Text = shares[i].ToString("0.#", CultureInfo.InvariantCulture);
        }

        StatusResourceKey = "Program_StockAllocated";
        RefreshDurations();
    }

    /// <summary>默认值：选中这一道的参数全部回到默认（砂轮修整回到砂轮页的修整参数）。</summary>
    private void ResetStepToDefaults(StepRowViewModel? step)
    {
        if (step is null)
        {
            return;
        }

        ParameterSet defaults = DefaultsFor(step.StepType);
        foreach (ParameterRowViewModel row in step.Parameters)
        {
            if (defaults.TryGet(row.Key, out ParameterValue? value) && value is not null)
            {
                row.Text = value.ToInvariantString();
            }
        }

        StatusResourceKey = "Program_StepDefaultsRestored";
        RefreshDurations();
    }

    partial void OnBodyLengthMmTextChanged(string value) => RefreshDurations();

    partial void OnNominalDiameterMmTextChanged(string value) => RefreshDurations();

    [RelayCommand]
    private void AddStep()
    {
        if (SelectedStepType is null)
        {
            return;
        }

        if (!SelectedStepType.IsAvailable)
        {
            // 机床没装这道工序要用的装置：当场说清楚，而不是让人编完、下发时才被打回来。
            Alarms.Raise(
                AlarmSeverity.Warning,
                "Alarm_StepTypeNotAvailable",
                Localizer["StepType_" + SelectedStepType.Key]);
            return;
        }

        if (ProgramFrame.IsFixed(SelectedStepType.Key))
        {
            // 开始、结束固定在首尾，各一个，已经在了。
            StatusResourceKey = "Program_FrameFixed";
            return;
        }

        // 插在选中工序之后；没选、或选的是"结束"，就插在"结束"前面——结束永远是最后一道。
        IGrindingStepType stepType = this.stepTypes.Get(SelectedStepType.Key);
        int endPosition = Steps.Count > 0 && Steps[^1].StepTypeKey == StepTypeKeys.End ? Steps.Count - 1 : Steps.Count;
        int selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        int position = selectedIndex >= 0 && selectedIndex < endPosition ? selectedIndex + 1 : endPosition;
        var added = new StepRowViewModel(position + 1, stepType, DefaultsFor(stepType), Localizer);
        Steps.Insert(position, Track(added));
        Renumber();
        RefreshDurations();
        MarkEdited();
        SelectedStep = added;
    }

    [RelayCommand]
    private void RemoveStep(StepRowViewModel? step)
    {
        if (step is null)
        {
            return;
        }

        if (ProgramFrame.IsFixed(step.StepTypeKey))
        {
            StatusResourceKey = "Program_FrameFixed";
            return;
        }

        int index = Steps.IndexOf(step);
        Steps.Remove(step);
        Renumber();
        MarkEdited();

        // 选中留在原位置，连着按"删除"能一道道删（首尾不会被选去删）。
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Min(index, Steps.Count - 1)];

        RefreshDurations();
    }

    /// <summary>这道工序往前挪一位（第一轮甲方测试：只能删，不能调顺序）。</summary>
    [RelayCommand]
    private void MoveStepUp(StepRowViewModel? step) => MoveStep(step, -1);

    /// <summary>这道工序往后挪一位。</summary>
    [RelayCommand]
    private void MoveStepDown(StepRowViewModel? step) => MoveStep(step, +1);

    private void MoveStep(StepRowViewModel? step, int offset)
    {
        int from = step is null ? -1 : Steps.IndexOf(step);
        int to = from + offset;
        if (from < 0 || to < 0 || to >= Steps.Count)
        {
            return;
        }

        // 开始、结束不挪，别的也挪不过它们。
        if (ProgramFrame.IsFixed(Steps[from].StepTypeKey) || ProgramFrame.IsFixed(Steps[to].StepTypeKey))
        {
            StatusResourceKey = "Program_FrameFixed";
            return;
        }

        Steps.Move(from, to);
        Renumber();
        MarkEdited();
        RefreshDurations();
    }

    private void Renumber()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].Order = i + 1;
        }
    }

    /// <summary>新建一支程序：只有开始与结束，开关回到默认。</summary>
    [RelayCommand]
    private void NewProgram()
    {
        ResetToEmptyProgram();
        MarkEdited();
    }

    private void ResetToEmptyProgram()
    {
        this.suppressDirty = true;
        try
        {
            ProgramId = null;
            ProgramName = string.Empty;
            Steps.Clear();
            foreach (GrindingJobStep step in ProgramFrame.Normalize(Array.Empty<GrindingJobStep>(), this.stepTypes))
            {
                Steps.Add(Track(new StepRowViewModel(step.Order, this.stepTypes.Get(step.StepTypeKey), step.Parameters, Localizer)));
            }

            foreach (ProgramOptionRowViewModel row in ProgramOptions)
            {
                row.IsOn = row.Descriptor.DefaultEnabled && row.IsAvailable;
            }

            Violations.Clear();
            StatusResourceKey = string.Empty;
        }
        finally
        {
            this.suppressDirty = false;
        }

        RefreshDurations();
        SelectDefaultStep();
    }

    /// <summary>校验这支程序：每道参数、机床能力、至少一道走拖板（与存程序、下发前同一套）。</summary>
    [RelayCommand]
    private void Validate()
    {
        RefreshDurations();
        if (CheckProgram() is not null)
        {
            StatusResourceKey = "Program_Valid";
        }
    }

    /// <summary>
    /// 用这支程序拼一份作业。程序得先存进库里——作业引用的是库里那一支，
    /// 没存或改了没存，作业里用的就不是眼前这一份了。
    /// </summary>
    [RelayCommand]
    private void UseForJob()
    {
        if (ProgramId is null || IsDirty)
        {
            StatusResourceKey = "Program_SaveBeforeUse";
            return;
        }

        this.jobDraft.PendingProgramId = ProgramId;
        Navigator.StartTask(PageKey.Job, PageKey.Steps);
    }

    // ── 程序库 ────────────────────────────────────────────────────────────────
    //
    // 程序是**可复用的模板**，作业只是"这支辊用哪条辊形、哪支程序"。
    // 作业引用它的时候复制一份快照，库里之后改了不会动已经磨过的那支辊的记录。

    /// <summary>当前程序的名字，库里按这个名字找。</summary>
    [ObservableProperty]
    private string programName = string.Empty;

    /// <summary>当前程序在库里的标识；还没存过就是 null。</summary>
    [ObservableProperty]
    private string? programId;

    partial void OnProgramNameChanged(string value)
    {
        MarkEdited();
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>有名字才谈得上保存——没名字存进库里就找不回来了。</summary>
    public override bool CanSave => !IsRoleLocked && !string.IsNullOrWhiteSpace(ProgramName);

    /// <summary>
    /// 把当前这支程序存回程序库。走页面基类的保存契约，
    /// 所以"改了没存就想离开"那道拦截也会用到它。
    /// </summary>
    public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        string name = ProgramName.Trim();
        string targetId = ProgramId ?? NewProgramId();
        if (name.Length > 0)
        {
            if (CheckProgram() is null)
            {
                return false;
            }

            try
            {
                // 改了名字撞上库里另一支：不悄悄存成两支同名的，问一句"覆盖 / 改名 / 取消"。
                if (await this.programs.FindIdByNameAsync(name, targetId, cancellationToken).ConfigureAwait(true) is not null)
                {
                    NamePrompt.Open(
                        Localizer["Library_SaveTitle"],
                        name,
                        (chosen, overwrite, token) => SaveUnderNameAsync(chosen, targetId, overwrite, token),
                        Localizer.Format("Library_NameTakenFormat", name));
                    return false;
                }
            }
            catch (DataStoreException ex)
            {
                Alarms.RaiseException(ex);
                return false;
            }
        }

        return await StoreProgramAsync(targetId, name, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>起名字的框（另存为、保存时撞名）。</summary>
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

    /// <summary>
    /// 另存一支新程序，库里原来那支不动。先校验——有错就不必起名字了；
    /// 再起名字：预填"原名-副本"，名字在库里已经有了，由操作员选覆盖、改名或取消。
    /// </summary>
    [RelayCommand]
    private void SaveProgramAs()
    {
        if (CheckProgram() is null)
        {
            return;
        }

        NamePrompt.Open(
            Localizer["Program_SaveAsTitle"],
            string.IsNullOrWhiteSpace(ProgramName) ? string.Empty : Localizer.Format("Library_CopyNameFormat", ProgramName.Trim()),
            (name, overwrite, token) => SaveUnderNameAsync(name, NewProgramId(), overwrite, token));
    }

    /// <summary>按指定名字存；名字被库里另一支占了，要么覆盖那一支（存进它的标识），要么退回去让人改名。</summary>
    private async Task<NamePromptOutcome> SaveUnderNameAsync(
        string name, string targetId, bool overwrite, CancellationToken cancellationToken)
    {
        try
        {
            string? holder = await this.programs.FindIdByNameAsync(name, targetId, cancellationToken).ConfigureAwait(true);
            if (holder is not null)
            {
                if (!overwrite)
                {
                    return NamePromptOutcome.Conflict(Localizer.Format("Library_NameTakenFormat", name));
                }

                targetId = holder;
            }
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
            return NamePromptOutcome.Refused(Localizer["Library_SaveFailed"]);
        }

        return await StoreProgramAsync(targetId, name, cancellationToken).ConfigureAwait(true)
            ? NamePromptOutcome.Done
            : NamePromptOutcome.Refused(Localizer["Library_SaveFailed"]);
    }

    /// <summary>
    /// 存程序前的校验（第一轮甲方测试 工序 2⑥）：每道参数成立、在范围内、机床装得了、
    /// 展开后不超机床能力、至少一道走拖板。不过就不存，原因列在页面的校验结果里。
    /// 程序不带辊，按本页填的辊身与直径展开；那两格没填成立时按机床允许的最小辊展开。
    /// </summary>
    /// <returns>校验通过的工序；不通过返回 null。</returns>
    private List<GrindingJobStep>? CheckProgram()
    {
        Violations.Clear();
        var steps = new List<GrindingJobStep>(Steps.Count);
        foreach (StepRowViewModel step in Steps)
        {
            ParameterSet? stepParameters = Collect(step.Parameters);
            if (stepParameters is null)
            {
                StatusResourceKey = "Job_ParametersInvalid";
                Alarms.Raise(AlarmSeverity.Warning, "Program_HasErrors", code: AlarmCodes.DomainFailure);
                return null;
            }

            steps.Add(new GrindingJobStep(step.Order, step.StepTypeKey, stepParameters));
        }

        if (steps.Count == 0)
        {
            StatusResourceKey = "Job_NoSteps";
            Alarms.Raise(AlarmSeverity.Warning, "Program_HasErrors", code: AlarmCodes.DomainFailure);
            return null;
        }

        RollGeometry reference =
            TryParseDouble(BodyLengthMmText, out double bodyLengthMm) && bodyLengthMm > 0.0
            && TryParseDouble(NominalDiameterMmText, out double diameterMm) && diameterMm > 0.0
                ? RollGeometry.FromDiameter(bodyLengthMm, diameterMm)
                : RollGeometry.FromDiameter(this.machine.Workpiece.MinBodyLengthMm, this.machine.Workpiece.MinDiameterMm);

        ParameterValidationResult result = this.validator.ValidateSteps(steps, reference, this.capability);
        if (!result.IsValid)
        {
            foreach (ParameterViolation violation in result.Violations)
            {
                Violations.Add(new ViolationRowViewModel(violation, Localizer));
            }

            StatusResourceKey = "Program_ValidationFailed";
            Alarms.Raise(AlarmSeverity.Warning, "Program_HasErrors", code: AlarmCodes.DomainFailure);
            return null;
        }

        return steps;
    }

    private async Task<bool> StoreProgramAsync(string programId, string name, CancellationToken cancellationToken)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Alarms.Raise(AlarmSeverity.Warning, "Program_NeedsName", code: AlarmCodes.DomainFailure);
            return false;
        }

        List<GrindingJobStep>? steps = CheckProgram();
        if (steps is null)
        {
            return false;
        }

        try
        {
            // 库里名字唯一：同名的两支分不清哪支是哪支（第一轮甲方测试）。兜底，正常走不到这里。
            if (await this.programs.FindIdByNameAsync(name, programId, cancellationToken).ConfigureAwait(true) is not null)
            {
                Alarms.Raise(AlarmSeverity.Warning, "Library_NameTaken", name, AlarmCodes.DomainFailure);
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            GrindingProgram? existing = await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(true);

            await this.programs.SaveAsync(
                GrindingProgram.Create(programId, name, steps, existing?.CreatedAtUtc ?? now, CollectProgramOptions())
                    with { ModifiedAtUtc = now },
                cancellationToken).ConfigureAwait(true);

            ProgramId = programId;
            ProgramName = name;
            Capture();
            IsDirty = false;
            StatusResourceKey = "Program_SavedStatus";
            Alarms.Raise(AlarmSeverity.Information, "Program_Saved", ProgramName, AlarmCodes.HandoverCompleted);
            return true;
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
            return false;
        }
        catch (DomainException ex)
        {
            Alarms.RaiseException(ex);
            return false;
        }
    }

    /// <summary>把库里一支程序调进编辑器（库区"打开"、作业向导"打开程序"都走这里），整串工序与开关一起换掉。</summary>
    private async Task OpenFromLibraryAsync(string programId, CancellationToken cancellationToken)
    {
        try
        {
            GrindingProgram? program = await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(true);
            if (program is not null)
            {
                ApplyProgram(program);
            }
        }
        catch (DataStoreException ex)
        {
            Alarms.RaiseException(ex);
        }
    }

    private void ApplyProgram(GrindingProgram program)
    {
        bool framed;
        this.suppressDirty = true;
        try
        {
            ProgramId = program.ProgramId;
            ProgramName = program.Name;

            // 以前存的程序可能没有开始 / 结束，或者不在首尾：整理成"开始 … 结束"，并提示存一次。
            framed = ProgramFrame.IsNormalized(program.Steps);
            Steps.Clear();
            foreach (GrindingJobStep step in ProgramFrame.Normalize(program.Steps, this.stepTypes))
            {
                IGrindingStepType stepType = this.stepTypes.Get(step.StepTypeKey);
                Steps.Add(Track(new StepRowViewModel(step.Order, stepType, step.Parameters, Localizer)));
            }

            foreach (ProgramOptionRowViewModel row in ProgramOptions)
            {
                row.IsOn = program.IsProgramOptionEnabled(row.Descriptor.Key);
            }
        }
        finally
        {
            this.suppressDirty = false;
        }

        Capture();
        IsDirty = !framed;
        StatusResourceKey = framed ? string.Empty : "Program_FrameAdded";
        Violations.Clear();
        RefreshDurations();
        SelectDefaultStep();
    }

    /// <summary>新条目的标识。精确到毫秒：只到秒的话，一秒内另存两次会悄悄盖掉前一支。</summary>
    private static string NewProgramId() =>
        string.Create(CultureInfo.InvariantCulture, $"G{DateTimeOffset.Now:yyyyMMddHHmmssfff}");

    private ParameterSet CollectProgramOptions() => new(ProgramOptions.Select(row =>
        new KeyValuePair<string, ParameterValue>(
            row.Descriptor.Key, ParameterValue.FromBoolean(row.IsOn))));

    /// <summary>切到本页时记住当前程序，"放弃修改"才有东西可回；从库区或作业向导"打开"过来的先调进来。</summary>
    public override void OnActivated()
    {
        Capture();
        if (this.jobDraft.ProgramToOpen is { } id)
        {
            this.jobDraft.ProgramToOpen = null;
            if (id == JobDraft.NewEntry)
            {
                NewProgram();
            }
            else
            {
                _ = RunGuardedAsync(token => OpenFromLibraryAsync(id, token), CancellationToken.None);
            }
        }
    }

    /// <summary>放弃修改：回到进入本页（或上次下发成功）时的程序。</summary>
    public override void DiscardChanges()
    {
        Restore(this.committed);
        base.DiscardChanges();
    }

    /// <summary>把当前程序存成"干净"版本。</summary>
    private void Capture() => this.committed = new StepsSnapshot(
        ProgramId,
        ProgramName,
        Steps.Select(step => new StepSnapshot(
            step.StepTypeKey,
            step.Parameters.Select(row => row.Text).ToArray())).ToArray(),
        ProgramOptions.Select(row => row.IsOn).ToArray());

    private void Restore(StepsSnapshot snapshot)
    {
        this.suppressDirty = true;
        try
        {
            ProgramId = snapshot.ProgramId;
            ProgramName = snapshot.ProgramName;

            Steps.Clear();
            for (int i = 0; i < snapshot.Steps.Count; i++)
            {
                StepSnapshot stepSnapshot = snapshot.Steps[i];
                IGrindingStepType stepType = this.stepTypes.Get(stepSnapshot.TypeKey);
                var row = new StepRowViewModel(i + 1, stepType, stepType.Schema.CreateDefaults(), Localizer);
                ApplyTexts(row.Parameters, stepSnapshot.ParameterTexts);
                Steps.Add(Track(row));
            }

            int optionCount = Math.Min(ProgramOptions.Count, snapshot.ProgramOptions.Count);
            for (int i = 0; i < optionCount; i++)
            {
                ProgramOptions[i].IsOn = snapshot.ProgramOptions[i];
            }

            Violations.Clear();
            StatusResourceKey = string.Empty;
            RefreshDurations();
        }
        finally
        {
            this.suppressDirty = false;
        }

        SelectDefaultStep();
    }

    private static void ApplyTexts(IList<ParameterRowViewModel> rows, IReadOnlyList<string> texts)
    {
        int count = Math.Min(rows.Count, texts.Count);
        for (int i = 0; i < count; i++)
        {
            rows[i].Text = texts[i];
        }
    }

    /// <summary>参数行的文本一改就算改了程序。</summary>
    private ParameterRowViewModel Track(ParameterRowViewModel row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ParameterRowViewModel.Text))
            {
                MarkEdited();
            }
        };

        return row;
    }

    private StepRowViewModel Track(StepRowViewModel row)
    {
        foreach (ParameterRowViewModel parameter in row.Parameters)
        {
            Track(parameter);
        }

        return row;
    }

    /// <summary>程序被改动：打脏标记，并把"已下发"状态清掉——界面与机床已经不一致了。</summary>
    private void MarkEdited()
    {
        RefreshHints();
        if (this.suppressDirty)
        {
            return;
        }

        MarkDirty();
    }

    private static ParameterSet? Collect(IEnumerable<ParameterRowViewModel> rows)
    {
        var values = new List<KeyValuePair<string, ParameterValue>>();
        foreach (ParameterRowViewModel row in rows)
        {
            ParameterValue? value = row.ToParameterValue();
            if (value is null)
            {
                return null;
            }

            values.Add(new KeyValuePair<string, ParameterValue>(row.Key, value));
        }

        return new ParameterSet(values);
    }

    /// <summary>按当前参数重算每道工序与总的预计时长。</summary>
    private void RefreshDurations()
    {
        RefreshHints();
        if (!TryParseDouble(BodyLengthMmText, out double bodyLengthMm)
            || !TryParseDouble(NominalDiameterMmText, out double nominalDiameterMm))
        {
            TotalDurationText = "--";
            return;
        }

        RollGeometry geometry;
        try
        {
            geometry = RollGeometry.FromDiameter(bodyLengthMm, nominalDiameterMm);
        }
        catch (DomainException)
        {
            TotalDurationText = "--";
            return;
        }

        TimeSpan total = TimeSpan.Zero;
        foreach (StepRowViewModel step in Steps)
        {
            ParameterSet? parameters = Collect(step.Parameters);
            if (parameters is null)
            {
                step.DurationText = string.Empty;
                continue;
            }

            try
            {
                TimeSpan duration = step.StepType.CreatePlan(geometry, parameters).EstimateDuration(geometry);
                total += duration;
                step.DurationText = duration > TimeSpan.Zero
                    ? Localizer.Format("Auto_StepDurationFormat", (int)duration.TotalMinutes)
                    : string.Empty;
            }
            catch (DomainException)
            {
                step.DurationText = string.Empty;
            }
        }

        TotalDurationText = Localizer.Format("Steps_TotalTimeFormat", (int)total.TotalMinutes);
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string NewJobId() =>
        string.Create(CultureInfo.InvariantCulture, $"J{DateTimeOffset.Now:yyyyMMddHHmmss}");
}
