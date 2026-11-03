using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace RollGrinder.Data;

/// <summary>磨削方案的 SQLite 实现。</summary>
public sealed class SqliteGrindingPlanRepository : IGrindingPlanRepository
{
    private const string Columns =
        "plan_id, roll_id, plan_no, name, purpose, profile_id, program_id, source_kind, source_ref, end_zone_mm, created_at_utc, created_by";

    private readonly SqliteDatabase database;

    public SqliteGrindingPlanRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<GrindingPlanRecord>> ListByRollAsync(string rollId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM grinding_plan WHERE roll_id = $roll ORDER BY plan_no;";
        SqlMapping.AddParameter(command, "$roll", rollId);

        var plans = new List<GrindingPlanRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            plans.Add(Map(reader));
        }

        return plans;
    }

    public async Task<GrindingPlanRecord?> GetAsync(string planId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM grinding_plan WHERE plan_id = $id;";
        SqlMapping.AddParameter(command, "$id", planId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<int> NextPlanNoAsync(string rollId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // 作业里记过的号也算：方案删了，记录里的"方案 n"不会指到另一个新方案上。
        command.CommandText =
            """
            SELECT MAX(n) FROM (
                SELECT MAX(plan_no) AS n FROM grinding_plan WHERE roll_id = $roll
                UNION ALL
                SELECT MAX(plan_no) AS n FROM job WHERE roll_id = $roll);
            """;
        SqlMapping.AddParameter(command, "$roll", rollId);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 1 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) + 1;
    }

    public async Task SaveAsync(GrindingPlanRecord plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO grinding_plan ({Columns})
            VALUES ($id, $roll, $no, $name, $purpose, $profile, $program, $sourceKind, $sourceRef, $endZone, $created, $createdBy)
            ON CONFLICT(plan_id) DO UPDATE SET
                name = excluded.name,
                purpose = excluded.purpose,
                profile_id = excluded.profile_id,
                program_id = excluded.program_id,
                source_kind = excluded.source_kind,
                source_ref = excluded.source_ref,
                end_zone_mm = excluded.end_zone_mm;
            """;
        SqlMapping.AddParameter(command, "$id", plan.PlanId);
        SqlMapping.AddParameter(command, "$roll", plan.RollId);
        SqlMapping.AddParameter(command, "$no", plan.PlanNo);
        SqlMapping.AddParameter(command, "$name", plan.Name);
        SqlMapping.AddParameter(command, "$purpose", plan.Purpose);
        SqlMapping.AddParameter(command, "$profile", plan.ProfileId);
        SqlMapping.AddParameter(command, "$program", plan.ProgramId);
        SqlMapping.AddParameter(command, "$sourceKind", (int)plan.SourceKind);
        SqlMapping.AddParameter(command, "$sourceRef", plan.SourceRef);
        SqlMapping.AddParameter(command, "$endZone", plan.EndZoneMm);
        SqlMapping.AddParameter(command, "$created", SqlMapping.ToText(plan.CreatedAtUtc));
        SqlMapping.AddParameter(command, "$createdBy", plan.CreatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string planId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM grinding_plan WHERE plan_id = $id;";
        SqlMapping.AddParameter(command, "$id", planId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, PlanUsage>> UsageAsync(string rollId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT plan_id, COUNT(*), MAX(created_at_utc) FROM job
            WHERE roll_id = $roll AND plan_id IS NOT NULL
            GROUP BY plan_id;
            """;
        SqlMapping.AddParameter(command, "$roll", rollId);

        var usage = new Dictionary<string, PlanUsage>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            usage[reader.GetString(0)] = new PlanUsage(
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : SqlMapping.ToTimestamp(reader.GetString(2)));
        }

        return usage;
    }

    private static GrindingPlanRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        (PlanSourceKind)reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetDouble(9),
        SqlMapping.ToTimestamp(reader.GetString(10)),
        reader.GetString(11));
}
