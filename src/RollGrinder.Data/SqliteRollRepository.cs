using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Geometry;
using RollGrinder.Data.Model;

namespace RollGrinder.Data;

/// <summary>辊件档案的 SQLite 实现。</summary>
public sealed class SqliteRollRepository : IRollRepository
{
    private readonly SqliteDatabase database;

    public SqliteRollRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task UpsertAsync(RollRecord roll, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roll);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO roll (roll_id, code, body_length_mm, nominal_radius_mm, material, created_at_utc)
            VALUES ($id, $code, $length, $radius, $material, $created)
            ON CONFLICT(roll_id) DO UPDATE SET
                code = excluded.code,
                body_length_mm = excluded.body_length_mm,
                nominal_radius_mm = excluded.nominal_radius_mm,
                material = excluded.material;
            """;
        SqlMapping.AddParameter(command, "$id", roll.RollId);
        SqlMapping.AddParameter(command, "$code", roll.Code);
        SqlMapping.AddParameter(command, "$length", roll.Geometry.BodyLengthMm);
        SqlMapping.AddParameter(command, "$radius", roll.Geometry.NominalRadiusMm);
        SqlMapping.AddParameter(command, "$material", roll.Material);
        SqlMapping.AddParameter(command, "$created", SqlMapping.ToText(roll.CreatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RollRecord?> GetAsync(string rollId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT roll_id, code, body_length_mm, nominal_radius_mm, material, created_at_utc
            FROM roll WHERE roll_id = $id;
            """;
        SqlMapping.AddParameter(command, "$id", rollId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<RollRecord>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT roll_id, code, body_length_mm, nominal_radius_mm, material, created_at_utc
            FROM roll ORDER BY created_at_utc DESC LIMIT $limit;
            """;
        SqlMapping.AddParameter(command, "$limit", limit);

        var rolls = new List<RollRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rolls.Add(Map(reader));
        }

        return rolls;
    }

    private static RollRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        RollGeometry.Create(reader.GetDouble(2), reader.GetDouble(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        SqlMapping.ToTimestamp(reader.GetString(5)));
}
