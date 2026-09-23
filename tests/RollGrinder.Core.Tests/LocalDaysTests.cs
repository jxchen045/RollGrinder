using System;
using FluentAssertions;
using RollGrinder.Core.Time;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 本地日历日 → UTC 区间。时区显式传入，所以这些断言在任何时区的机器上结论都一样——
/// 之前的写法在 UTC 的开发机上全过，在东八区的工控机上一查询就抛异常。
/// </summary>
public sealed class LocalDaysTests
{
    private static readonly TimeZoneInfo China = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    /// <summary>一个从 3 月第二个周日 0:00 跳到 1:00 的夏令时区（午夜不存在）。</summary>
    private static readonly TimeZoneInfo MidnightDst = TimeZoneInfo.CreateCustomTimeZone(
        "DST-midnight",
        TimeSpan.FromHours(-3),
        "DST-midnight",
        "Standard",
        "Daylight",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1),
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 1, DayOfWeek.Sunday)),
        });

    [Fact]
    public void A_day_in_UTC_plus_8_starts_at_16_00_UTC_the_day_before()
    {
        (DateTimeOffset from, DateTimeOffset to) = LocalDays.Range(new DateTime(2026, 9, 23), new DateTime(2026, 9, 23), China);

        from.UtcDateTime.Should().Be(new DateTime(2026, 9, 22, 16, 0, 0));
        to.UtcDateTime.Should().Be(new DateTime(2026, 9, 23, 16, 0, 0));
        from.Offset.Should().Be(TimeSpan.FromHours(8));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public void Kind_of_the_input_never_matters(DateTimeKind kind)
    {
        // DateTime.Today 是 Local，DatePicker 给的是 Unspecified；两种都要能用（曾经 Local 会抛异常）。
        DateTime day = DateTime.SpecifyKind(new DateTime(2026, 9, 23, 13, 45, 0), kind);

        LocalDays.StartOf(day, China).UtcDateTime.Should().Be(new DateTime(2026, 9, 22, 16, 0, 0));
    }

    [Fact]
    public void Works_with_DateTime_Today_on_this_machine()
    {
        Action act = () => LocalDays.Range(DateTime.Today.AddDays(-30), DateTime.Today, TimeZoneInfo.Local);

        act.Should().NotThrow();
    }

    [Fact]
    public void Reversed_dates_still_give_an_ordered_range()
    {
        (DateTimeOffset from, DateTimeOffset to) = LocalDays.Range(new DateTime(2026, 9, 30), new DateTime(2026, 9, 1), China);

        from.Should().BeBefore(to);
        (to - from).Should().Be(TimeSpan.FromDays(30));
    }

    [Fact]
    public void A_month_spans_whole_local_days()
    {
        (DateTimeOffset from, DateTimeOffset to) = LocalDays.Range(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), China);

        (to - from).Should().Be(TimeSpan.FromDays(30));
        from.UtcDateTime.Should().Be(new DateTime(2026, 8, 31, 16, 0, 0));
    }

    [Fact]
    public void A_missing_local_midnight_moves_to_the_first_real_instant()
    {
        // 2026-03-08 是 3 月第二个周日：当地 0:00 直接跳到 1:00。
        DateTimeOffset start = LocalDays.StartOf(new DateTime(2026, 3, 8), MidnightDst);

        MidnightDst.IsInvalidTime(start.DateTime).Should().BeFalse();
        start.DateTime.Date.Should().Be(new DateTime(2026, 3, 8));
    }
}
