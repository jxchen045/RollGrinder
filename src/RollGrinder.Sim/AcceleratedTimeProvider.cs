using System;

namespace RollGrinder.Sim;

/// <summary>
/// 把仿真机床的时间按倍率加速：一支辊原速要磨几分钟，界面自检时不必真等。
/// 只加速"经过了多久"（时间戳），墙上时钟（GetUtcNow）不动——记录里的时间仍是真实时间。
/// </summary>
internal sealed class AcceleratedTimeProvider : TimeProvider
{
    private readonly TimeProvider inner;
    private readonly double factor;
    private readonly long origin;

    public AcceleratedTimeProvider(TimeProvider inner, double factor)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1.0);
        this.factor = factor;
        this.origin = inner.GetTimestamp();
    }

    public override long TimestampFrequency => this.inner.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => this.inner.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => this.inner.LocalTimeZone;

    public override long GetTimestamp() =>
        this.origin + (long)((this.inner.GetTimestamp() - this.origin) * this.factor);
}
