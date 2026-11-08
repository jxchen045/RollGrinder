using System;

namespace RollGrinder.Data.Model;

/// <summary>
/// 道次表的一行（设计说明第 6 节、接口约定 4.2）：一道往复，左右折返各一组 NC 锁存的实际值。
/// 随磨削记录保存；读不到的字段为 null。
/// </summary>
public sealed record RecordPassRow(
    int Sequence,
    int StepOrder,
    int Pass,
    double? LeftZMm,
    double? LeftDwellSeconds,
    double? LeftOverridePercent,
    double? LeftXMm,
    double? RightZMm,
    double? RightDwellSeconds,
    double? RightOverridePercent,
    double? RightXMm,
    double? CompensationMicrometer,
    int? StrokeVersion,
    double? DrfOffsetMm,
    double? MeanCurrentA,
    int? EditAppliedSeq,
    DateTimeOffset ReadAtUtc);

/// <summary>倍率是哪一个。</summary>
public static class OverrideKinds
{
    public const string Feed = "feed";
    public const string Spindle = "spindle";
    public const string Wheel = "wheel";
}

/// <summary>
/// 倍率摘要（设计说明第 6 节）：一道里某个倍率的时间加权平均、最低、最高、不在 100 % 的时间占比；
/// 不记逐条变化。
/// </summary>
public sealed record OverrideSummaryRow(
    int StepOrder,
    int Pass,
    string Kind,
    double MeanPercent,
    double MinPercent,
    double MaxPercent,
    double OffNominalShare,
    double Seconds);

/// <summary>过程曲线是哪一条。</summary>
public static class TraceKinds
{
    /// <summary>磨削电流（A）。</summary>
    public const string Current = "current";

    /// <summary>在线测量直径（mm）。</summary>
    public const string Diameter = "diameter";

    /// <summary>拖板位置 Z（mm）。</summary>
    public const string CarriageZ = "carriageZ";
}

/// <summary>过程曲线的一个点（降采样后）：从下发起第几秒、值。</summary>
public sealed record TracePoint(string Kind, double Seconds, double Value);

/// <summary>记录事件的种类（资源键 "RunEvent_" + 名字）。</summary>
public static class RunEventKinds
{
    /// <summary>倍率低于 50 %。</summary>
    public const string OverrideLow = "OverrideLow";

    /// <summary>倍率高于 110 %。</summary>
    public const string OverrideHigh = "OverrideHigh";

    /// <summary>整道都不在 100 %。</summary>
    public const string OverrideWholePass = "OverrideWholePass";

    /// <summary>道次表缺行（缓冲溢出被覆盖，或读到一半被改写）。</summary>
    public const string PassMissing = "PassMissing";

    /// <summary>道次数据超时没更新（看门狗）。</summary>
    public const string PassDataStale = "PassDataStale";

    /// <summary>NC 确认了这一份下发（循环启动时锁存序号）。</summary>
    public const string NcAccepted = "NcAccepted";

    /// <summary>运行中改的参数 NC 采用了（第 n 道起）。</summary>
    public const string EditApplied = "EditApplied";

    public static readonly string[] All =
    {
        OverrideLow, OverrideHigh, OverrideWholePass, PassMissing, PassDataStale, NcAccepted, EditApplied,
    };

    /// <summary>越界类（"只看越界"筛的就是这些）。</summary>
    public static bool IsOutOfBound(string kind) => kind is OverrideLow or OverrideHigh or OverrideWholePass;
}

/// <summary>一条记录事件：逐条记，可筛。</summary>
/// <param name="AtUtc">时刻。</param>
/// <param name="Kind">种类（<see cref="RunEventKinds"/>）。</param>
/// <param name="StepOrder">第几道工序；不关某道工序为 null。</param>
/// <param name="Pass">工序内第几道次；同上。</param>
/// <param name="Value">数值（倍率 %、缺几道……）。</param>
/// <param name="Detail">补充（倍率种类、缺的序号段……）。</param>
public sealed record RunEvent(DateTimeOffset AtUtc, string Kind, int? StepOrder, int? Pass, double? Value, string? Detail);

/// <summary>这次下发 NC 确认了没有（接口约定第 2 节）。</summary>
public sealed record RecordNcInfo(int? DownloadSequence, DateTimeOffset? AcceptedAtUtc);

/// <summary>一支记录的事件计数（列表"标记"列用）。</summary>
public sealed record RunEventCounts(int OutOfBound, int MissingPasses);
