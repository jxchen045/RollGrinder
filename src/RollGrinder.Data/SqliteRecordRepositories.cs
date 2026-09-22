using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>磨削记录的 SQLite 实现。</summary>
public sealed class SqliteGrindingRecordRepository : IGrindingRecordRepository
{
    private readonly SqliteDatabase database;

    public SqliteGrindingRecordRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(GrindingRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO grinding_record (record_id, job_id, started_at_utc, finished_at_utc, state, note)
            VALUES ($id, $job, $started, $finished, $state, $note);
            """;
        SqlMapping.AddParameter(command, "$id", record.RecordId);
        SqlMapping.AddParameter(command, "$job", record.JobId);
        SqlMapping.AddParameter(command, "$started", SqlMapping.ToText(record.StartedAtUtc));
        SqlMapping.AddParameter(command, "$finished", record.FinishedAtUtc is null ? null : SqlMapping.ToText(record.FinishedAtUtc.Value));
        SqlMapping.AddParameter(command, "$state", (int)record.State);
        SqlMapping.AddParameter(command, "$note", record.Note);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FinishAsync(
        string recordId,
        DateTimeOffset finishedAtUtc,
        JobState state,
        string? note,
        double? wheelDiameterMm,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE grinding_record
            SET finished_at_utc = $finished,
                state = $state,
                note = COALESCE($note, note),
                wheel_diameter_mm = COALESCE($wheel, wheel_diameter_mm)
            WHERE record_id = $id;
            """;
        SqlMapping.AddParameter(command, "$finished", SqlMapping.ToText(finishedAtUtc));
        SqlMapping.AddParameter(command, "$state", (int)state);
        SqlMapping.AddParameter(command, "$note", note);
        SqlMapping.AddParameter(command, "$wheel", wheelDiameterMm);
        SqlMapping.AddParameter(command, "$id", recordId);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new DataStoreException($"Grinding record '{recordId}' does not exist.");
        }
    }

    public async Task<GrindingRecord?> GetAsync(string recordId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT record_id, job_id, started_at_utc, finished_at_utc, state, note, wheel_diameter_mm
            FROM grinding_record WHERE record_id = $id;
            """;
        SqlMapping.AddParameter(command, "$id", recordId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<GrindingRecord>> QueryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT record_id, job_id, started_at_utc, finished_at_utc, state, note, wheel_diameter_mm
            FROM grinding_record
            WHERE started_at_utc >= $from AND started_at_utc <= $to
            ORDER BY started_at_utc DESC
            LIMIT $limit;
            """;
        SqlMapping.AddParameter(command, "$from", SqlMapping.ToText(fromUtc));
        SqlMapping.AddParameter(command, "$to", SqlMapping.ToText(toUtc));
        SqlMapping.AddParameter(command, "$limit", limit);

        var records = new List<GrindingRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(Map(reader));
        }

        return records;
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM grinding_record WHERE started_at_utc < $threshold;";
        SqlMapping.AddParameter(command, "$threshold", SqlMapping.ToText(thresholdUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GrindingRecord>> QueryByRollAsync(
        string rollId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // 辊号挂在作业上，记录只指向作业，所以得连一次。
        command.CommandText =
            """
            SELECT r.record_id, r.job_id, r.started_at_utc, r.finished_at_utc, r.state, r.note, r.wheel_diameter_mm
            FROM grinding_record r
            JOIN job j ON j.job_id = r.job_id
            WHERE j.roll_id = $roll
            ORDER BY r.started_at_utc DESC
            LIMIT $limit;
            """;
        SqlMapping.AddParameter(command, "$roll", rollId);
        SqlMapping.AddParameter(command, "$limit", limit);

        var records = new List<GrindingRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(Map(reader));
        }

        return records;
    }

    private static GrindingRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        SqlMapping.ToTimestamp(reader.GetString(2)),
        reader.IsDBNull(3) ? null : SqlMapping.ToTimestamp(reader.GetString(3)),
        (JobState)reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5))
    {
        WheelDiameterMm = reader.IsDBNull(6) ? null : reader.GetDouble(6),
    };
}

/// <summary>测量结果的 SQLite 实现。</summary>
public sealed class SqliteMeasurementRepository : IMeasurementRepository
{
    private readonly SqliteDatabase database;

