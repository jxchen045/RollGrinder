using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>改动记录的仓储。</summary>
public interface IChangeLogRepository
{
    /// <summary>一次改动的几项一起记（一个事务）。</summary>
    Task AddAsync(IReadOnlyList<ChangeLogEntry> entries, CancellationToken cancellationToken);

    /// <summary>最近的若干条，新的在前。</summary>
    Task<IReadOnlyList<ChangeLogEntry>> ListAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>改动记录的 SQLite 实现。</summary>
public sealed class SqliteChangeLogRepository : IChangeLogRepository
{
    private readonly SqliteDatabase database;

    public SqliteChangeLogRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(IReadOnlyList<ChangeLogEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (ChangeLogEntry entry in entries)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO change_log (entry_id, changed_at_utc, changed_by, area, item, old_value, new_value)
                VALUES ($id, $at, $by, $area, $item, $old, $new);
                """;
            SqlMapping.AddParameter(command, "$id", entry.EntryId);
            SqlMapping.AddParameter(command, "$at", SqlMapping.ToText(entry.ChangedAtUtc));
            SqlMapping.AddParameter(command, "$by", entry.ChangedBy);
            SqlMapping.AddParameter(command, "$area", entry.Area);
            SqlMapping.AddParameter(command, "$item", entry.Item);
            SqlMapping.AddParameter(command, "$old", entry.OldValue);
            SqlMapping.AddParameter(command, "$new", entry.NewValue);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChangeLogEntry>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entry_id, changed_at_utc, changed_by, area, item, old_value, new_value
            FROM change_log ORDER BY changed_at_utc DESC, rowid DESC LIMIT $limit;
            """;
        SqlMapping.AddParameter(command, "$limit", Math.Max(1, limit));

        var entries = new List<ChangeLogEntry>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new ChangeLogEntry(
                reader.GetString(0),
                SqlMapping.ToTimestamp(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return entries;
    }
}

/// <summary>上位机设定覆盖值的仓储（键值对）：没有覆盖的项，上层用配置文件里的值。</summary>
public interface IHmiSettingRepository
{
    Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>写入（有则改、无则加）。</summary>
    Task SaveAsync(IReadOnlyDictionary<string, string> values, string changedBy, DateTimeOffset changedAtUtc, CancellationToken cancellationToken);

    /// <summary>删掉这几项的覆盖，回到配置文件的值。</summary>
    Task RemoveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}

/// <summary>设定覆盖值的 SQLite 实现。</summary>
public sealed class SqliteHmiSettingRepository : IHmiSettingRepository
{
    private readonly SqliteDatabase database;

    public SqliteHmiSettingRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, value FROM hmi_setting;";

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, string> values,
        string changedBy,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach ((string key, string value) in values)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO hmi_setting (setting_key, value, changed_at_utc, changed_by)
                VALUES ($key, $value, $at, $by)
                ON CONFLICT (setting_key) DO UPDATE SET value = $value, changed_at_utc = $at, changed_by = $by;
                """;
            SqlMapping.AddParameter(command, "$key", key);
            SqlMapping.AddParameter(command, "$value", value);
            SqlMapping.AddParameter(command, "$at", SqlMapping.ToText(changedAtUtc));
            SqlMapping.AddParameter(command, "$by", changedBy);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (string key in keys)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM hmi_setting WHERE setting_key = $key;";
            SqlMapping.AddParameter(command, "$key", key);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
