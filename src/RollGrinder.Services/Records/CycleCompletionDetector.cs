using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Services.Records;

/// <summary>一拍观察之后该对当前记录做什么。</summary>
public enum CycleCompletionDecision
{
    /// <summary>什么也不做。</summary>
    None = 0,

    /// <summary>NC 报了循环正常结束：收尾为已完成。</summary>
    Completed = 1,

    /// <summary>通道在循环正常结束之前就回到了复位：收尾为已放弃。</summary>
    Abandoned = 2,

    /// <summary>
    /// 循环结束了，但 NC 没有提供结束位（tagmap 里没映射）：上位机不猜，
    /// 记录留给操作员在记录页手动收尾，只提醒一声。
    /// </summary>
    NeedsOperator = 3,
}

/// <summary>
/// 从一拍一拍的通道状态与"循环正常结束"位里判断这支辊是磨完了还是被中止了。
///
/// 约定（NC 侧）：程序开头 R124=0，走完最后一道、M30 之前 R124=1。于是——
/// - 通道从运行/中断回到复位，期间见过结束位为 1 ⇒ 磨完了；
/// - 回到复位时结束位一直是 0 ⇒ 半路被复位了；
/// - 结束位没映射（读不到）⇒ 不猜，交给人。
///
/// 上位机中途被杀、重启后第一拍就看到"复位 + 结束位为 1"：那是它不在的时候磨完的，
/// 照样判为磨完（最高原则：上位机不在，这支辊也照样磨完——记录要能补上）。
/// 重启后第一拍看到"复位 + 结束位为 0"：可能压根还没开磨，也可能被中止过，分不清就不动。
///
/// 纯逻辑，不碰网关与数据库，便于把每种边界情况写成单测。
/// </summary>
public sealed class CycleCompletionDetector
{
    private bool initialised;
    private bool wasRunning;
    private bool sawComplete;

    /// <summary>喂一拍观察。</summary>
    /// <param name="connected">这一拍是否连着机床。断线时什么也不判，状态原样保留，等重连后接着看。</param>
    /// <param name="channel">通道状态；读不到为 null。</param>
    /// <param name="cycleComplete">结束位；没映射、读不到为 null。</param>
    public CycleCompletionDecision Observe(bool connected, NcChannelState? channel, bool? cycleComplete)
    {
        if (!connected || channel is null)
        {
            return CycleCompletionDecision.None;
        }

        bool inCycle = channel is NcChannelState.Running or NcChannelState.Interrupted;

        if (!this.initialised)
        {
            this.initialised = true;
            if (!inCycle)
            {
                // 重启后第一拍：结束位为 1 说明上位机不在的时候这支辊磨完了。
                return cycleComplete == true ? CycleCompletionDecision.Completed : CycleCompletionDecision.None;
            }
        }

        if (inCycle)
        {
            if (!this.wasRunning)
            {
                // 新的一趟：上一趟看见过的结束位不能带进来。
                this.sawComplete = false;
            }

            this.wasRunning = true;
            this.sawComplete |= cycleComplete == true;
            return CycleCompletionDecision.None;
        }

        if (!this.wasRunning)
        {
            return CycleCompletionDecision.None;
        }

        // 运行 → 复位：这一趟结束了。
        this.wasRunning = false;
        bool complete = this.sawComplete || cycleComplete == true;
        this.sawComplete = false;

        if (complete)
        {
            return CycleCompletionDecision.Completed;
        }

        return cycleComplete is null ? CycleCompletionDecision.NeedsOperator : CycleCompletionDecision.Abandoned;
    }
}
