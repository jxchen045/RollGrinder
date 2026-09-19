using System;
using System.Collections.Generic;

namespace RollGrinder.Services.Alarms;

/// <summary>报警写入端。异常统一转成报警条目，禁止静默吞掉。</summary>
public interface IAlarmSink
{
    /// <summary>登记一条报警。</summary>
    void Raise(AlarmSeverity severity, string messageResourceKey, string? detail = null, int code = AlarmCodes.Unspecified);

    /// <summary>把异常转成报警：领域异常与网关异常分别对应不同资源键。</summary>
    void RaiseException(Exception exception);
}

/// <summary>报警读取端。</summary>
public interface IAlarmLog : IAlarmSink
{
    /// <summary>取当前报警列表的不可变快照，最新的在前。</summary>
    IReadOnlyList<AlarmEntry> Snapshot();

    /// <summary>清空。</summary>
    void Clear();

    /// <summary>报警列表变化时触发（可能来自后台线程）。</summary>
    event EventHandler? Changed;
}
