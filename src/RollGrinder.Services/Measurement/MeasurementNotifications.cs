using System.Threading;

namespace RollGrinder.Services.Measurement;

/// <summary>
/// "又存下了一次测量"的计数。测量是在后台（测量工序走完、手动采点）存的，
/// 自动磨削页的误差曲线与 RMS 要跟着换；页面每一拍看一眼计数变没变，变了就重读。
/// 只是个计数，不带数据——该读什么仍然去仓储里读，这里不做第二份缓存。
/// </summary>
public interface IMeasurementNotifications
{
    /// <summary>到目前为止存下的测量次数（本次运行内）。</summary>
    long Version { get; }

    /// <summary>存完一次测量后调用。</summary>
    void Archived();
}

/// <inheritdoc cref="IMeasurementNotifications"/>
public sealed class MeasurementNotifications : IMeasurementNotifications
{
    private long version;

    public long Version => Interlocked.Read(ref this.version);

    public void Archived() => Interlocked.Increment(ref this.version);
}
