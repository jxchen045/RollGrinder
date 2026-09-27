using System;

namespace RollGrinder.Data.Model;

/// <summary>砂轮上发生了什么。</summary>
public enum WheelEventKind
{
    /// <summary>换了一片新砂轮（换砂轮向导走完）。</summary>
    Change = 1,

    /// <summary>修整了一次。</summary>
    Dress = 2,
}

/// <summary>这件事是从哪儿来的。</summary>
public enum WheelEventSource
{
    /// <summary>换砂轮向导。</summary>
    Wizard = 1,

    /// <summary>程序里的"砂轮修整"工序由 NC 走完。</summary>
    Program = 2,

    /// <summary>手动页按了"砂轮修整"循环。</summary>
    Manual = 3,
}

/// <summary>砂轮的一条修整 / 更换记录。</summary>
/// <param name="EventId">记录标识。</param>
/// <param name="OccurredAtUtc">发生时刻（UTC）。</param>
/// <param name="Kind">换砂轮还是修整。</param>
/// <param name="Source">从哪儿来的。</param>
/// <param name="WheelDiameterMm">当时的砂轮直径（mm）；读不到为空。</param>
/// <param name="ChangedBy">操作员；NC 自己走完的工序为空串。</param>
/// <param name="Detail">补充说明，例如修整的切深与道次（现场数据，不翻译）。</param>
public sealed record WheelEvent(
    string EventId,
    DateTimeOffset OccurredAtUtc,
    WheelEventKind Kind,
    WheelEventSource Source,
    double? WheelDiameterMm,
    string ChangedBy,
    string Detail);
