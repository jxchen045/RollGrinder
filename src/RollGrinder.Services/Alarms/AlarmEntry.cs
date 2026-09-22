using System;

namespace RollGrinder.Services.Alarms;

/// <summary>
/// 上位机报警号。号段 800000–800999 是本系统专用，新增一条报警在这里登记一个号，
/// 现场按号查，不靠文案。
///
/// 为什么是 800000：MGK84160 操作说明书把号段划死了——NC 报警 &lt; 500000，
/// PLC 报警 ≥ 500000，其中严重故障 &lt; 700040、一般故障 ≥ 700040。
/// 早先用的 720000 段正落在机床的一般故障号段里，现场按号查手册会查到机床自己的条目。
/// 800000 段在机床两段之外，且是 SINUMERIK 用户循环报警的惯用段。
/// </summary>
public static class AlarmCodes
{
    /// <summary>未登记号的报警（不应出现在正式发布里）。</summary>
    public const int Unspecified = 0;

    /// <summary>号段下限。</summary>
    public const int RangeStart = 800000;

    /// <summary>号段上限。</summary>
    public const int RangeEnd = 800999;

    /// <summary>
    /// 机床一般故障报警的起点（说明书给的分界）。本系统的号段必须整段落在它之上，
    /// 有测试盯着这一条——号段撞了，现场查到的就是另一本手册上的故障。
    /// </summary>
    public const int MachineGeneralFaultRangeStart = 700040;

    /// <summary>与机床通信失败。</summary>
    public const int GatewayFailure = 800001;

    /// <summary>工艺数据不合法。</summary>
    public const int DomainFailure = 800002;

    /// <summary>未预期的错误。</summary>
    public const int UnexpectedFailure = 800003;

    /// <summary>与机床的连接中断。</summary>
    public const int ConnectionLost = 800010;

    /// <summary>与机床的连接已恢复。</summary>
    public const int ConnectionRestored = 800011;

    /// <summary>参数已下发。</summary>
    public const int HandoverCompleted = 800020;

    /// <summary>参数已下发但记录未入库。</summary>
    public const int HandoverNotArchived = 800021;

    /// <summary>磨削进行当中改了工序参数。</summary>
    public const int StepParametersUpdated = 800022;

    /// <summary>磨削当中跳到了另一道工序。</summary>
    public const int StepJumped = 800023;

    /// <summary>磨削当中把某一道工序提前结束了。</summary>
    public const int StepEndedEarly = 800024;

    /// <summary>报表没出来（打印是顺带做的，不影响磨削）。</summary>
    public const int ReportNotPrinted = 800025;

    /// <summary>tagmap 缺少必需变量。</summary>
    public const int TagMissing = 800030;

    /// <summary>号是否落在本系统号段内。</summary>
    public static bool IsHmiCode(int code) => code is >= RangeStart and <= RangeEnd;
}

/// <summary>报警级别。</summary>
public enum AlarmSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// 一条报警。只带资源键与技术细节，界面文字由界面层按语言取。
/// </summary>
/// <param name="Id">序号，单调递增。</param>
/// <param name="RaisedAtUtc">发生时刻（UTC）。</param>
/// <param name="Severity">级别。</param>
/// <param name="MessageResourceKey">界面文案的资源键。</param>
/// <param name="Detail">技术细节（异常消息等），不翻译。</param>
/// <param name="Code">报警号。上位机自己的报警占 800000–800999 号段，
/// 避开机床既有号段；来自 NC/PLC 的报警沿用机床给的号。</param>
public sealed record AlarmEntry(
    long Id,
    DateTimeOffset RaisedAtUtc,
    AlarmSeverity Severity,
    string MessageResourceKey,
    string? Detail,
    int Code = AlarmCodes.Unspecified);
