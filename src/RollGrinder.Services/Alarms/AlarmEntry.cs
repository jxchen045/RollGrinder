using System;

namespace RollGrinder.Services.Alarms;

/// <summary>
/// 上位机报警号。号段 720000–720999 是本系统专用，避开机床既有号段；
/// 新增一条报警在这里登记一个号，现场按号查，不靠文案。
/// </summary>
public static class AlarmCodes
{
    /// <summary>未登记号的报警（不应出现在正式发布里）。</summary>
    public const int Unspecified = 0;

    /// <summary>号段下限。</summary>
    public const int RangeStart = 720000;

    /// <summary>号段上限。</summary>
    public const int RangeEnd = 720999;

    /// <summary>与机床通信失败。</summary>
    public const int GatewayFailure = 720001;

    /// <summary>工艺数据不合法。</summary>
    public const int DomainFailure = 720002;

    /// <summary>未预期的错误。</summary>
    public const int UnexpectedFailure = 720003;

    /// <summary>与机床的连接中断。</summary>
    public const int ConnectionLost = 720010;

    /// <summary>与机床的连接已恢复。</summary>
    public const int ConnectionRestored = 720011;

    /// <summary>参数已下发。</summary>
    public const int HandoverCompleted = 720020;

    /// <summary>参数已下发但记录未入库。</summary>
    public const int HandoverNotArchived = 720021;

    /// <summary>tagmap 缺少必需变量。</summary>
    public const int TagMissing = 720030;

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
/// <param name="Code">报警号。上位机自己的报警占 720000–720999 号段，
/// 避开机床既有号段；来自 NC/PLC 的报警沿用机床给的号。</param>
public sealed record AlarmEntry(
    long Id,
    DateTimeOffset RaisedAtUtc,
    AlarmSeverity Severity,
    string MessageResourceKey,
    string? Detail,
    int Code = AlarmCodes.Unspecified);
