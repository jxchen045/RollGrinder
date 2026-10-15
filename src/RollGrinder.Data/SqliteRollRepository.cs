using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core;
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
            INSERT INTO roll (
                roll_id, code, body_length_mm, nominal_radius_mm, material, created_at_utc,
                net_weight_kg, head_box_weight_kg, tail_box_weight_kg,
                roll_kind, current_diameter_mm, scrap_diameter_mm,
                target_profile_id, program_id, purpose, retired, plan_inferred)
            VALUES (
                $id, $code, $length, $radius, $material, $created,
                $netWeight, $headBoxWeight, $tailBoxWeight,
                $kind, $currentDiameter, $scrapDiameter,
                $profileId, $programId, $purpose, $retired, $inferred)
            ON CONFLICT(roll_id) DO UPDATE SET
                code = excluded.code,
                body_length_mm = excluded.body_length_mm,
                nominal_radius_mm = excluded.nominal_radius_mm,
                material = excluded.material,
                net_weight_kg = excluded.net_weight_kg,
                head_box_weight_kg = excluded.head_box_weight_kg,
                tail_box_weight_kg = excluded.tail_box_weight_kg,
                roll_kind = excluded.roll_kind,
                current_diameter_mm = excluded.current_diameter_mm,
                scrap_diameter_mm = excluded.scrap_diameter_mm,
                target_profile_id = excluded.target_profile_id,
                program_id = excluded.program_id,
                purpose = excluded.purpose,
                retired = excluded.retired,
                plan_inferred = excluded.plan_inferred;
            """;
        SqlMapping.AddParameter(command, "$netWeight", roll.Data.NetWeightKg);
        SqlMapping.AddParameter(command, "$headBoxWeight", roll.Data.HeadBoxWeightKg);
        SqlMapping.AddParameter(command, "$tailBoxWeight", roll.Data.TailBoxWeightKg);
        SqlMapping.AddParameter(command, "$kind", (int)roll.Kind);
        SqlMapping.AddParameter(command, "$currentDiameter", roll.CurrentDiameterMm);
        SqlMapping.AddParameter(command, "$scrapDiameter", roll.ScrapDiameterMm);
        SqlMapping.AddParameter(command, "$profileId", roll.TargetProfileId);
        SqlMapping.AddParameter(command, "$programId", roll.ProgramId);
        SqlMapping.AddParameter(command, "$purpose", roll.Purpose);
        SqlMapping.AddParameter(command, "$retired", roll.Retired ? 1 : 0);
        SqlMapping.AddParameter(command, "$inferred", roll.PlanInferred ? 1 : 0);
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
            SELECT roll_id, code, body_length_mm, nominal_radius_mm, material, created_at_utc,
                   grind_start_position_mm, curve_length_mm, curve_tolerance_um,
                   net_weight_kg, head_box_weight_kg, tail_box_weight_kg,
                   roll_kind, current_diameter_mm, scrap_diameter_mm,
                   target_profile_id, program_id, purpose, retired, plan_inferred
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
            SELECT roll_id, code, body_length_mm, nominal_radius_mm, material, created_at_utc,
                   grind_start_position_mm, curve_length_mm, curve_tolerance_um,
                   net_weight_kg, head_box_weight_kg, tail_box_weight_kg,
                   roll_kind, current_diameter_mm, scrap_diameter_mm,
                   target_profile_id, program_id, purpose, retired, plan_inferred
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

    public async Task<IReadOnlyList<RollRecord>> ListUsingAsync(string? profileId, string? programId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT roll_id, code, body_length_mm, nominal_radius_mm, material, created_at_utc,
                   grind_start_position_mm, curve_length_mm, curve_tolerance_um,
                   net_weight_kg, head_box_weight_kg, tail_box_weight_kg,
                   roll_kind, current_diameter_mm, scrap_diameter_mm,
                   target_profile_id, program_id, purpose, retired, plan_inferred
            FROM roll
            WHERE retired = 0 AND (($profile IS NOT NULL AND target_profile_id = $profile)
                                OR ($program IS NOT NULL AND program_id = $program))
            ORDER BY roll_id;
            """;
        SqlMapping.AddParameter(command, "$profile", profileId);
        SqlMapping.AddParameter(command, "$program", programId);

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
        SqlMapping.ToTimestamp(reader.GetString(5)))
    {
        // 列 6–8（启磨点、曲线长度、曲线允许误差）停用：启磨点归作业（每次装夹示教），
        // 曲线长度就是辊身长度、公差只认辊形里的那一个（参数归属，一个数一个出处）。列留着不读不写。
        Data = new RollDataSheet(
            Nullable(reader, 9),
            Nullable(reader, 10),
            Nullable(reader, 11)),
        Kind = (RollKind)reader.GetInt32(12),
        CurrentDiameterMm = Nullable(reader, 13),
        ScrapDiameterMm = Nullable(reader, 14),
        TargetProfileId = reader.IsDBNull(15) ? null : reader.GetString(15),
        ProgramId = reader.IsDBNull(16) ? null : reader.GetString(16),
        Purpose = reader.IsDBNull(17) ? null : reader.GetString(17),
        Retired = reader.GetInt32(18) != 0,
        PlanInferred = reader.GetInt32(19) != 0,
    };

    /// <summary>没登记的那几项读回来仍然是"没登记"，不是 0。</summary>
    private static double? Nullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
