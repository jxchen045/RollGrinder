using System;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Steps;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Jobs;

/// <summary>流程控制被挡下来的原因。</summary>
public enum StepFlowRefusal
{
    /// <summary>没被挡。</summary>
    None = 0,

    /// <summary>tagmap 里没登记这个命令位。</summary>
    NotMapped = 1,

    /// <summary>没连上机床。</summary>
    Disconnected = 2,

    /// <summary>NC 现在没有在跑作业。</summary>
    NotRunning = 3,

    /// <summary>跳转目标不在作业的工序范围内。</summary>
    OutOfRange = 4,

    /// <summary>跳到已经磨完的工序上（只许往前，不许回头重磨）。</summary>
    Backwards = 5,

    /// <summary>写机床失败。</summary>
    WriteFailed = 6,
}

/// <summary>流程控制的结果。</summary>
/// <param name="Succeeded">命令是否已发到机床。</param>
/// <param name="Refusal">被挡下来的原因。</param>
/// <param name="MessageResourceKey">给界面看的文案键。</param>
public sealed record StepFlowResult(bool Succeeded, StepFlowRefusal Refusal, string? MessageResourceKey)
{
    /// <summary>已发出。</summary>
    public static StepFlowResult Sent { get; } = new(true, StepFlowRefusal.None, null);

    /// <summary>被挡下来了，附带一句为什么。</summary>
    public static StepFlowResult Refused(StepFlowRefusal refusal, string resourceKey) =>
        new(false, refusal, resourceKey);
}

/// <summary>
/// 磨削当中的工序流程控制：跳到某一道、把当前这一道提前结束。
///
/// **上位机只是按一下按钮。** 真正什么时候跳、跳到哪里安全，由 NC 决定：
/// 上位机写下目标工序号，再脉冲一下命令位（写 true → 等脉宽 → 写 false），
/// NC 在一个安全点（通常是本次走刀走完）读取并动作。上位机被强制结束时，
/// 已经发出去的跳转照常生效，没发出去的就当没按过——两头都不会卡住（最高原则）。
///
/// 只许往前跳。往回跳意味着已经磨完的工序要重来一遍，余量对不上，
/// 记录也说不清这支辊到底按什么磨的；真要重磨，停下来重新下发一支作业。
/// </summary>
public interface IStepFlowControlService
{
    /// <summary>这两个命令位在 tagmap 里登记了吗？没登记时界面把按钮压暗。</summary>
    bool IsMapped { get; }

    /// <summary>能不能跳到第 <paramref name="targetStepOrder"/> 道（从 1 开始）。</summary>
    StepFlowResult CanJumpTo(GrindingJob job, int targetStepOrder);

    /// <summary>能不能把当前这一道提前结束。</summary>
    StepFlowResult CanEndStepEarly(GrindingJob job);

    /// <summary>跳到第 <paramref name="targetStepOrder"/> 道。</summary>
    Task<StepFlowResult> JumpToStepAsync(
        GrindingJob job, int targetStepOrder, string requestedBy, CancellationToken cancellationToken);

    /// <summary>把当前这一道提前结束，进入下一道。</summary>
    Task<StepFlowResult> EndStepEarlyAsync(
        GrindingJob job, string requestedBy, CancellationToken cancellationToken);

    /// <summary>
    /// 请 NC 启动循环。
    ///
    /// **是请求不是命令。** 上位机不在任何一条使能链里（见机床硬件评估 §5），
    /// 能不能动由 PLC 的互锁说了算；这一下只是把"操作工想开始了"告诉它。
    /// </summary>
    Task<StepFlowResult> RequestCycleStartAsync(string requestedBy, CancellationToken cancellationToken);

    /// <summary>请 NC 进给保持。同样是请求。</summary>
    Task<StepFlowResult> RequestFeedHoldAsync(string requestedBy, CancellationToken cancellationToken);

    /// <summary>这两个请求位在 tagmap 里登记了吗。</summary>
    bool CanRequestCycleControl { get; }
}

/// <inheritdoc cref="IStepFlowControlService"/>
public sealed class StepFlowControlService : IStepFlowControlService
{
    /// <summary>tagmap 里没登记流程控制命令位。</summary>
    public const string NotMappedResourceKey = "Alarm_StepFlowNeedsTagMapping";

    /// <summary>没连上机床。</summary>
    public const string DisconnectedResourceKey = "Alarm_ActionNeedsConnection";

    /// <summary>NC 现在没有在跑作业。</summary>
    public const string NotRunningResourceKey = "Alarm_StepFlowNeedsRunningJob";

    /// <summary>跳转目标不在工序范围内。</summary>
    public const string OutOfRangeResourceKey = "Alarm_StepJumpOutOfRange";

