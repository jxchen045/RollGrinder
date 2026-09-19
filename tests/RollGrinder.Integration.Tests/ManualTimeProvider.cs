using System;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 手动推进的时钟，用来让仿真与轮询的测试可复现。
/// 只实现测试用到的部分：取当前时刻、取时间戳、按频率换算。
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now;

    public ManualTimeProvider(DateTimeOffset start)
    {
        this.now = start;
    }

    public override DateTimeOffset GetUtcNow() => this.now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => this.now.UtcTicks;

    public void Advance(TimeSpan delta) => this.now = this.now.Add(delta);
}
