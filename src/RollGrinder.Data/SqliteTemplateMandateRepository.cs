using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace RollGrinder.Data;

/// <summary>"必须采用"的 SQLite 实现。</summary>
public sealed class SqliteTemplateMandateRepository : ITemplateMandateRepository
{
    private const string MandateColumns =
        "m.mandate_id, m.kind, m.template_id, m.template_version, m.template_name, m.created_at_utc, m.created_by, m.revoked";

    private const string RollColumns = "r.mandate_id, r.roll_id, r.decision, r.applied_plan_id, r.applied_at_utc";

    private readonly SqliteDatabase database;

    public SqliteTemplateMandateRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(TemplateMandate mandate, IReadOnlyList<TemplateMandateRoll> rolls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mandate);
        ArgumentNullException.ThrowIfNull(rolls);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO template_mandate (mandate_id, kind, template_id, template_version, template_name, created_at_utc, created_by, revoked)
                VALUES ($id, $kind, $template, $version, $name, $created, $by, $revoked);
                """;
            SqlMapping.AddParameter(command, "$id", mandate.MandateId);
            SqlMapping.AddParameter(command, "$kind", (int)mandate.Kind);
            SqlMapping.AddParameter(command, "$template", mandate.TemplateId);
            SqlMapping.AddParameter(command, "$version", mandate.TemplateVersion);
            SqlMapping.AddParameter(command, "$name", mandate.TemplateName);
            SqlMapping.AddParameter(command, "$created", SqlMapping.ToText(mandate.CreatedAtUtc));
            SqlMapping.AddParameter(command, "$by", mandate.CreatedBy);
            SqlMapping.AddParameter(command, "$revoked", mandate.Revoked ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (TemplateMandateRoll roll in rolls)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO template_mandate_roll (mandate_id, roll_id, decision) VALUES ($id, $roll, $decision);";
            SqlMapping.AddParameter(command, "$id", mandate.MandateId);
            SqlMapping.AddParameter(command, "$roll", roll.RollId);
            SqlMapping.AddParameter(command, "$decision", (int)roll.Decision);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TemplateMandate>> ListByTemplateAsync(TemplateKind kind, string templateId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {MandateColumns} FROM template_mandate m WHERE m.kind = $kind AND m.template_id = $template ORDER BY m.created_at_utc DESC;";
        SqlMapping.AddParameter(command, "$kind", (int)kind);
        SqlMapping.AddParameter(command, "$template", templateId);

        var list = new List<TemplateMandate>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapMandate(reader, 0));
        }

        return list;
    }

    public async Task<IReadOnlyList<(TemplateMandate Mandate, TemplateMandateRoll Roll)>> ListActiveForRollAsync(
        string rollId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {MandateColumns}, {RollColumns}
            FROM template_mandate m JOIN template_mandate_roll r ON r.mandate_id = m.mandate_id
            WHERE r.roll_id = $roll AND m.revoked = 0
            ORDER BY m.created_at_utc DESC;
            """;
        SqlMapping.AddParameter(command, "$roll", rollId);

        var list = new List<(TemplateMandate, TemplateMandateRoll)>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add((MapMandate(reader, 0), MapRoll(reader, 8)));
        }

        return list;
    }

    public async Task<IReadOnlyList<TemplateMandateRoll>> ListRollsAsync(string mandateId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {RollColumns} FROM template_mandate_roll r WHERE r.mandate_id = $id ORDER BY r.roll_id;";
        SqlMapping.AddParameter(command, "$id", mandateId);

        var list = new List<TemplateMandateRoll>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapRoll(reader, 0));
        }

        return list;
    }

    public async Task MarkAppliedAsync(string mandateId, string rollId, string planId, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE template_mandate_roll SET applied_plan_id = $plan, applied_at_utc = $at WHERE mandate_id = $id AND roll_id = $roll;";
        SqlMapping.AddParameter(command, "$plan", planId);
        SqlMapping.AddParameter(command, "$at", SqlMapping.ToText(atUtc));
        SqlMapping.AddParameter(command, "$id", mandateId);
        SqlMapping.AddParameter(command, "$roll", rollId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(string mandateId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE template_mandate SET revoked = 1 WHERE mandate_id = $id;";
        SqlMapping.AddParameter(command, "$id", mandateId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TemplateMandate MapMandate(SqliteDataReader reader, int at) => new(
        reader.GetString(at),
        (TemplateKind)reader.GetInt32(at + 1),
        reader.GetString(at + 2),
        reader.GetInt32(at + 3),
        reader.GetString(at + 4),
        SqlMapping.ToTimestamp(reader.GetString(at + 5)),
        reader.GetString(at + 6),
        reader.GetInt32(at + 7) != 0);

    private static TemplateMandateRoll MapRoll(SqliteDataReader reader, int at) => new(
        reader.GetString(at),
        reader.GetString(at + 1),
        (MandateDecision)reader.GetInt32(at + 2),
        reader.IsDBNull(at + 3) ? null : reader.GetString(at + 3),
        reader.IsDBNull(at + 4) ? null : SqlMapping.ToTimestamp(reader.GetString(at + 4)));
}
