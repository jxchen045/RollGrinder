using System;
using FluentAssertions;
using RollGrinder.App.Diagnostics;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>界面异常兜底：偶发的兜住，连珠炮似的放手（免得空转拖累共屏的 Operate）。</summary>
public sealed class ExceptionStormDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Occasional_exceptions_are_always_absorbed()
    {
        var detector = new ExceptionStormDetector(maxCount: 3, window: TimeSpan.FromSeconds(2));

        for (int i = 0; i < 20; i++)
        {
            detector.TryAbsorb(T0.AddSeconds(i * 5)).Should().BeTrue("隔得开的异常各算各的");
        }
    }

    [Fact]
    public void A_burst_beyond_the_limit_is_a_storm()
    {
        var detector = new ExceptionStormDetector(maxCount: 3, window: TimeSpan.FromSeconds(2));

        detector.TryAbsorb(T0).Should().BeTrue();
        detector.TryAbsorb(T0.AddMilliseconds(10)).Should().BeTrue();
        detector.TryAbsorb(T0.AddMilliseconds(20)).Should().BeTrue();
        detector.TryAbsorb(T0.AddMilliseconds(30)).Should().BeFalse("窗口内第 4 次：多半是渲染里反复抛，兜住只会空转");
    }

    [Fact]
    public void The_window_slides()
    {
        var detector = new ExceptionStormDetector(maxCount: 2, window: TimeSpan.FromSeconds(2));

        detector.TryAbsorb(T0).Should().BeTrue();
        detector.TryAbsorb(T0.AddSeconds(1)).Should().BeTrue();
        detector.TryAbsorb(T0.AddSeconds(2.5)).Should().BeTrue("第一次已滑出窗口");
    }
}
