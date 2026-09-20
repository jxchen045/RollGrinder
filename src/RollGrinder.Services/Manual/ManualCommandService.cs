using System;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Manual;

/// <summary>
/// 手动动作的执行者。
///
/// 三条规矩：
/// 1. tagmap 里没登记的动作直接返回 <see cref="ManualCommandOutcome.NotMapped"/>，
///    界面据此把按钮压暗——不装作按下去有效。
/// 2. 自动循环还挂着程序时，除了少数几个（冷却水）一律不发。这是防呆；
///    真正的联锁在 PLC，上位机只是不去按那个按钮。
/// 3. 脉冲型写 true → 等脉宽 → 写 false。**PLC 侧必须按上升沿触发并自行复位**：
///    上位机被强制结束时，那一句 false 就发不出去了（最高原则）。
/// </summary>
public sealed class ManualCommandService : IManualCommandService
{
    private readonly IMachineGateway gateway;
    private readonly IMachineMonitor monitor;
    private readonly ITagMap tagMap;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pulseWidth;

    public ManualCommandService(
        IMachineGateway gateway,
        IMachineMonitor monitor,
        ITagMap tagMap,
        HmiSettings settings,
        TimeProvider timeProvider)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(settings);

        this.pulseWidth = TimeSpan.FromMilliseconds(settings.ManualPulseMs);
    }

    /// <summary>tagmap 里没登记这个动作时报出来的资源键。</summary>
    public const string NotMappedResourceKey = "Alarm_ActionNeedsTagMapping";

    /// <summary>自动循环挂着程序时报出来的资源键。</summary>
    public const string ChannelBusyResourceKey = "Alarm_ActionBlockedWhileRunning";

    /// <summary>没连上机床时报出来的资源键。</summary>
    public const string DisconnectedResourceKey = "Alarm_ActionNeedsConnection";

    /// <summary>写机床失败时报出来的资源键。</summary>
    public const string WriteFailedResourceKey = "Alarm_ActionWriteFailed";

    public bool IsMapped(ManualCommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Kind == ManualCommandKind.Local
            || this.tagMap.TryResolve(MachineTagKeys.ManualCommand(command.Key), out _);
    }

    public ManualCommandResult CanExecute(ManualCommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Kind == ManualCommandKind.Local)
        {
            return ManualCommandResult.Sent;
        }

        if (!IsMapped(command))
        {
            return new ManualCommandResult(ManualCommandOutcome.NotMapped, NotMappedResourceKey);
        }

        MachineStateSnapshot snapshot = this.monitor.Current;
        if (snapshot.ConnectionState != GatewayConnectionState.Connected)
        {
            return new ManualCommandResult(ManualCommandOutcome.Disconnected, DisconnectedResourceKey);
        }

        if (command.RequiresIdleChannel && IsChannelBusy(snapshot))
        {
            return new ManualCommandResult(ManualCommandOutcome.ChannelBusy, ChannelBusyResourceKey);
        }

        return ManualCommandResult.Sent;
    }

    public async Task<ManualCommandResult> ExecuteAsync(
        ManualCommandDescriptor command,
        bool? desiredState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ManualCommandResult permission = CanExecute(command);
        if (!permission.Succeeded)
        {
            return permission;
        }

        if (command.Kind == ManualCommandKind.Local)
        {
            // 本地动作由调用方自己做，这里只负责放行。
            return ManualCommandResult.Sent;
        }

        string logicalName = MachineTagKeys.ManualCommand(command.Key);

        try
        {
            if (command.Kind == ManualCommandKind.Toggle)
            {
                bool target = desiredState ?? !(ReadState(command) ?? false);
                await WriteAsync(logicalName, target, cancellationToken).ConfigureAwait(false);
                return ManualCommandResult.Sent;
            }

            await WriteAsync(logicalName, true, cancellationToken).ConfigureAwait(false);
            await Task.Delay(this.pulseWidth, this.timeProvider, cancellationToken).ConfigureAwait(false);
            await WriteAsync(logicalName, false, cancellationToken).ConfigureAwait(false);
            return ManualCommandResult.Sent;
        }
        catch (OperationCanceledException)
        {
            // 取消发生在脉宽等待里：尽力把命令位清掉，别留一个按下去就不松的按钮。
            await TryClearAsync(logicalName).ConfigureAwait(false);
            throw;
        }
        catch (GatewayException)
        {
            await TryClearAsync(logicalName).ConfigureAwait(false);
            return new ManualCommandResult(ManualCommandOutcome.WriteFailed, WriteFailedResourceKey);
        }
    }

    public bool? ReadState(ManualCommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Kind != ManualCommandKind.Toggle
            ? null
            : this.monitor.Current.GetBooleanOrNull(MachineTagKeys.ManualCommandState(command.Key));
    }

    private static bool IsChannelBusy(MachineStateSnapshot snapshot)
    {
        double? channelState = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState);

        // 读不到通道状态时当作"在忙"：不知道机床在干什么，就别乱动它。
        return channelState is null || (NcChannelState)(int)channelState.Value != NcChannelState.Reset;
    }

    private Task WriteAsync(string logicalName, bool value, CancellationToken cancellationToken) =>
        this.gateway.WriteTagAsync(
            logicalName,
            new TagValue(logicalName, TagDataType.Boolean, value, this.timeProvider.GetUtcNow()),
            cancellationToken);

    private async Task TryClearAsync(string logicalName)
    {
        try
        {
            await WriteAsync(logicalName, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GatewayException)
        {
            // 清不掉也没别的办法了：PLC 的上升沿触发与自复位是最后一道保险。
        }
    }
}
