using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.App.Interaction;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 确认、对话行、数字键盘、长按（界面最终稿 D5、4.5、4.6、F8）的纯逻辑。
/// </summary>
public sealed class InteractionTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    // ── 确认 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_runs_the_action_once_and_clears_the_question()
    {
        var clock = new ManualTimeProvider(Start);
        var service = new ConfirmationService(clock);
        int runs = 0;
        var outcomes = new List<ConfirmationOutcome>();
        service.Request("start wheel?", () => runs++, outcomes.Add);

        service.Pending!.Question.Should().Be("start wheel?");
        service.Pending.ConfirmLabelKey.Should().Be("Vk_Confirm");

        (await service.ConfirmAsync()).Should().BeTrue();
        (await service.ConfirmAsync()).Should().BeFalse("答完了，再按确认不会再发一次");

        runs.Should().Be(1);
        service.Pending.Should().BeNull();
        outcomes.Should().Equal(ConfirmationOutcome.Confirmed);
    }

    [Fact]
    public async Task Five_seconds_without_an_answer_cancels_by_itself()
    {
        var clock = new ManualTimeProvider(Start);
        var service = new ConfirmationService(clock);
        int runs = 0;
        var outcomes = new List<ConfirmationOutcome>();
        service.Request("download?", () => runs++, outcomes.Add);

        clock.Advance(TimeSpan.FromSeconds(4.9));
        service.Tick();
        service.Pending.Should().NotBeNull();
        service.Remaining.Should().BeCloseTo(TimeSpan.FromSeconds(0.1), TimeSpan.FromMilliseconds(1));

        clock.Advance(TimeSpan.FromSeconds(0.1));
        (await service.ConfirmAsync()).Should().BeFalse("到点了，迟到的确认不算数");
        runs.Should().Be(0);
        outcomes.Should().Equal(ConfirmationOutcome.Expired);
        service.Remaining.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Tick_expires_and_announces_the_change()
    {
        var clock = new ManualTimeProvider(Start);
        var service = new ConfirmationService(clock);
        int changes = 0;
        service.Changed += (_, _) => changes++;
        service.Request("delete?", () => { });

        clock.Advance(ConfirmationService.Timeout);
        service.Tick();

        service.Pending.Should().BeNull();
        changes.Should().Be(2, "出现一次、超时一次");
    }

    [Fact]
    public void Only_one_question_at_a_time_the_older_one_is_superseded()
    {
        var clock = new ManualTimeProvider(Start);
        var service = new ConfirmationService(clock);
        var first = new List<ConfirmationOutcome>();
        service.Request("first?", () => { }, first.Add);

        service.Request("second?", () => { }, _ => { }, "Vk_ConfirmDownload");

        first.Should().Equal(ConfirmationOutcome.Superseded);
        service.Pending!.Question.Should().Be("second?");
        service.Pending.ConfirmLabelKey.Should().Be("Vk_ConfirmDownload");
    }

    [Fact]
    public void Cancel_reports_whether_there_was_anything_to_cancel()
    {
        var service = new ConfirmationService(new ManualTimeProvider(Start));
        service.Cancel().Should().BeFalse("没有待确认的事，Esc 交给外壳退一级");

        var outcomes = new List<ConfirmationOutcome>();
        service.Request("clear?", () => throw new InvalidOperationException("must not run"), outcomes.Add);
        service.Cancel().Should().BeTrue();
        outcomes.Should().Equal(ConfirmationOutcome.Cancelled);
    }

    [Fact]
    public async Task An_action_that_asks_the_next_question_is_not_confused_with_the_current_one()
    {
        var service = new ConfirmationService(new ManualTimeProvider(Start));
        service.Request("step 1?", () => service.Request("step 2?", () => { }));

        await service.ConfirmAsync();

        service.Pending!.Question.Should().Be("step 2?");
    }

    // ── 对话行 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Question_beats_messages_and_messages_beat_the_hint()
    {
        var clock = new ManualTimeProvider(Start);
        var line = new DialogLineModel(clock);

        line.SetHint("feed: mm/min, 0..3000");
        line.Kind.Should().Be(DialogLineKind.Hint);

        line.Show("needs manufacturer", DialogLineKind.Reason);
        line.Text.Should().Be("needs manufacturer");

        line.SetQuestion("start wheel?");
        line.Kind.Should().Be(DialogLineKind.Question);

        line.SetQuestion(null);
        line.Kind.Should().Be(DialogLineKind.Reason, "问题答完，还没到时的消息接着显示");

        clock.Advance(DialogLineModel.DefaultDuration);
        line.Tick();
        line.Text.Should().Be("feed: mm/min, 0..3000");

        line.SetHint(null);
        line.Kind.Should().Be(DialogLineKind.Idle);
        line.Text.Should().BeEmpty();
    }

    [Fact]
    public void Messages_only_come_in_transient_kinds()
    {
        var line = new DialogLineModel(new ManualTimeProvider(Start));
        FluentActions.Invoking(() => line.Show("x", DialogLineKind.Question)).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── 数字键盘 ────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_digit_replaces_the_old_value_backspace_edits_it()
    {
        var replace = new NumericEntry("35.0", 18, 45, 1);
        replace.Digit(3);
        replace.Digit(8);
        replace.Text.Should().Be("38");

        var edit = new NumericEntry("35.0", 18, 45, 1);
        edit.Press(NumericKey.Backspace);
        edit.Digit(5);
        edit.Text.Should().Be("35.5");
    }

    [Fact]
    public void Out_of_range_values_are_refused_with_the_reason()
    {
        var entry = new NumericEntry(string.Empty, 50, 100, 0);
        entry.Digit(1);
        entry.Digit(2);
        entry.Digit(0);

        entry.TryCommit(out _).Should().Be(NumericEntryError.AboveMaximum);

        entry.Press(NumericKey.Clear);
        entry.TryCommit(out _).Should().Be(NumericEntryError.Empty);

        entry.Digit(4);
        entry.TryCommit(out _).Should().Be(NumericEntryError.BelowMinimum);

        entry.Press(NumericKey.Clear);
        entry.Digit(7);
        entry.Digit(5);
        entry.TryCommit(out double value).Should().Be(NumericEntryError.None);
        value.Should().Be(75);
    }

    [Fact]
    public void Precision_sign_and_decimal_point_follow_the_field()
    {
        var integer = new NumericEntry("0", 0, 10, 0);
        integer.Press(NumericKey.DecimalPoint);
        integer.Press(NumericKey.Sign);
        integer.Digit(7);
        integer.Text.Should().Be("7", "整数格没有小数点，不许负的格没有负号，前导零不累积");

        var micrometre = new NumericEntry(null, -2000, 2000, 1);
        micrometre.Press(NumericKey.DecimalPoint);
        micrometre.Digit(2);
        micrometre.Digit(5);
        micrometre.Press(NumericKey.Sign);
        micrometre.Text.Should().Be("-0.2", "只收一位小数，第二位不进缓冲");
        micrometre.TryCommit(out double value).Should().Be(NumericEntryError.None);
        value.Should().Be(-0.2);
    }

    [Fact]
    public void A_backwards_range_is_a_programming_error()
    {
        FluentActions.Invoking(() => new NumericEntry("1", 5, 1)).Should().Throw<ArgumentException>();
    }

    // ── 长按 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Holding_still_for_six_tenths_of_a_second_fires_once()
    {
        var press = new LongPressDetector();
        press.Down(100, 100, Start);

        press.Poll(Start.AddSeconds(0.5)).Should().BeFalse();
        press.Move(105, 104);
        press.Poll(Start.AddSeconds(0.6)).Should().BeTrue();
        press.Poll(Start.AddSeconds(0.9)).Should().BeFalse("一次按下只触发一次");
        press.Fired.Should().BeTrue();
    }

    [Fact]
    public void Swiping_or_lifting_early_cancels_the_long_press()
    {
        var swipe = new LongPressDetector();
        swipe.Down(100, 100, Start);
        swipe.Move(100, 130);
        swipe.Poll(Start.AddSeconds(1)).Should().BeFalse("在扫动翻页，不是长按");

        var tap = new LongPressDetector();
        tap.Down(100, 100, Start);
        tap.Up();
        tap.Poll(Start.AddSeconds(1)).Should().BeFalse();
        tap.Fired.Should().BeFalse();
    }
}
