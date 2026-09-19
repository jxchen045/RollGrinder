namespace RollGrinder.Contracts.Dtos;

/// <summary>NC 通道状态。数值与 tagmap 指向的通道状态变量取值对应。</summary>
public enum NcChannelState
{
    /// <summary>复位/空闲。</summary>
    Reset = 0,

    /// <summary>中断（进给保持）。</summary>
    Interrupted = 1,

    /// <summary>运行中。</summary>
    Running = 2,
}
