using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Data;

/// <summary>标定值仓储的 SQLite 实现。</summary>
public sealed class SqliteCalibrationRepository : ICalibrationRepository
{
    private readonly SqliteDatabase database;

    public SqliteCalibrationRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<ParameterSet> LoadAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadValuesAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CalibrationAudit>> LoadAuditAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT parameter_key, changed_at_utc, changed_by FROM machine_calibration ORDER BY parameter_key;";

        var audit = new List<CalibrationAudit>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            audit.Add(new CalibrationAudit(
                reader.GetString(0), SqlMapping.ToTimestamp(reader.GetString(1)), reader.GetString(2)));
        }

        return audit;
    }

    public async Task SaveAsync(ParameterSet values, string changedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        ParameterSet stored = await ReadValuesAsync(connection, cancellationToken).ConfigureAwait(false);
        string timestamp = SqlMapping.ToText(DateTimeOffset.UtcNow);

        foreach (KeyValuePair<string, ParameterValue> pair in values.ToOrderedPairs())
        {
            // 没变的项不重写：否则按一下保存，所有项的"最后改动"都会被刷成今天，
            // 追溯"上次换砂轮是什么时候"就没意义了。
            if (stored.TryGet(pair.Key, out ParameterValue? existing)
                && existing is not null
                && string.Equals(existing.ToInvariantString(), pair.Value.ToInvariantString(), StringComparison.Ordinal)
                && existing.Kind == pair.Value.Kind)
            {
                continue;
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO machine_calibration (parameter_key, value_kind, value_text, changed_at_utc, changed_by)
                VALUES ($key, $kind, $value, $at, $by)
                ON CONFLICT(parameter_key) DO UPDATE SET
                    value_kind = excluded.value_kind,
                    value_text = excluded.value_text,
                    changed_at_utc = excluded.changed_at_utc,
                    changed_by = excluded.changed_by;
                """;
            SqlMapping.AddParameter(command, "$key", pair.Key);
            SqlMapping.AddParameter(command, "$kind", (int)pair.Value.Kind);
            SqlMapping.AddParameter(command, "$value", pair.Value.ToInvariantString());
            SqlMapping.AddParameter(command, "$at", timestamp);
            SqlMapping.AddParameter(command, "$by", changedBy);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ParameterSet> ReadValuesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT parameter_key, value_kind, value_text FROM machine_calibration ORDER BY parameter_key;";

        var pairs = new List<KeyValuePair<string, ParameterValue>>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pairs.Add(new KeyValuePair<string, ParameterValue>(
                reader.GetString(0),
                ParameterValue.Parse((ParameterValueKind)reader.GetInt32(1), reader.GetString(2))));
        }

        return new ParameterSet(pairs);
    }
}
