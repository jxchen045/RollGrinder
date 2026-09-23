using FluentAssertions;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 磨完了还是被中止了：按 NC 的"循环正常结束"位（R124）判。
/// 每一条对应一种现场会遇到的情形。
/// </summary>
public sealed class CycleCompletionDetectorTests
{
    private const NcChannelState Run = NcChannelState.Running;
    private const NcChannelState Hold = NcChannelState.Interrupted;
    private const NcChannelState Idle = NcChannelState.Reset;

    [Fact]
    public void A_normal_cycle_ends_as_completed()
    {
        var detector = new CycleCompletionDetector();
        detector.Observe(true, Idle, false).Should().Be(CycleCompletionDecision.None);
        detector.Observe(true, Run, false).Should().Be(CycleCompletionDecision.None);
        detector.Observe(true, Run, true).Should().Be(CycleCompletionDecision.None, "结束位先到，复位还没来");
        detector.Observe(true, Idle, true).Should().Be(CycleCompletionDecision.Completed);
        detector.Observe(true, Idle, true).Should().Be(CycleCompletionDecision.None, "同一趟只收尾一次");
    }

    [Fact]
    public void Flag_and_reset_arriving_in_the_same_poll_still_count_as_completed()
    {
        var detector = new CycleCompletionDetector();
        detector.Observe(true, Run, false);

        detector.Observe(true, Idle, true).Should().Be(CycleCompletionDecision.Completed);
    }

    [Fact]
    public void Reset_before_the_flag_is_an_abandoned_roll()
    {
        var detector = new CycleCompletionDetector();
        detector.Observe(true, Run, false);
        detector.Observe(true, Hold, false);

        detector.Observe(true, Idle, false).Should().Be(CycleCompletionDecision.Abandoned);
    }

    [Fact]
    public void Feed_hold_is_still_part_of_the_cycle()
    {
        var detector = new CycleCompletionDetector();
        detector.Observe(true, Run, false);

        detector.Observe(true, Hold, false).Should().Be(CycleCompletionDecision.None, "进给保持不是结束");
        detector.Observe(true, Run, true).Should().Be(CycleCompletionDecision.None);
        detector.Observe(true, Idle, true).Should().Be(CycleCompletionDecision.Completed);
    }

    [Fact]
    public void Without_the_flag_mapped_the_hmi_does_not_guess()
    {
        var detector = new CycleCompletionDetector();
        detector.Observe(true, Run, null);

        detector.Observe(true, Idle, null).Should().Be(CycleCompletionDecision.NeedsOperator);
    }

    [Fact]
    public void A_flag_left_over_from_the_previous_roll_does_not_complete_the_next_one()
    {
        var detector = new CycleCompletionDetector();
        detector.Observe(true, Run, false);
        detector.Observe(true, Idle, true).Should().Be(CycleCompletionDecision.Completed);

        // 下一支：NC 程序开头本该清 0，但万一第一拍还读到上一支的 1……
        detector.Observe(true, Run, true);
        detector.Observe(true, Run, false);
        detector.Observe(true, Idle, false).Should().Be(CycleCompletionDecision.Completed,
            "运行中见过 1 就算——所以 NC 必须在程序开头清 0，这一条把这个约定钉住");
    }

    [Fact]
    public void Disconnection_neither_decides_nor_forgets()
    {
        var detector = new CycleCompletionDetector();
        detector.Observe(true, Run, false);

        detector.Observe(false, null, null).Should().Be(CycleCompletionDecision.None);
        detector.Observe(true, Idle, true).Should().Be(CycleCompletionDecision.Completed, "断线期间磨完的，重连后照样收尾");
    }

    [Fact]
    public void Restarting_the_hmi_after_the_roll_finished_completes_the_record()
    {
        // 最高原则：上位机被杀，这支辊照样磨完。重启后第一拍看到"复位 + 结束位 1"，记录要补上。
        var detector = new CycleCompletionDetector();

        detector.Observe(true, Idle, true).Should().Be(CycleCompletionDecision.Completed);
    }

    [Fact]
    public void Restarting_the_hmi_while_idle_without_the_flag_changes_nothing()
    {
        var detector = new CycleCompletionDetector();

        detector.Observe(true, Idle, false).Should().Be(CycleCompletionDecision.None, "还没开磨与被中止过分不清，就不动");
    }

    [Fact]
    public void Restarting_the_hmi_mid_cycle_keeps_watching()
    {
        var detector = new CycleCompletionDetector();
        detector.Observe(true, Run, false).Should().Be(CycleCompletionDecision.None);

        detector.Observe(true, Idle, true).Should().Be(CycleCompletionDecision.Completed);
    }
}
