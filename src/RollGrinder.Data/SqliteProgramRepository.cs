using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Steps;

namespace RollGrinder.Data;

/// <summary>程序库的 SQLite 实现。保存是整支程序的替换式写入，工序与参数一起换。</summary>
public sealed class SqliteProgramRepository : IProgramRepository
{
    private readonly SqliteDatabase database;

    public SqliteProgramRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<ProgramSummary>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // 工序数从工序表里带出来，省得为了列个表把每支程序都读全。
        command.CommandText =
            """
            SELECT p.program_id, p.name, p.modified_at_utc,
                   (SELECT COUNT(*) FROM program_step s WHERE s.program_id = p.program_id)
            FROM program p
            ORDER BY p.modified_at_utc DESC
            LIMIT $limit;
            """;
        SqlMapping.AddParameter(command, "$limit", limit);

        var summaries = new List<ProgramSummary>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            summaries.Add(new ProgramSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(3),
                SqlMapping.ToTimestamp(reader.GetString(2))));
        }

        return summaries;
    }

    public async Task<GrindingProgram?> GetAsync(string programId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);

        string name;
        DateTimeOffset createdAtUtc;
        DateTimeOffset modifiedAtUtc;

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, created_at_utc, modified_at_utc FROM program WHERE program_id = $id;";
            SqlMapping.AddParameter(command, "$id", programId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            name = reader.GetString(0);
            createdAtUtc = SqlMapping.ToTimestamp(reader.GetString(1));
            modifiedAtUtc = SqlMapping.ToTimestamp(reader.GetString(2));
        }

        Dictionary<int, ParameterSet> parameters = await SqlMapping
            .ReadParametersAsync(connection, programId, cancellationToken, SqlMapping.ProgramParameters)
            .ConfigureAwait(false);

        var steps = new List<GrindingJobStep>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT step_order, step_type_key FROM program_step WHERE program_id = $id ORDER BY step_order;";
            SqlMapping.AddParameter(command, "$id", programId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int order = reader.GetInt32(0);
                steps.Add(new GrindingJobStep(
                    order,
                    reader.GetString(1),
                    parameters.TryGetValue(order, out ParameterSet? stepParameters)
                        ? stepParameters
                        : ParameterSet.Empty));
            }
        }

        if (steps.Count == 0)
        {
            // 有条目没有工序：库坏了，不猜一支程序出来。
            throw new DataStoreException($"Program '{programId}' has no steps.");
        }

        ParameterSet programOptions =
            parameters.TryGetValue(SqlMapping.ProgramOptionStepOrder, out ParameterSet? storedOptions)
                ? storedOptions
                : ParameterSet.Empty;

        return GrindingProgram.Create(programId, name, steps, createdAtUtc, programOptions) with
        {
            ModifiedAtUtc = modifiedAtUtc,
        };
    }

    public async Task SaveAsync(GrindingProgram program, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(program);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO program (program_id, name, created_at_utc, modified_at_utc)
                VALUES ($id, $name, $created, $modified)
                ON CONFLICT(program_id) DO UPDATE SET
                    name = excluded.name,
                    modified_at_utc = excluded.modified_at_utc;
                """;
            SqlMapping.AddParameter(command, "$id", program.ProgramId);
            SqlMapping.AddParameter(command, "$name", program.Name);
            SqlMapping.AddParameter(command, "$created", SqlMapping.ToText(program.CreatedAtUtc));
            SqlMapping.AddParameter(command, "$modified", SqlMapping.ToText(program.ModifiedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 先删后写：改少了工序，旧的那几道不能还留在库里。
        foreach (string table in new[] { "program_parameter", "program_step" })
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE program_id = $id;";
            SqlMapping.AddParameter(command, "$id", program.ProgramId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await SqlMapping.WriteParametersAsync(
            connection,
            transaction,
            program.ProgramId,
            SqlMapping.ProgramOptionStepOrder,
            program.ProgramOptions,
            cancellationToken,
            SqlMapping.ProgramParameters).ConfigureAwait(false);

        foreach (GrindingJobStep step in program.Steps)
        {
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO program_step (program_id, step_order, step_type_key) VALUES ($id, $order, $type);";
                SqlMapping.AddParameter(command, "$id", program.ProgramId);
                SqlMapping.AddParameter(command, "$order", step.Order);
                SqlMapping.AddParameter(command, "$type", step.StepTypeKey);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SqlMapping.WriteParametersAsync(
                connection,
                transaction,
                program.ProgramId,
                step.Order,
                step.Parameters,
                cancellationToken,
                SqlMapping.ProgramParameters).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> FindIdByNameAsync(string name, string? exceptId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT program_id FROM program
            WHERE trim(name) = trim($name) COLLATE NOCASE
              AND ($except IS NULL OR program_id <> $except)
            LIMIT 1;
            """;
        SqlMapping.AddParameter(command, "$name", name);
        SqlMapping.AddParameter(command, "$except", exceptId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task DeleteAsync(string programId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        foreach (string table in new[] { "program_parameter", "program_step", "program" })
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE program_id = $id;";
            SqlMapping.AddParameter(command, "$id", programId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