    public SqliteMeasurementRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(MeasurementRecord measurement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO measurement (measurement_id, job_id, recorded_at_utc, source, stage)
                VALUES ($id, $job, $recorded, $source, $stage);
                """;
            SqlMapping.AddParameter(command, "$id", measurement.MeasurementId);
            SqlMapping.AddParameter(command, "$job", measurement.JobId);
            SqlMapping.AddParameter(command, "$recorded", SqlMapping.ToText(measurement.RecordedAtUtc));
            SqlMapping.AddParameter(command, "$source", measurement.Source);
            SqlMapping.AddParameter(command, "$stage", (int)measurement.Stage);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (MeasurementPoint point in measurement.Profile.Points)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO measurement_point (measurement_id, body_position_mm, measured_radius_mm)
                VALUES ($id, $position, $radius);
                """;
            SqlMapping.AddParameter(command, "$id", measurement.MeasurementId);
            SqlMapping.AddParameter(command, "$position", point.BodyPositionMm);
            SqlMapping.AddParameter(command, "$radius", point.MeasuredRadiusMm);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MeasurementRecord?> GetLatestByJobAsync(string jobId, CancellationToken cancellationToken)
    {
        IReadOnlyList<MeasurementRecord> measurements = await ListByJobAsync(jobId, 1, cancellationToken).ConfigureAwait(false);
        return measurements.Count > 0 ? measurements[0] : null;
    }

    public async Task<MeasurementRecord?> GetLatestByStageAsync(
        string jobId, MeasurementStage stage, CancellationToken cancellationToken)
    {
        // 一支辊上磨前磨后各量一次，中间测量可能好几次——条数不多，
        // 取回来在内存里挑比在 SQL 里再写一条按阶段过滤的查询省事，也不会走样。
        IReadOnlyList<MeasurementRecord> all = await ListByJobAsync(jobId, 100, cancellationToken)
            .ConfigureAwait(false);

        foreach (MeasurementRecord measurement in all)
        {
            if (measurement.Stage == stage)
            {
                return measurement;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<MeasurementRecord>> ListByJobAsync(string jobId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var headers = new List<(string Id, DateTimeOffset RecordedAt, string Source, MeasurementStage Stage)>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT measurement_id, recorded_at_utc, source, stage
                FROM measurement WHERE job_id = $job
                ORDER BY recorded_at_utc DESC LIMIT $limit;
                """;
            SqlMapping.AddParameter(command, "$job", jobId);
            SqlMapping.AddParameter(command, "$limit", limit);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                headers.Add((
                    reader.GetString(0),
                    SqlMapping.ToTimestamp(reader.GetString(1)),
                    reader.GetString(2),
                    (MeasurementStage)reader.GetInt32(3)));
            }
        }

        var measurements = new List<MeasurementRecord>(headers.Count);
        foreach ((string id, DateTimeOffset recordedAt, string source, MeasurementStage stage) in headers)
        {
            var points = new List<MeasurementPoint>();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT body_position_mm, measured_radius_mm
                FROM measurement_point WHERE measurement_id = $id
                ORDER BY body_position_mm;
                """;
            SqlMapping.AddParameter(command, "$id", id);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                points.Add(new MeasurementPoint(reader.GetDouble(0), reader.GetDouble(1)));
            }

            measurements.Add(
                new MeasurementRecord(id, jobId, recordedAt, source, new MeasuredProfile(points)) { Stage = stage });
        }

        return measurements;
    }
}

/// <summary>补偿结果的 SQLite 实现。</summary>
public sealed class SqliteCompensationRepository : ICompensationRepository
{
    private readonly SqliteDatabase database;

    public SqliteCompensationRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(CompensationRecord compensation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compensation);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO compensation (compensation_id, job_id, created_at_utc, measurement_id)
                VALUES ($id, $job, $created, $measurement);
                """;
            SqlMapping.AddParameter(command, "$id", compensation.CompensationId);
            SqlMapping.AddParameter(command, "$job", compensation.JobId);
            SqlMapping.AddParameter(command, "$created", SqlMapping.ToText(compensation.CreatedAtUtc));
            SqlMapping.AddParameter(command, "$measurement", compensation.BasedOnMeasurementId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (ProfilePoint point in compensation.Points)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO compensation_point (compensation_id, body_position_mm, offset_radius_mm)
                VALUES ($id, $position, $offset);
                """;
            SqlMapping.AddParameter(command, "$id", compensation.CompensationId);
            SqlMapping.AddParameter(command, "$position", point.BodyPositionMm);
            SqlMapping.AddParameter(command, "$offset", point.RadiusOffsetMm);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompensationRecord?> GetLatestByJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);

        string compensationId;
        DateTimeOffset createdAt;
        string? measurementId;

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT compensation_id, created_at_utc, measurement_id
                FROM compensation WHERE job_id = $job
                ORDER BY created_at_utc DESC LIMIT 1;
                """;
            SqlMapping.AddParameter(command, "$job", jobId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            compensationId = reader.GetString(0);
            createdAt = SqlMapping.ToTimestamp(reader.GetString(1));
            measurementId = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var points = new List<ProfilePoint>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT body_position_mm, offset_radius_mm
                FROM compensation_point WHERE compensation_id = $id
                ORDER BY body_position_mm;
                """;
            SqlMapping.AddParameter(command, "$id", compensationId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                points.Add(new ProfilePoint(reader.GetDouble(0), reader.GetDouble(1)));
            }
        }

