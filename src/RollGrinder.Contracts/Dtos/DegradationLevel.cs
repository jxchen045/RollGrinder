namespace RollGrinder.Contracts.Dtos;

/// <summary>
/// 补偿降级级别。测量或补偿链路出问题时，可以退一步继续磨，而不是停机。
/// 取值由机床侧的逻辑变量承载，不是上位机的内部状态。
/// </summary>
public enum DegradationLevel
{
    /// <summary>全功能：轴线前馈 + 行程间迭代 + 实时补偿。</summary>
    Full = 0,

    /// <summary>降一级：轴线前馈 + 行程间迭代。</summary>
    NoRealtime = 1,

    /// <summary>降二级：仅轴线前馈。</summary>
    FeedForwardOnly = 2,
}
