using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <inheritdoc cref="IRunLogRepository"/>
public sealed class SqliteRunLogRepository : IRunLogRepository
{
    private readonly SqliteDatabase database;

    public SqliteRunLogRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AppendPassesAsync(string recordId, IReadOnlyList<RecordPassRow> rows, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (RecordPassRow row in rows)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR REPLACE INTO record_pass
                    (record_id, seq, step_order, pass, left_z, left_dwell, left_override, left_x, right_z, right_dwell, right_override, right_x,
                     compensation, stroke_version, drf, mean_current, edit_applied, read_at_utc)
                VALUES ($r, $seq, $step, $pass, $lz, $ld, $lo, $lx, $rz, $rd, $ro, $rx, $comp, $ver, $drf, $cur, $edit, $at);
                """;
            SqlMapping.AddParameter(command, "$r", recordId);
            SqlMapping.AddParameter(command, "$seq", row.Sequence);
            SqlMapping.AddParameter(command, "$step", row.StepOrder);
            SqlMapping.AddParameter(command, "$pass", row.Pass);
            SqlMapping.AddParameter(command, "$lz", row.LeftZMm);
            SqlMapping.AddParameter(command, "$ld", row.LeftDwellSeconds);
            SqlMapping.AddParameter(command, "$lo", row.LeftOverridePercent);
            SqlMapping.AddParameter(command, "$lx", row.LeftXMm);
            SqlMapping.AddParameter(command, "$rz", row.RightZMm);
            SqlMapping.AddParameter(command, "$rd", row.RightDwellSeconds);
            SqlMapping.AddParameter(command, "$ro", row.RightOverridePercent);
            SqlMapping.AddParameter(command, "$rx", row.RightXMm);
            SqlMapping.AddParameter(command, "$comp", row.CompensationMicrometer);
            SqlMapping.AddParameter(command, "$ver", row.StrokeVersion);
            SqlMapping.AddParameter(command, "$drf", row.DrfOffsetMm);
            SqlMapping.AddParameter(command, "$cur", row.MeanCurrentA);
            SqlMapping.AddParameter(command, "$edit", row.EditAppliedSeq);
            SqlMapping.AddParameter(command, "$at", SqlMapping.ToText(row.ReadAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecordPassRow>> ListPassesAsync(string recordId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT seq, step_order, pass, left_z, left_dwell, left_override, left_x, right_z, right_dwell, right_override, right_x,
                   compensation, stroke_version, drf, mean_current, edit_applied, read_at_utc
            FROM record_pass WHERE record_id = $r ORDER BY seq;
            """;
        SqlMapping.AddParameter(command, "$r", recordId);
        var list = new List<RecordPassRow>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            double? D(int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);
            int? I(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
            list.Add(new RecordPassRow(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                D(3), D(4), D(5), D(6), D(7), D(8), D(9), D(10), D(11), I(12), D(13), D(14), I(15),
                SqlMapping.ToTimestamp(reader.GetString(16))));
        }

        return list;
    }

