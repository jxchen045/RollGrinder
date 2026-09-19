using System;

namespace RollGrinder.Services.Alarms;

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
public sealed record AlarmEntry(
    long Id,
    DateTimeOffset RaisedAtUtc,
    AlarmSeverity Severity,
    string MessageResourceKey,
    string? Detail);
