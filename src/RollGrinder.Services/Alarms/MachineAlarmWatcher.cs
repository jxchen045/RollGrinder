using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Alarms;

/// <summary>
/// 把机床自己的报警搬进上位机的报警表。
///
/// 为什么要搬：操作工面前是一块屏，上位机与 SINUMERIK Operate 共用它。
/// 机床报警只在 Operate 那一侧显示的话，人得来回切屏才知道"停下来是因为什么"。
/// 上位机的报警号段当初特意挪出机床的两段（见 <see cref="AlarmCodes"/>），
/// 为的就是两边并排显示时按号一眼分得清是谁的报警。
///
/// **这里只搬不判。** 号、文本、严重与否全按机床给的来：
/// 严重与一般的分界是说明书划的 700040，不是上位机的判断。
/// 机床报警的处理、复位、联锁一律在 NC/PLC 侧——上位机连读都是只读。
/// </summary>
public sealed class MachineAlarmWatcher : IHostedService
{
    /// <summary>机床报警在上位机这边的文案键。真正的说明按号查机床手册。</summary>
    public const string MachineAlarmResourceKey = "Alarm_MachineAlarm";

    /// <summary>机床报警消失时的文案键。</summary>
    public const string MachineAlarmClearedResourceKey = "Alarm_MachineAlarmCleared";

    private readonly IMachineMonitor monitor;
    private readonly IAlarmSink alarms;

    /// <summary>上一拍还挂着的报警号 → 文本。用来只报"新来的"与"刚没的"。</summary>
    private readonly Dictionary<int, string?> active = new();

    public MachineAlarmWatcher(IMachineMonitor monitor, IAlarmSink alarms)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.monitor.SnapshotUpdated += OnSnapshot;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.monitor.SnapshotUpdated -= OnSnapshot;
        return Task.CompletedTask;
    }

    private void OnSnapshot(object? sender, MachineStateSnapshot snapshot) => Observe(snapshot);

    /// <summary>
    /// 机床报警的级别按**号段**定，不由上位机判断：
    /// 说明书把 700040 划为严重与一般的分界，PLC 报警从 500000 起。
    /// </summary>
    public static AlarmSeverity SeverityOf(int machineAlarmNumber) =>
        machineAlarmNumber >= AlarmCodes.MachineGeneralFaultRangeStart
            ? AlarmSeverity.Warning
            : AlarmSeverity.Error;

    /// <summary>当前还挂着的机床报警号，最先炸的在前。</summary>
    public IReadOnlyCollection<int> ActiveNumbers => this.active.Keys;

    private void Observe(MachineStateSnapshot snapshot)
    {
        if (snapshot is null || snapshot.ConnectionState != GatewayConnectionState.Connected)
        {
            // 断线时不清空：断线本身已经有一条报警，
            // 把机床报警一并抹掉会让人以为故障自己好了。
            return;
        }

        if (snapshot.GetNumberOrNull(MachineTagKeys.MachineAlarmCount) is not double count)
        {
            // tagmap 没映射机床报警：报警表里就只有上位机自己的，不装作读过。
            return;
        }

        var current = new Dictionary<int, string?>();
        int slots = Math.Min((int)count, MachineTagKeys.MachineAlarmSlots);
        for (int i = 0; i < slots; i++)
        {
            if (snapshot.GetNumberOrNull(MachineTagKeys.MachineAlarmNumberAt(i)) is not double number
                || number <= 0.0)
            {
                continue;
            }

            current[(int)number] = snapshot.GetTextOrNull(MachineTagKeys.MachineAlarmTextAt(i));
        }

        foreach (KeyValuePair<int, string?> alarm in current)
        {
            if (this.active.ContainsKey(alarm.Key))
            {
                continue;
            }

            // 同一条报警只报一次：机床上挂着的报警每一拍都读得到，
            // 每拍报一条会把报警表冲成一片同样的字。
            this.alarms.Raise(
                SeverityOf(alarm.Key),
                MachineAlarmResourceKey,
                alarm.Value ?? string.Empty,
                alarm.Key);
        }

        foreach (int number in new List<int>(this.active.Keys))
        {
            if (current.ContainsKey(number))
            {
                continue;
            }

            // 消失也留一条：现场要能看出"刚才那条什么时候被复位的"。
            this.alarms.Raise(AlarmSeverity.Information, MachineAlarmClearedResourceKey, null, number);
        }

        this.active.Clear();
        foreach (KeyValuePair<int, string?> alarm in current)
        {
            this.active[alarm.Key] = alarm.Value;
        }
    }
}
