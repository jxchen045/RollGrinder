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
                             created_at_utc, profile_id, profile_name, program_id, program_name,
                             profile_version, program_version, start_diameter_mm, stock_um,
                             deviation_kind, deviation_reason, regrind_of, scrap_diameter_mm, grind_start_z_mm,
                             plan_id, plan_no)
            VALUES ($job, $roll, $profile, $length, $radius, $state, $created,
                    $profileId, $profileName, $programId, $programName,
                    $profileVersion, $programVersion, $start, $stock,
                    $deviation, $reason, $regrindOf, $scrap, $startZ,
                    $planId, $planNo)
            ON CONFLICT(job_id) DO UPDATE SET
                roll_id = excluded.roll_id,
                profile_type_key = excluded.profile_type_key,
                body_length_mm = excluded.body_length_mm,
                nominal_radius_mm = excluded.nominal_radius_mm,
                state = excluded.state,
                profile_id = excluded.profile_id,
                profile_name = excluded.profile_name,
                program_id = excluded.program_id,
                program_name = excluded.program_name,
                profile_version = excluded.profile_version,
                program_version = excluded.program_version,
                start_diameter_mm = excluded.start_diameter_mm,
                stock_um = excluded.stock_um,
                deviation_kind = excluded.deviation_kind,
                deviation_reason = excluded.deviation_reason,
                regrind_of = excluded.regrind_of,
                scrap_diameter_mm = excluded.scrap_diameter_mm,
                grind_start_z_mm = excluded.grind_start_z_mm,
                plan_id = excluded.plan_id,
                plan_no = excluded.plan_no;
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
                SqlMapping.AddParameter(command, "$programId", job.ProgramId);
                SqlMapping.AddParameter(command, "$programName", job.ProgramName);
                SqlMapping.AddParameter(command, "$length", job.Geometry.BodyLengthMm);
                SqlMapping.AddParameter(command, "$radius", job.Geometry.NominalRadiusMm);
                SqlMapping.AddParameter(command, "$state", (int)state);
                SqlMapping.AddParameter(command, "$created", SqlMapping.ToText(DateTimeOffset.UtcNow));
                SqlMapping.AddParameter(command, "$profileVersion", job.ProfileVersion);
                SqlMapping.AddParameter(command, "$programVersion", job.ProgramVersion);
                SqlMapping.AddParameter(command, "$start", job.StartDiameterMm);
                SqlMapping.AddParameter(command, "$stock", job.StockMicrometer);
                SqlMapping.AddParameter(command, "$deviation", (int)job.Deviation);
                SqlMapping.AddParameter(command, "$reason", job.DeviationReason);
                SqlMapping.AddParameter(command, "$regrindOf", job.RegrindOfJobId);
                SqlMapping.AddParameter(command, "$scrap", job.ScrapDiameterMm);
                SqlMapping.AddParameter(command, "$startZ", job.GrindStartZMm);
                SqlMapping.AddParameter(command, "$planId", job.PlanId);
                SqlMapping.AddParameter(command, "$planNo", job.PlanNo);
            }).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM job_step WHERE job_id = $job;", cancellationToken,
            command => SqlMapping.AddParameter(command, "$job", job.JobId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM job_parameter WHERE job_id = $job;", cancellationToken,
            command => SqlMapping.AddParameter(command, "$job", job.JobId)).ConfigureAwait(false);

        await ProfileSegmentMapping.WriteAsync(
            connection, transaction, ProfileSegmentTables.JobSnapshot, job.JobId, job.Profile, cancellationToken)
            .ConfigureAwait(false);

        // 本次调整（设计说明 5.4）：整份替换，顺序就是改的先后。
        await ExecuteAsync(connection, transaction, "DELETE FROM job_adjustment WHERE job_id = $job;", cancellationToken,
            command => SqlMapping.AddParameter(command, "$job", job.JobId)).ConfigureAwait(false);
        int seq = 0;
        foreach (JobAdjustment adjustment in job.Adjustments)
        {
            int at = ++seq;
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO job_adjustment
                    (job_id, seq, step_order, parameter_key, plan_value, job_value, running, changed_by, changed_at_utc, edit_seq, effective_step, effective_pass)
                VALUES ($job, $seq, $step, $key, $plan, $value, $running, $by, $at, $edit, $estep, $epass);
                """,
                cancellationToken,
                command =>
                {
                    SqlMapping.AddParameter(command, "$job", job.JobId);
                    SqlMapping.AddParameter(command, "$seq", at);
                    SqlMapping.AddParameter(command, "$step", adjustment.StepOrder);
                    SqlMapping.AddParameter(command, "$key", adjustment.ParameterKey);
                    SqlMapping.AddParameter(command, "$plan", adjustment.PlanValue);
                    SqlMapping.AddParameter(command, "$value", adjustment.JobValue);
                    SqlMapping.AddParameter(command, "$running", adjustment.Running ? 1 : 0);
                    SqlMapping.AddParameter(command, "$by", adjustment.ChangedBy);
                    SqlMapping.AddParameter(command, "$at", SqlMapping.ToText(adjustment.ChangedAtUtc));
                    SqlMapping.AddParameter(command, "$edit", adjustment.EditSeq);
                    SqlMapping.AddParameter(command, "$estep", adjustment.EffectiveStepOrder);
                    SqlMapping.AddParameter(command, "$epass", adjustment.EffectivePass);
                }).ConfigureAwait(false);
        }

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
        string? programId;
        string? programName;
        RollGeometry geometry;
        JobState state;
        JobExtras extras;

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT roll_id, profile_type_key, body_length_mm, nominal_radius_mm, state,
                       profile_id, profile_name, program_id, program_name,
                       profile_version, program_version, start_diameter_mm, stock_um,
                       deviation_kind, deviation_reason, regrind_of, scrap_diameter_mm, grind_start_z_mm,
                       plan_id, plan_no
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
            programId = reader.IsDBNull(7) ? null : reader.GetString(7);
            programName = reader.IsDBNull(8) ? null : reader.GetString(8);
            extras = new JobExtras(
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetDouble(11),
                reader.IsDBNull(12) ? null : reader.GetDouble(12),
                (JobDeviation)reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetDouble(16),
                reader.IsDBNull(17) ? null : reader.GetDouble(17))
            {
                PlanId = reader.IsDBNull(18) ? null : reader.GetString(18),
                PlanNo = reader.IsDBNull(19) ? null : reader.GetInt32(19),
            };
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
            ProgramId = programId,
            ProgramName = programName,
            ProfileVersion = extras.ProfileVersion,
            ProgramVersion = extras.ProgramVersion,
            StartDiameterMm = extras.StartDiameterMm,
            StockMicrometer = extras.StockMicrometer,
            Deviation = extras.Deviation,
            DeviationReason = extras.DeviationReason,
            RegrindOfJobId = extras.RegrindOf,
            ScrapDiameterMm = extras.ScrapDiameterMm,
            GrindStartZMm = extras.GrindStartZMm,
            PlanId = extras.PlanId,
            PlanNo = extras.PlanNo,
            Adjustments = await ReadAdjustmentsAsync(connection, jobId, cancellationToken).ConfigureAwait(false),
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

    public async Task<IReadOnlyList<JobListEntry>> ListByStateAsync(JobState state, int limit, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id, roll_id, created_at_utc, regrind_of FROM job
            WHERE state = $state ORDER BY created_at_utc DESC LIMIT $limit;
            """;
        SqlMapping.AddParameter(command, "$state", (int)state);
        SqlMapping.AddParameter(command, "$limit", limit);

        var jobs = new List<JobListEntry>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(new JobListEntry(
                reader.GetString(0), reader.GetString(1), SqlMapping.ToTimestamp(reader.GetString(2)), state,
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return jobs;
    }

    public async Task<int> MarkAdjustmentsEffectiveAsync(
        string jobId, int appliedSeq, int currentStepOrder, int currentPass, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // 改的是正在跑的那一道：从这一道次起；改的是后面的工序：从那道工序第 1 道次起。
        command.CommandText =
            """
            UPDATE job_adjustment
            SET effective_step = step_order,
                effective_pass = CASE WHEN step_order = $step THEN $pass ELSE 1 END
            WHERE job_id = $job AND edit_seq IS NOT NULL AND edit_seq <= $applied AND effective_pass IS NULL;
            """;
        SqlMapping.AddParameter(command, "$job", jobId);
        SqlMapping.AddParameter(command, "$applied", appliedSeq);
        SqlMapping.AddParameter(command, "$step", currentStepOrder);
        SqlMapping.AddParameter(command, "$pass", Math.Max(1, currentPass));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<JobAdjustment>> ReadAdjustmentsAsync(
        SqliteConnection connection, string jobId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT step_order, parameter_key, plan_value, job_value, running, changed_by, changed_at_utc, edit_seq, effective_step, effective_pass
            FROM job_adjustment WHERE job_id = $job ORDER BY seq;
            """;
        SqlMapping.AddParameter(command, "$job", jobId);
        var list = new List<JobAdjustment>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new JobAdjustment(
                reader.GetInt32(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetInt32(4) != 0,
                reader.GetString(5), SqlMapping.ToTimestamp(reader.GetString(6)))
            {
                EditSeq = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                EffectiveStepOrder = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                EffectivePass = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            });
        }

        return list;
    }

    private sealed record JobExtras(
        int? ProfileVersion,
        int? ProgramVersion,
        double? StartDiameterMm,
        double? StockMicrometer,
        JobDeviation Deviation,
        string? DeviationReason,
        string? RegrindOf,
        double? ScrapDiameterMm,
        double? GrindStartZMm)
    {
        public string? PlanId { get; init; }

        public int? PlanNo { get; init; }
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
