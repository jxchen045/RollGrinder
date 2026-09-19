using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RollGrinder.Data;
using RollGrinder.Services.Alarms;

namespace RollGrinder.Services.Records;

/// <summary>
/// 把内存报警落到数据库，并在启动时清理过期记录。
/// 归档失败不能影响界面继续显示报警，因此这里只吞到日志级别的失败并继续。
/// </summary>
public sealed class AlarmArchiveHostedService : IHostedService, IDisposable
{
    private readonly IAlarmLog alarmLog;
    private readonly IAlarmRepository repository;
    private readonly IRecordService records;

    private long lastArchivedId;

    public AlarmArchiveHostedService(IAlarmLog alarmLog, IAlarmRepository repository, IRecordService records)
    {
        this.alarmLog = alarmLog ?? throw new ArgumentNullException(nameof(alarmLog));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.records.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        this.alarmLog.Changed += OnAlarmsChanged;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.alarmLog.Changed -= OnAlarmsChanged;
        return Task.CompletedTask;
    }

    public void Dispose() => this.alarmLog.Changed -= OnAlarmsChanged;

    private void OnAlarmsChanged(object? sender, EventArgs e)
    {
        // 事件来自后台线程，这里只投递不等待，避免拖慢取数循环。
        _ = ArchiveNewEntriesAsync();
    }

    private async Task ArchiveNewEntriesAsync()
    {
        foreach (AlarmEntry entry in this.alarmLog.Snapshot())
        {
            if (entry.Id <= Volatile.Read(ref this.lastArchivedId))
            {
                continue;
            }

            try
            {
                await this.repository.AddAsync(
                    entry.RaisedAtUtc,
                    (int)entry.Severity,
                    entry.MessageResourceKey,
                    entry.Detail,
                    entry.Code,
                    CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref this.lastArchivedId, entry.Id);
            }
            catch (Exception ex) when (ex is DataStoreException or Microsoft.Data.Sqlite.SqliteException)
            {
                // 归档失败不再回头报警，否则会自激；界面上的报警仍然在。
                return;
            }
        }
    }
}
