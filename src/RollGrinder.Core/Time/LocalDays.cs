using System;

namespace RollGrinder.Core.Time;

/// <summary>
/// "本地的某几天"到"UTC 时间区间"的换算，整个程序只在这一处做。
///
/// 操作员说"今天""这个月"，指的是工控机所在地的日历日；库里存的是 UTC。
/// 东八区的"9 月 23 日"是 UTC 9 月 22 日 16:00 到 9 月 23 日 16:00，不是 UTC 的 0 点到 24 点。
///
/// 时区作为参数传入而不是直接读本机时区：单元测试可以指定东八区、夏令时区来验证，
/// 结论不随跑测试那台机器的时区变化（开发机是 UTC，恰恰测不出问题）。
/// </summary>
public static class LocalDays
{
    /// <summary>某个本地日历日的零点（按该时区当天的 UTC 偏移）。</summary>
    /// <param name="day">日期；只取年月日，时刻与 Kind 一概忽略。</param>
    /// <param name="zone">时区。</param>
    public static DateTimeOffset StartOf(DateTime day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTime local = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);

        // 夏令时从零点开始的地方，零点这一刻不存在：取当天第一个存在的时刻。
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// 从 <paramref name="firstDay"/> 那天零点起、到 <paramref name="lastDay"/> 那天结束为止的区间，
    /// 前闭后开。两个日期写反了也照样给出正确顺序的区间。
    /// </summary>
    public static (DateTimeOffset FromInclusive, DateTimeOffset ToExclusive) Range(
        DateTime firstDay, DateTime lastDay, TimeZoneInfo zone)
    {
        DateTime first = firstDay.Date <= lastDay.Date ? firstDay.Date : lastDay.Date;
        DateTime last = firstDay.Date <= lastDay.Date ? lastDay.Date : firstDay.Date;
        return (StartOf(first, zone), StartOf(last.AddDays(1), zone));
    }
}
