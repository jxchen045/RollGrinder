using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>作业仓储的 SQLite 实现。保存是整支作业的替换式写入，保证参数与工序一致。</summary>
public sealed class SqliteJobRepository : IJobRepository
{
    private readonly SqliteDatabase database;

    public SqliteJobRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task SaveAsync(GrindingJob job, JobState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO job (job_id, roll_id, profile_type_key, body_length_mm, nominal_radius_mm, state,
                             created_at_utc, profile_id, profile_name)
            VALUES ($job, $roll, $profile, $length, $radius, $state, $created, $profileId, $profileName)
            ON CONFLICT(job_id) DO UPDATE SET
                roll_id = excluded.roll_id,
                profile_type_key = excluded.profile_type_key,
                body_length_mm = excluded.body_length_mm,
                nominal_radius_mm = excluded.nominal_radius_mm,
                state = excluded.state,
                profile_id = excluded.profile_id,
                profile_name = excluded.profile_name;
            """,
            cancellationToken,
            command =>
            {
                SqlMapping.AddParameter(command, "$job", job.JobId);
                SqlMapping.AddParameter(command, "$roll", job.RollId);

                // profile_type_key 写第一段的类型：列表显示与旧库读路径都用它。
                SqlMapping.AddParameter(command, "$profile", job.ProfileTypeKey);
                SqlMapping.AddParameter(command, "$profileId", job.ProfileId);
                SqlMapping.AddParameter(command, "$profileName", job.ProfileName);
                SqlMapping.AddParameter(command, "$length", job.Geometry.BodyLengthMm);
                SqlMapping.AddParameter(command, "$radius", job.Geometry.NominalRadiusMm);
                SqlMapping.AddParameter(command, "$state", (int)state);
                SqlMapping.AddParameter(command, "$created", SqlMapping.ToText(DateTimeOffset.UtcNow));
            }).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM job_step WHERE job_id = $job;", cancellationToken,
            command => SqlMapping.AddParameter(command, "$job", job.JobId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM job_parameter WHERE job_id = $job;", cancellationToken,
            command => SqlMapping.AddParameter(command, "$job", job.JobId)).ConfigureAwait(false);

        await ProfileSegmentMapping.WriteAsync(
            connection, transaction, ProfileSegmentTables.JobSnapshot, job.JobId, job.Profile, cancellationToken)
            .ConfigureAwait(false);

        await SqlMapping.WriteParametersAsync(
            connection, transaction, job.JobId, SqlMapping.ProgramOptionStepOrder, job.ProgramOptions, cancellationToken)
            .ConfigureAwait(false);

        foreach (GrindingJobStep step in job.Steps)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO job_step (job_id, step_order, step_type_key) VALUES ($job, $order, $type);",
                cancellationToken,
                command =>
                {
                    SqlMapping.AddParameter(command, "$job", job.JobId);
                    SqlMapping.AddParameter(command, "$order", step.Order);
                    SqlMapping.AddParameter(command, "$type", step.StepTypeKey);
                }).ConfigureAwait(false);

            await SqlMapping.WriteParametersAsync(
                connection, transaction, job.JobId, step.Order, step.Parameters, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(GrindingJob Job, JobState State)?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);

        string rollId;
        string profileTypeKey;
        string? profileId;
        string? profileName;
        RollGeometry geometry;
        JobState state;

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT roll_id, profile_type_key, body_length_mm, nominal_radius_mm, state,
                       profile_id, profile_name
                FROM job WHERE job_id = $job;
                """;
            SqlMapping.AddParameter(command, "$job", jobId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            rollId = reader.GetString(0);
            profileTypeKey = reader.GetString(1);
            geometry = RollGeometry.Create(reader.GetDouble(2), reader.GetDouble(3));
            state = (JobState)reader.GetInt32(4);
            profileId = reader.IsDBNull(5) ? null : reader.GetString(5);
            profileName = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        CompositeRollProfile? storedProfile = await ProfileSegmentMapping
            .ReadAsync(connection, ProfileSegmentTables.JobSnapshot, jobId, cancellationToken).ConfigureAwait(false);

        Dictionary<int, ParameterSet> parameters = await SqlMapping
            .ReadParametersAsync(connection, jobId, cancellationToken).ConfigureAwait(false);

        var steps = new List<GrindingJobStep>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT step_order, step_type_key FROM job_step WHERE job_id = $job ORDER BY step_order;";
            SqlMapping.AddParameter(command, "$job", jobId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int order = reader.GetInt32(0);
                steps.Add(new GrindingJobStep(
                    order,
                    reader.GetString(1),
                    parameters.TryGetValue(order, out ParameterSet? stepParameters) ? stepParameters : ParameterSet.Empty));
            }
        }

        ParameterSet programOptions =
            parameters.TryGetValue(SqlMapping.ProgramOptionStepOrder, out ParameterSet? storedOptions)
                ? storedOptions
                : ParameterSet.Empty;

        // 没有段记录的是迁移前存下的作业：按老表示（profile_type_key + step_order 0 的参数）读回来，
        // 当成一段铺满全长的主辊形。旧作业照样打得开。
        CompositeRollProfile profile = storedProfile ?? CompositeRollProfile.Single(
            profileTypeKey,
            geometry,
            parameters.TryGetValue(SqlMapping.ProfileParameterStepOrder, out ParameterSet? legacy)
                ? legacy
                : ParameterSet.Empty);

        GrindingJob job = GrindingJob.Create(jobId, rollId, geometry, profile, steps, programOptions) with
        {
            ProfileId = profileId,
            ProfileName = profileName,
        };
        return (job, state);
    }

    public async Task SetStateAsync(string jobId, JobState state, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE job SET state = $state WHERE job_id = $job;";
        SqlMapping.AddParameter(command, "$state", (int)state);
        SqlMapping.AddParameter(command, "$job", jobId);

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new DataStoreException($"Job '{jobId}' does not exist.");
        }
    }

    public async Task<IReadOnlyList<string>> ListJobIdsByRollAsync(string rollId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT job_id FROM job WHERE roll_id = $roll ORDER BY created_at_utc DESC LIMIT $limit;";
        SqlMapping.AddParameter(command, "$roll", rollId);
        SqlMapping.AddParameter(command, "$limit", limit);

        var jobIds = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobIds.Add(reader.GetString(0));
        }

        return jobIds;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        Action<SqliteCommand> bind)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