        return new CompensationRecord(compensationId, jobId, createdAt, measurementId, points);
    }

    public async Task<IReadOnlyList<CompensationRecord>> ListByJobAsync(
        string jobId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var headers = new List<(string Id, DateTimeOffset CreatedAt, string? MeasurementId)>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            // 从早到晚：收敛曲线的横坐标是"第几次迭代"，倒着取就把曲线画反了。
            command.CommandText =
                """
                SELECT compensation_id, created_at_utc, measurement_id
                FROM compensation WHERE job_id = $job
                ORDER BY created_at_utc LIMIT $limit;
                """;
            SqlMapping.AddParameter(command, "$job", jobId);
            SqlMapping.AddParameter(command, "$limit", limit);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                headers.Add((
                    reader.GetString(0),
                    SqlMapping.ToTimestamp(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var compensations = new List<CompensationRecord>(headers.Count);
        foreach ((string id, DateTimeOffset createdAt, string? measurementId) in headers)
        {
            var points = new List<ProfilePoint>();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT body_position_mm, offset_radius_mm
                FROM compensation_point WHERE compensation_id = $id
                ORDER BY body_position_mm;
                """;
            SqlMapping.AddParameter(command, "$id", id);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                points.Add(new ProfilePoint(reader.GetDouble(0), reader.GetDouble(1)));
            }

            compensations.Add(new CompensationRecord(id, jobId, createdAt, measurementId, points));
        }

        return compensations;
    }

    public async Task<int> CountByJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM compensation WHERE job_id = $job;";
        SqlMapping.AddParameter(command, "$job", jobId);

        object? count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return count is null ? 0 : Convert.ToInt32(count, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>报警归档的 SQLite 实现。</summary>
public sealed class SqliteAlarmRepository : IAlarmRepository
{
    private readonly SqliteDatabase database;

    public SqliteAlarmRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(
        DateTimeOffset raisedAtUtc,
        int severity,
        string messageResourceKey,
        string? detail,
        int code,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageResourceKey);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO alarm (raised_at_utc, severity, message_key, detail, code)
            VALUES ($raised, $severity, $key, $detail, $code);
            """;
        SqlMapping.AddParameter(command, "$raised", SqlMapping.ToText(raisedAtUtc));
        SqlMapping.AddParameter(command, "$severity", severity);
        SqlMapping.AddParameter(command, "$key", messageResourceKey);
        SqlMapping.AddParameter(command, "$detail", detail);
        SqlMapping.AddParameter(command, "$code", code);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlarmRecord>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT alarm_id, raised_at_utc, severity, message_key, detail, code
            FROM alarm ORDER BY alarm_id DESC LIMIT $limit;
            """;
        SqlMapping.AddParameter(command, "$limit", limit);

        var alarms = new List<AlarmRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            alarms.Add(new AlarmRecord(
                reader.GetInt64(0),
                SqlMapping.ToTimestamp(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5)));
        }

        return alarms;
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alarm WHERE raised_at_utc < $threshold;";
        SqlMapping.AddParameter(command, "$threshold", SqlMapping.ToText(thresholdUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
