using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>圆度测量的 SQLite 实现。</summary>
public sealed class SqliteRoundnessRepository : IRoundnessRepository
{
    private readonly SqliteDatabase database;

    public SqliteRoundnessRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(RoundnessMeasurement measurement, CancellationToken cancellationToken)
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
                INSERT INTO roundness_measurement (roundness_id, job_id, recorded_at_utc, source)
                VALUES ($id, $job, $recorded, $source);
                """;
            SqlMapping.AddParameter(command, "$id", measurement.RoundnessId);
            SqlMapping.AddParameter(command, "$job", measurement.JobId);
            SqlMapping.AddParameter(command, "$recorded", SqlMapping.ToText(measurement.RecordedAtUtc));
            SqlMapping.AddParameter(command, "$source", measurement.Source);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (RoundnessPoint point in measurement.Points)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO roundness_point
                    (roundness_id, body_position_mm, roundness_micrometer, eccentricity_micrometer)
                VALUES ($id, $position, $roundness, $eccentricity);
                """;
            SqlMapping.AddParameter(command, "$id", measurement.RoundnessId);
            SqlMapping.AddParameter(command, "$position", point.BodyPositionMm);
            SqlMapping.AddParameter(command, "$roundness", point.RoundnessMicrometer);
            SqlMapping.AddParameter(command, "$eccentricity", point.EccentricityMicrometer);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoundnessMeasurement?> GetLatestByJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);

        string? id = null;
        DateTimeOffset recordedAt = default;
        string source = string.Empty;

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT roundness_id, recorded_at_utc, source
                FROM roundness_measurement WHERE job_id = $job
                ORDER BY recorded_at_utc DESC LIMIT 1;
                """;
            SqlMapping.AddParameter(command, "$job", jobId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                id = reader.GetString(0);
                recordedAt = SqlMapping.ToTimestamp(reader.GetString(1));
                source = reader.GetString(2);
            }
        }

        if (id is null)
        {
            return null;
        }

        var points = new List<RoundnessPoint>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT body_position_mm, roundness_micrometer, eccentricity_micrometer
                FROM roundness_point WHERE roundness_id = $id
                ORDER BY body_position_mm;
                """;
            SqlMapping.AddParameter(command, "$id", id);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                points.Add(new RoundnessPoint(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2)));
            }
        }

        return new RoundnessMeasurement(id, jobId, recordedAt, source, points);
    }
}
