using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Manual;

/// <summary>NC 方式请求（machine.modeRequest）。取值与 PLC 约定：1 JOG、2 AUTO。</summary>
public enum MachineModeRequest
{
    /// <summary>手动。进手动磨削时请求。</summary>
    Jog = 1,

    /// <summary>自动。下发作业后请求。</summary>
    Auto = 2,
}

/// <summary>定位循环能走的轴（manual.position.axis）。</summary>
public enum PositioningAxis
{
    /// <summary>拖板 Z：到某个 Z、到辊身 ¼ / ½ / ¾。</summary>
    CarriageZ = 1,

    /// <summary>测量架 X1：到轧辊、归位。</summary>
    MeasuringCarriageX1 = 2,

    /// <summary>磨架 X：退到安全位。</summary>
    InfeedX = 3,
}

/// <summary>
/// 手动磨削页（界面最终稿 5.1、7.2）的执行入口：给定、倍率、拖板往复、带启动装置、定位循环、方式请求。
///
/// 和 <see cref="IManualCommandService"/> 同一套规矩：
/// 1. tagmap 没登记的直接返回 <see cref="ManualCommandOutcome.NotMapped"/>，界面把键压暗并写"缺标签 xxx"；
/// 2. 没连上机床不发；会让机床动的（往复、定位）在自动循环挂着程序时不发；
/// 3. 全是"命令位 + 参数"的请求：参数先写、再给一个脉冲，PLC / NC 按上升沿接手并自复位；
///    上位机被强制结束，已经在转的砂轮、在走的拖板保持原状，由操作者在按钮板上停（最高原则）。
/// 4. 数值范围在这里再查一遍（界面已经挡过一次），PLC 侧还有自己的限幅。
/// </summary>
public interface IManualGrindingService
{
    /// <summary>这几个逻辑名里第一个没在 tagmap 登记的；都登记了返回 null。</summary>
    string? FirstUnmapped(params string[] logicalNames);

    /// <summary>写一个给定或倍率（double 或 int 按 tagmap 的类型写）。</summary>
    Task<ManualCommandResult> WriteValueAsync(string logicalName, double value, CancellationToken cancellationToken);

    /// <summary>带启动装置（保持型）。</summary>
    Task<ManualCommandResult> SetHeadstockAssistAsync(bool on, CancellationToken cancellationToken);

    /// <summary>拖板往复：写速度与行程，再给启动脉冲。</summary>
    Task<ManualCommandResult> StartReciprocationAsync(
        double speedMmPerMin, double strokeStartMm, double strokeEndMm, CancellationToken cancellationToken);

    /// <summary>拖板停止：只给停止脉冲。停止不看通道状态——往安全那一侧走的命令总是发。</summary>
    Task<ManualCommandResult> StopReciprocationAsync(CancellationToken cancellationToken);

    /// <summary>定位循环：写轴、目标、速度，再给启动脉冲（NC 提供循环，界面最终稿 M4）。</summary>
    Task<ManualCommandResult> StartPositioningAsync(
        PositioningAxis axis, double targetMm, double speedMmPerMin, CancellationToken cancellationToken);

    /// <summary>请求 NC 方式（界面最终稿 M7）：PLC 决定切不切。</summary>
    Task<ManualCommandResult> RequestModeAsync(MachineModeRequest mode, CancellationToken cancellationToken);
}

/// <summary>默认实现。</summary>
public sealed class ManualGrindingService : IManualGrindingService
{
    private readonly IMachineGateway gateway;
    private readonly IMachineMonitor monitor;
    private readonly ITagMap tagMap;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pulseWidth;