    /// <summary>不许往回跳。</summary>
    public const string BackwardsResourceKey = "Alarm_StepJumpBackwards";

    /// <summary>写机床失败。</summary>
    public const string WriteFailedResourceKey = "Alarm_ActionWriteFailed";

    /// <summary>已跳转（提示级）。</summary>
    public const string JumpedResourceKey = "Alarm_StepJumped";

    /// <summary>已提前结束（提示级）。</summary>
    public const string EndedEarlyResourceKey = "Alarm_StepEndedEarly";

    /// <summary>已请求循环启动（提示级）。</summary>
    public const string CycleStartResourceKey = "Alarm_CycleStartRequested";

    /// <summary>已请求进给保持（提示级）。</summary>
    public const string FeedHoldResourceKey = "Alarm_FeedHoldRequested";

    private readonly IMachineGateway gateway;
    private readonly IMachineMonitor monitor;
    private readonly ITagMap tagMap;
    private readonly IAlarmSink alarms;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pulseWidth;

    public StepFlowControlService(
        IMachineGateway gateway,
        IMachineMonitor monitor,
        ITagMap tagMap,
        IAlarmSink alarms,
        HmiSettings settings,
        TimeProvider timeProvider)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(settings);

        // 和手动动作共用一个脉宽：现场调一处，两边一起跟着走。
        this.pulseWidth = TimeSpan.FromMilliseconds(settings.ManualPulseMs);
    }

    public bool IsMapped =>
        this.tagMap.TryResolve(MachineTagKeys.JobControlJumpToStep, out _)
        && this.tagMap.TryResolve(MachineTagKeys.JobControlEndStepEarly, out _)
        && this.tagMap.TryResolve(MachineTagKeys.JobControlTargetStepOrder, out _);

    public StepFlowResult CanJumpTo(GrindingJob job, int targetStepOrder)
    {
        ArgumentNullException.ThrowIfNull(job);

        StepFlowResult common = CheckCommon(out int currentStepOrder);
        if (!common.Succeeded)
        {
            return common;
        }

        if (targetStepOrder < 1 || targetStepOrder > job.Steps.Count)
        {
            return StepFlowResult.Refused(StepFlowRefusal.OutOfRange, OutOfRangeResourceKey);
        }

        // 跳到正在跑的那一道等于什么都没做，也归入"往回跳"一类挡掉——
        // 按了没反应比按了被拒更让人犯嘀咕。
        if (targetStepOrder <= currentStepOrder)
        {
            return StepFlowResult.Refused(StepFlowRefusal.Backwards, BackwardsResourceKey);
        }

        return StepFlowResult.Sent;
    }

    public StepFlowResult CanEndStepEarly(GrindingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return CheckCommon(out _);
    }

    public async Task<StepFlowResult> JumpToStepAsync(
        GrindingJob job, int targetStepOrder, string requestedBy, CancellationToken cancellationToken)
    {
        StepFlowResult permission = CanJumpTo(job, targetStepOrder);
        if (!permission.Succeeded)
        {
            return permission;
        }

        try
        {
            // 先写目标号再脉冲：反过来的话 NC 可能读到上一次的目标。
            DateTimeOffset now = this.timeProvider.GetUtcNow();
            await this.gateway.WriteTagAsync(
                MachineTagKeys.JobControlTargetStepOrder,
                new TagValue(
                    MachineTagKeys.JobControlTargetStepOrder, TagDataType.Int32, targetStepOrder, now),
                cancellationToken).ConfigureAwait(false);

            await PulseAsync(MachineTagKeys.JobControlJumpToStep, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException ex)
        {
            return await FailedAsync(MachineTagKeys.JobControlJumpToStep, ex).ConfigureAwait(false);
        }

        this.alarms.Raise(
            AlarmSeverity.Information,
            JumpedResourceKey,
            $"{requestedBy}: -> {targetStepOrder}",
            AlarmCodes.StepJumped);

        return StepFlowResult.Sent;
    }

    public async Task<StepFlowResult> EndStepEarlyAsync(
        GrindingJob job, string requestedBy, CancellationToken cancellationToken)
    {
        StepFlowResult permission = CanEndStepEarly(job);
        if (!permission.Succeeded)
        {
            return permission;
        }

        int currentStepOrder = CurrentStepOrder();

        try
        {
            await PulseAsync(MachineTagKeys.JobControlEndStepEarly, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException ex)
        {
            return await FailedAsync(MachineTagKeys.JobControlEndStepEarly, ex).ConfigureAwait(false);
        }

        this.alarms.Raise(
            AlarmSeverity.Information,
            EndedEarlyResourceKey,
            $"{requestedBy}: {currentStepOrder}",
            AlarmCodes.StepEndedEarly);

        return StepFlowResult.Sent;
    }

    public bool CanRequestCycleControl =>
        this.tagMap.TryResolve(MachineTagKeys.JobControlCycleStart, out _)
        && this.tagMap.TryResolve(MachineTagKeys.JobControlFeedHold, out _);

    public Task<StepFlowResult> RequestCycleStartAsync(string requestedBy, CancellationToken cancellationToken) =>
        RequestAsync(MachineTagKeys.JobControlCycleStart, CycleStartResourceKey, requestedBy, cancellationToken);

    public Task<StepFlowResult> RequestFeedHoldAsync(string requestedBy, CancellationToken cancellationToken) =>
        RequestAsync(MachineTagKeys.JobControlFeedHold, FeedHoldResourceKey, requestedBy, cancellationToken);

    /// <summary>
    /// 循环启动 / 进给保持共用这一段：脉冲一下就完事。
    ///
    /// 与跳转不同，这两下**不看有没有在跑作业**——想启动的时候本来就还没在跑，
    /// 而想保持的时候更不该被"读不到工序号"挡住。
    /// </summary>
    private async Task<StepFlowResult> RequestAsync(
        string logicalName, string resourceKey, string requestedBy, CancellationToken cancellationToken)
    {
        if (!this.tagMap.TryResolve(logicalName, out _))
        {
            return StepFlowResult.Refused(StepFlowRefusal.NotMapped, NotMappedResourceKey);
        }

        if (this.monitor.Current.ConnectionState != GatewayConnectionState.Connected)
        {
            return StepFlowResult.Refused(StepFlowRefusal.Disconnected, DisconnectedResourceKey);
        }

        try
        {
            await PulseAsync(logicalName, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException ex)
        {
            return await FailedAsync(logicalName, ex).ConfigureAwait(false);
        }

        this.alarms.Raise(AlarmSeverity.Information, resourceKey, requestedBy, AlarmCodes.StepJumped);
        return StepFlowResult.Sent;
    }

    private StepFlowResult CheckCommon(out int currentStepOrder)
    {
        currentStepOrder = CurrentStepOrder();

        if (!IsMapped)
        {
            return StepFlowResult.Refused(StepFlowRefusal.NotMapped, NotMappedResourceKey);
        }

        if (this.monitor.Current.ConnectionState != GatewayConnectionState.Connected)
        {
            return StepFlowResult.Refused(StepFlowRefusal.Disconnected, DisconnectedResourceKey);
        }

        // 没在跑就没有"当前工序"可言：这时候该改的是作业本身，不是按跳转。
        if (currentStepOrder < 1)
        {
            return StepFlowResult.Refused(StepFlowRefusal.NotRunning, NotRunningResourceKey);
        }

        return StepFlowResult.Sent;
    }

    private int CurrentStepOrder()
    {
        double? order = this.monitor.Current.GetNumberOrNull(MachineTagKeys.JobCurrentStepOrder);
        return order is null ? 0 : (int)order.Value;
    }

    private async Task PulseAsync(string logicalName, CancellationToken cancellationToken)
    {
        try
        {
            await WriteBitAsync(logicalName, true, cancellationToken).ConfigureAwait(false);
            await Task.Delay(this.pulseWidth, this.timeProvider, cancellationToken).ConfigureAwait(false);
            await WriteBitAsync(logicalName, false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消发生在脉宽等待里：尽力清掉命令位。清不掉也还有 PLC 的自复位兜着。
            await TryClearAsync(logicalName).ConfigureAwait(false);
            throw;
        }
    }

    private Task WriteBitAsync(string logicalName, bool value, CancellationToken cancellationToken) =>
        this.gateway.WriteTagAsync(
            logicalName,
            new TagValue(logicalName, TagDataType.Boolean, value, this.timeProvider.GetUtcNow()),
            cancellationToken);

    private async Task<StepFlowResult> FailedAsync(string logicalName, GatewayException ex)
    {
        await TryClearAsync(logicalName).ConfigureAwait(false);
        this.alarms.Raise(
            AlarmSeverity.Error, WriteFailedResourceKey, ex.Message, AlarmCodes.GatewayFailure);
        return StepFlowResult.Refused(StepFlowRefusal.WriteFailed, WriteFailedResourceKey);
    }

    private async Task TryClearAsync(string logicalName)
    {
        try
        {
            await WriteBitAsync(logicalName, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GatewayException)
        {
            // PLC 的上升沿触发与自复位是最后一道保险。
        }
    }
}