    public async Task SaveOverridesAsync(string recordId, IReadOnlyList<OverrideSummaryRow> rows, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (OverrideSummaryRow row in rows)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR REPLACE INTO record_override (record_id, step_order, pass, kind, mean, min, max, off_share, seconds)
                VALUES ($r, $step, $pass, $kind, $mean, $min, $max, $off, $sec);
                """;
            SqlMapping.AddParameter(command, "$r", recordId);
            SqlMapping.AddParameter(command, "$step", row.StepOrder);
            SqlMapping.AddParameter(command, "$pass", row.Pass);
            SqlMapping.AddParameter(command, "$kind", row.Kind);
            SqlMapping.AddParameter(command, "$mean", row.MeanPercent);
            SqlMapping.AddParameter(command, "$min", row.MinPercent);
            SqlMapping.AddParameter(command, "$max", row.MaxPercent);
            SqlMapping.AddParameter(command, "$off", row.OffNominalShare);
            SqlMapping.AddParameter(command, "$sec", row.Seconds);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OverrideSummaryRow>> ListOverridesAsync(string recordId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT step_order, pass, kind, mean, min, max, off_share, seconds FROM record_override WHERE record_id = $r ORDER BY step_order, pass, kind;";
        SqlMapping.AddParameter(command, "$r", recordId);
        var list = new List<OverrideSummaryRow>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new OverrideSummaryRow(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4),
                reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7)));
        }

        return list;
    }

    public async Task AddEventAsync(string recordId, RunEvent runEvent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(runEvent);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO record_event (record_id, seq, at_utc, kind, step_order, pass, value, detail)
            VALUES ($r, (SELECT COALESCE(MAX(seq), 0) + 1 FROM record_event WHERE record_id = $r), $at, $kind, $step, $pass, $value, $detail);
            """;
        SqlMapping.AddParameter(command, "$r", recordId);
        SqlMapping.AddParameter(command, "$at", SqlMapping.ToText(runEvent.AtUtc));
        SqlMapping.AddParameter(command, "$kind", runEvent.Kind);
        SqlMapping.AddParameter(command, "$step", runEvent.StepOrder);
        SqlMapping.AddParameter(command, "$pass", runEvent.Pass);
        SqlMapping.AddParameter(command, "$value", runEvent.Value);
        SqlMapping.AddParameter(command, "$detail", runEvent.Detail);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunEvent>> ListEventsAsync(string recordId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT at_utc, kind, step_order, pass, value, detail FROM record_event WHERE record_id = $r ORDER BY seq;";
        SqlMapping.AddParameter(command, "$r", recordId);
        var list = new List<RunEvent>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new RunEvent(
                SqlMapping.ToTimestamp(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return list;
    }

    public async Task<IReadOnlyDictionary<string, RunEventCounts>> CountEventsAsync(
        IReadOnlyList<string> recordIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        var result = new Dictionary<string, RunEventCounts>(StringComparer.Ordinal);
        if (recordIds.Count == 0)
        {
            return result;
        }

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (string[] chunk in recordIds.Distinct(StringComparer.Ordinal).Chunk(200))
        {
            await using SqliteCommand command = connection.CreateCommand();
            string[] names = chunk.Select((_, i) => "$id" + i).ToArray();
            command.CommandText =
                $"""
                SELECT record_id,
                       SUM(CASE WHEN kind IN ('{RunEventKinds.OverrideLow}', '{RunEventKinds.OverrideHigh}', '{RunEventKinds.OverrideWholePass}') THEN 1 ELSE 0 END),
                       SUM(CASE WHEN kind = '{RunEventKinds.PassMissing}' THEN COALESCE(value, 1) ELSE 0 END)
                FROM record_event WHERE record_id IN ({string.Join(", ", names)}) GROUP BY record_id;
                """;
            for (int i = 0; i < chunk.Length; i++)
            {
                SqlMapping.AddParameter(command, names[i], chunk[i]);
            }

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var counts = new RunEventCounts(reader.GetInt32(1), (int)reader.GetDouble(2));
                if (counts.OutOfBound > 0 || counts.MissingPasses > 0)
                {
                    result[reader.GetString(0)] = counts;
                }
            }
        }

        return result;
    }

    public async Task AppendTraceAsync(string recordId, IReadOnlyList<TracePoint> points, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (TracePoint point in points)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO record_trace (record_id, kind, t_seconds, value) VALUES ($r, $kind, $t, $v);";
            SqlMapping.AddParameter(command, "$r", recordId);
            SqlMapping.AddParameter(command, "$kind", point.Kind);
            SqlMapping.AddParameter(command, "$t", point.Seconds);
            SqlMapping.AddParameter(command, "$v", point.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TracePoint>> ListTraceAsync(string recordId, string kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT t_seconds, value FROM record_trace WHERE record_id = $r AND kind = $kind ORDER BY t_seconds;";
        SqlMapping.AddParameter(command, "$r", recordId);
        SqlMapping.AddParameter(command, "$kind", kind);
        var list = new List<TracePoint>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new TracePoint(kind, reader.GetDouble(0), reader.GetDouble(1)));
        }

        return list;
    }

    public async Task SetNcInfoAsync(string recordId, RecordNcInfo info, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(info);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO record_nc (record_id, download_seq, accepted_at_utc) VALUES ($r, $seq, $at);";
        SqlMapping.AddParameter(command, "$r", recordId);
        SqlMapping.AddParameter(command, "$seq", info.DownloadSequence);
        SqlMapping.AddParameter(command, "$at", info.AcceptedAtUtc is { } at ? SqlMapping.ToText(at) : null);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecordNcInfo?> GetNcInfoAsync(string recordId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT download_seq, accepted_at_utc FROM record_nc WHERE record_id = $r;";
        SqlMapping.AddParameter(command, "$r", recordId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RecordNcInfo(
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : SqlMapping.ToTimestamp(reader.GetString(1)));
    }
}
