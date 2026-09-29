using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Interaction;
using RollGrinder.App.Localization;
using RollGrinder.Contracts;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Manual;
using RollGrinder.Services.Session;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 把手动目录里的动作做成竖键（最终稿 5.1、5.4），手动动作页与手动磨削页共用：
/// 1. 要确认的动作标签带"…"，按下后对话行提问、竖键 7 / 8 确认才发（取代"再按一次确认"）；
/// 2. 发出后对话行写"XXX · 已发出"，3 秒后消失；到位看状态灯；
/// 3. 按不了的键灰、留在原位：缺标签写"缺标签 manual.xxx"，没连上、机床在忙写原因；
/// 4. 保持型动作开着时青底；成对的（砂轮启动 ⇄ 停止）在同一格里换字，停止不问。
/// </summary>
public sealed class ManualActionKeys
{
    private readonly IManualCommandService commands;
    private readonly ShellInteraction interaction;
    private readonly IStringLocalizer localizer;
    private readonly IAlarmSink alarms;
    private readonly Func<CancellationToken, Task> localAction;
    private readonly List<(ManualCommandDescriptor Descriptor, FunctionKeyViewModel Key)> keys = new();

    /// <param name="localAction">本地动作（测量采样）按下时做什么：它不写机床。</param>
    public ManualActionKeys(
        IManualCommandService commands,
        ShellInteraction interaction,
        IStringLocalizer localizer,
        IAlarmSink alarms,
        Func<CancellationToken, Task> localAction)
    {
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.localAction = localAction ?? throw new ArgumentNullException(nameof(localAction));
    }

    /// <summary>成对动作"开着"时的标签（例如砂轮启动 → 砂轮停止）。</summary>
    private static readonly IReadOnlyDictionary<string, string> StopLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["wheel.run"] = "Action_WheelStop",
    };

    /// <summary>按动作键做一个竖键。</summary>
    public FunctionKeyViewModel Create(string actionKey)
    {
        ManualCommandDescriptor descriptor = ManualCommandCatalog.All
            .Single(command => string.Equals(command.Key, actionKey, StringComparison.Ordinal));
        FunctionKeyViewModel key = null!;
        key = new FunctionKeyViewModel(
            LabelKey(descriptor, isOn: false),
            new AsyncRelayCommand(() => PressAsync(descriptor, key)),
            this.localizer)
        {
            IsMachineCommand = descriptor.Kind != ManualCommandKind.Local,
            RequiredPermission = Permission.RunMachine,
            LabelArgument = LabelArgument(descriptor, isOn: false),
        };
        this.keys.Add((descriptor, key));
        return key;
    }

    /// <summary>
    /// 每一拍刷新：能不能按（给出原因）、保持型开没开、成对键换字。
    /// <paramref name="block"/> 是页面的阻断入口（PageViewModelBase.Block）。
    /// </summary>
    public void Refresh(Action<FunctionKeyViewModel, string?> block)
    {
        ArgumentNullException.ThrowIfNull(block);
        foreach ((ManualCommandDescriptor descriptor, FunctionKeyViewModel key) in this.keys)
        {
            ManualCommandResult permission = this.commands.CanExecute(descriptor);
            block(key, permission.Succeeded ? null : Reason(descriptor, permission));

            bool isOn = this.commands.ReadState(descriptor) ?? false;
            key.IsActive = isOn;
            string label = LabelKey(descriptor, isOn);
            if (key.LabelResourceKey != label)
            {
                key.LabelResourceKey = label;
            }

            key.LabelArgument = LabelArgument(descriptor, isOn);
        }
    }

    private string Reason(ManualCommandDescriptor descriptor, ManualCommandResult permission) =>
        permission.Outcome == ManualCommandOutcome.NotMapped
            ? this.localizer.Format("Key_MissingTagFormat", MachineTagKeys.ManualCommand(descriptor.Key))
            : this.localizer[permission.ReasonResourceKey!];

    /// <summary>要确认的动作标签带"…"（最终稿 4.4）；成对键开着时换成停止（停止不问，不带"…"）。</summary>
    private static string LabelKey(ManualCommandDescriptor descriptor, bool isOn)
    {
        if (isOn && StopLabels.TryGetValue(descriptor.Key, out string? stop))
        {
            return stop;
        }

        return descriptor.RequiresConfirmation ? "Action_AsksFormat" : descriptor.ResourceKey;
    }

    private string? LabelArgument(ManualCommandDescriptor descriptor, bool isOn) =>
        LabelKey(descriptor, isOn) == "Action_AsksFormat" ? this.localizer[descriptor.ResourceKey] : null;

    private async Task PressAsync(ManualCommandDescriptor descriptor, FunctionKeyViewModel key)
    {
        if (descriptor.Kind == ManualCommandKind.Local)
        {
            await this.localAction(CancellationToken.None).ConfigureAwait(true);
            return;
        }

        bool isOn = this.commands.ReadState(descriptor) ?? false;
        bool stopping = isOn && StopLabels.ContainsKey(descriptor.Key);
        string name = this.localizer[descriptor.ResourceKey];

        if (descriptor.RequiresConfirmation && !stopping)
        {
            this.interaction.Ask(
                this.localizer.Format("Manual_AskFormat", name),
                () => SendAsync(descriptor, name, desired: descriptor.Kind == ManualCommandKind.Toggle ? true : null));
            return;
        }

        await SendAsync(descriptor, name, desired: descriptor.Kind == ManualCommandKind.Toggle ? !isOn : null).ConfigureAwait(true);
    }

    private async Task SendAsync(ManualCommandDescriptor descriptor, string name, bool? desired)
    {
        try
        {
            ManualCommandResult result = await this.commands
                .ExecuteAsync(descriptor, desired, CancellationToken.None).ConfigureAwait(true);
            if (result.Succeeded)
            {
                this.interaction.Say(this.localizer.Format("Manual_CommandSentFormat", name));
            }
            else
            {
                this.alarms.Raise(AlarmSeverity.Warning, result.ReasonResourceKey!, name);
            }
        }
        catch (GatewayException ex)
        {
            this.alarms.RaiseException(ex);
        }
    }
}