    public ManualGrindingService(
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

    /// <summary>数值不在允许范围里时的资源键。</summary>
    public const string OutOfRangeResourceKey = "Alarm_ValueOutOfRange";

    public string? FirstUnmapped(params string[] logicalNames)
    {
        ArgumentNullException.ThrowIfNull(logicalNames);
        return logicalNames.FirstOrDefault(name => !this.tagMap.TryResolve(name, out _));
    }

    public async Task<ManualCommandResult> WriteValueAsync(string logicalName, double value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(logicalName);
        if (!double.IsFinite(value))
        {
            return new ManualCommandResult(ManualCommandOutcome.WriteFailed, OutOfRangeResourceKey);
        }

        ManualCommandResult permission = Check(requiresIdleChannel: false, logicalName);
        if (!permission.Succeeded)
        {
            return permission;
        }

        return await TryAsync(() => WriteNumberAsync(logicalName, value, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ManualCommandResult> SetHeadstockAssistAsync(bool on, CancellationToken cancellationToken)
    {
        ManualCommandResult permission = Check(requiresIdleChannel: false, MachineTagKeys.ManualHeadstockAssist);
        if (!permission.Succeeded)
        {
            return permission;
        }

        return await TryAsync(() => WriteBooleanAsync(MachineTagKeys.ManualHeadstockAssist, on, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ManualCommandResult> StartReciprocationAsync(
        double speedMmPerMin, double strokeStartMm, double strokeEndMm, CancellationToken cancellationToken)
    {
        if (!(speedMmPerMin > 0) || !double.IsFinite(strokeStartMm) || !double.IsFinite(strokeEndMm) || strokeEndMm <= strokeStartMm)
        {
            return new ManualCommandResult(ManualCommandOutcome.WriteFailed, OutOfRangeResourceKey);
        }

        ManualCommandResult permission = Check(
            requiresIdleChannel: true,
            MachineTagKeys.ManualCarriageSpeed,
            MachineTagKeys.ManualCarriageStrokeStart,
            MachineTagKeys.ManualCarriageStrokeEnd,
            MachineTagKeys.ManualCarriageStart);
        if (!permission.Succeeded)
        {
            return permission;
        }

        return await TryAsync(async () =>
        {
            await WriteNumberAsync(MachineTagKeys.ManualCarriageSpeed, speedMmPerMin, cancellationToken).ConfigureAwait(false);
            await WriteNumberAsync(MachineTagKeys.ManualCarriageStrokeStart, strokeStartMm, cancellationToken).ConfigureAwait(false);
            await WriteNumberAsync(MachineTagKeys.ManualCarriageStrokeEnd, strokeEndMm, cancellationToken).ConfigureAwait(false);
            await PulseAsync(MachineTagKeys.ManualCarriageStart, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<ManualCommandResult> StopReciprocationAsync(CancellationToken cancellationToken)
    {
        ManualCommandResult permission = Check(requiresIdleChannel: false, MachineTagKeys.ManualCarriageStop);
        if (!permission.Succeeded)
        {
            return permission;
        }

        return await TryAsync(() => PulseAsync(MachineTagKeys.ManualCarriageStop, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ManualCommandResult> StartPositioningAsync(
        PositioningAxis axis, double targetMm, double speedMmPerMin, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(axis) || !double.IsFinite(targetMm) || !(speedMmPerMin > 0))
        {
            return new ManualCommandResult(ManualCommandOutcome.WriteFailed, OutOfRangeResourceKey);
        }

        ManualCommandResult permission = Check(
            requiresIdleChannel: true,
            MachineTagKeys.ManualPositionAxis,
            MachineTagKeys.ManualPositionTarget,
            MachineTagKeys.ManualPositionSpeed,
            MachineTagKeys.ManualPositionStart);
        if (!permission.Succeeded)
        {
            return permission;
        }

        return await TryAsync(async () =>
        {
            await WriteNumberAsync(MachineTagKeys.ManualPositionAxis, (int)axis, cancellationToken).ConfigureAwait(false);
            await WriteNumberAsync(MachineTagKeys.ManualPositionTarget, targetMm, cancellationToken).ConfigureAwait(false);
            await WriteNumberAsync(MachineTagKeys.ManualPositionSpeed, speedMmPerMin, cancellationToken).ConfigureAwait(false);
            await PulseAsync(MachineTagKeys.ManualPositionStart, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<ManualCommandResult> RequestModeAsync(MachineModeRequest mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            return new ManualCommandResult(ManualCommandOutcome.WriteFailed, OutOfRangeResourceKey);
        }

        ManualCommandResult permission = Check(requiresIdleChannel: false, MachineTagKeys.ModeRequest);
        if (!permission.Succeeded)
        {
            return permission;
        }

        return await TryAsync(() => WriteNumberAsync(MachineTagKeys.ModeRequest, (int)mode, cancellationToken)).ConfigureAwait(false);
    }

    private ManualCommandResult Check(bool requiresIdleChannel, params string[] logicalNames)
    {
        if (FirstUnmapped(logicalNames) is not null)
        {
            return new ManualCommandResult(ManualCommandOutcome.NotMapped, ManualCommandService.NotMappedResourceKey);
        }

        MachineStateSnapshot snapshot = this.monitor.Current;
        if (snapshot.ConnectionState != GatewayConnectionState.Connected)
        {
            return new ManualCommandResult(ManualCommandOutcome.Disconnected, ManualCommandService.DisconnectedResourceKey);
        }

        if (requiresIdleChannel)
        {
            double? channelState = snapshot.GetNumberOrNull(MachineTagKeys.ChannelState);
            if (channelState is null || (NcChannelState)(int)channelState.Value != NcChannelState.Reset)
            {
                return new ManualCommandResult(ManualCommandOutcome.ChannelBusy, ManualCommandService.ChannelBusyResourceKey);
            }
        }

        return ManualCommandResult.Sent;
    }

    private static async Task<ManualCommandResult> TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return ManualCommandResult.Sent;
        }
        catch (GatewayException)
        {
            return new ManualCommandResult(ManualCommandOutcome.WriteFailed, ManualCommandService.WriteFailedResourceKey);
        }
    }

    private async Task PulseAsync(string logicalName, CancellationToken cancellationToken)
    {
        await WriteBooleanAsync(logicalName, true, cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(this.pulseWidth, this.timeProvider, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 取消也要把命令位清掉；清不掉就靠 PLC 的上升沿触发与自复位。
            await WriteBooleanAsync(logicalName, false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private Task WriteBooleanAsync(string logicalName, bool value, CancellationToken cancellationToken) =>
        this.gateway.WriteTagAsync(
            logicalName,
            new TagValue(logicalName, TagDataType.Boolean, value, this.timeProvider.GetUtcNow()),
            cancellationToken);

    private Task WriteNumberAsync(string logicalName, double value, CancellationToken cancellationToken)
    {
        TagDataType type = this.tagMap.TryResolve(logicalName, out TagDescriptor? descriptor) && descriptor is not null
            ? descriptor.DataType
            : TagDataType.Double;
        object boxed = type == TagDataType.Int32 ? (object)(int)Math.Round(value) : value;
        return this.gateway.WriteTagAsync(
            logicalName,
            new TagValue(logicalName, type, boxed, this.timeProvider.GetUtcNow()),
            cancellationToken);
    }
}
