namespace RollGrinder.Contracts;

/// <summary>
/// 机床时间相对墙上时间的倍率。真机为 1；仿真机床开了 --sim-speed 时就是那个倍数。
/// 上位机的服务按墙上时间计时，拿"实际用时"去和"预计用时"比，要先换算回机床时间。
/// </summary>
/// <param name="Factor">倍率，大于 0。</param>
public sealed record MachineTimeScale(double Factor)
{
    /// <summary>真机：机床时间就是墙上时间。</summary>
    public static MachineTimeScale RealTime { get; } = new(1.0);
}
