using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>砂轮修整与更换记录的仓储。</summary>
public interface IWheelEventRepository
{
    Task AddAsync(WheelEvent wheelEvent, CancellationToken cancellationToken);

    /// <summary>最近的若干条，新的在前。</summary>
    Task<IReadOnlyList<WheelEvent>> ListAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>砂轮记录的 SQLite 实现。</summary>
public sealed class SqliteWheelEventRepository : IWheelEventRepository
{
    private readonly SqliteDatabase database;

    public SqliteWheelEventRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(WheelEvent wheelEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wheelEvent);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO wheel_event (event_id, occurred_at_utc, kind, source, wheel_diameter_mm, changed_by, detail)
            VALUES ($id, $at, $kind, $source, $diameter, $by, $detail);
            """;
        SqlMapping.AddParameter(command, "$id", wheelEvent.EventId);
        SqlMapping.AddParameter(command, "$at", SqlMapping.ToText(wheelEvent.OccurredAtUtc));
        SqlMapping.AddParameter(command, "$kind", (int)wheelEvent.Kind);
        SqlMapping.AddParameter(command, "$source", (int)wheelEvent.Source);
        SqlMapping.AddParameter(command, "$diameter", wheelEvent.WheelDiameterMm);
        SqlMapping.AddParameter(command, "$by", wheelEvent.ChangedBy);
        SqlMapping.AddParameter(command, "$detail", wheelEvent.Detail);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WheelEvent>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, occurred_at_utc, kind, source, wheel_diameter_mm, changed_by, detail
            FROM wheel_event ORDER BY occurred_at_utc DESC LIMIT $limit;
            """;
        SqlMapping.AddParameter(command, "$limit", Math.Max(1, limit));

        var events = new List<WheelEvent>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new WheelEvent(
                reader.GetString(0),
                SqlMapping.ToTimestamp(reader.GetString(1)),
                (WheelEventKind)reader.GetInt32(2),
                (WheelEventSource)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return events;
    }
}
