using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Profiles;

namespace RollGrinder.Data;

/// <summary>辊形库的 SQLite 实现。保存是整条辊形的替换式写入，段与参数一起换。</summary>
public sealed class SqliteRollProfileRepository : IRollProfileRepository
{
    private readonly SqliteDatabase database;

    public SqliteRollProfileRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<RollProfileSummary>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // 段数与主辊形类型从段表里带出来，省得为了列个表把每条辊形都读全。
        command.CommandText =
            """
            SELECT p.profile_id, p.name, p.body_length_mm, p.modified_at_utc,
                   (SELECT COUNT(*) FROM roll_profile_segment s WHERE s.profile_id = p.profile_id),
                   (SELECT s.profile_type_key FROM roll_profile_segment s
                    WHERE s.profile_id = p.profile_id ORDER BY s.segment_order LIMIT 1)
            FROM roll_profile p
            ORDER BY p.modified_at_utc DESC
            LIMIT $limit;
            """;
        SqlMapping.AddParameter(command, "$limit", limit);

        var summaries = new List<RollProfileSummary>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            summaries.Add(new RollProfileSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.GetInt32(4),
                reader.GetDouble(2),
                SqlMapping.ToTimestamp(reader.GetString(3))));
        }

        return summaries;
    }

    public async Task<RollProfileDefinition?> GetAsync(string profileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);

        string name;
        double bodyLengthMm;
        DateTimeOffset createdAtUtc;
        DateTimeOffset modifiedAtUtc;

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name, body_length_mm, created_at_utc, modified_at_utc FROM roll_profile WHERE profile_id = $id;";
            SqlMapping.AddParameter(command, "$id", profileId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            name = reader.GetString(0);
            bodyLengthMm = reader.GetDouble(1);
            createdAtUtc = SqlMapping.ToTimestamp(reader.GetString(2));
            modifiedAtUtc = SqlMapping.ToTimestamp(reader.GetString(3));
        }

        CompositeRollProfile? profile = await ProfileSegmentMapping
            .ReadAsync(connection, ProfileSegmentTables.Library, profileId, cancellationToken).ConfigureAwait(false);

        if (profile is null)
        {
            // 有条目没有段：库坏了，不猜一条曲线出来。
            throw new DataStoreException($"Roll profile '{profileId}' has no segments.");
        }

        return new RollProfileDefinition(profileId, name, bodyLengthMm, profile, createdAtUtc, modifiedAtUtc);
    }

    public async Task SaveAsync(RollProfileDefinition profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO roll_profile (profile_id, name, body_length_mm, created_at_utc, modified_at_utc)
                VALUES ($id, $name, $length, $created, $modified)
                ON CONFLICT(profile_id) DO UPDATE SET
                    name = excluded.name,
                    body_length_mm = excluded.body_length_mm,
                    modified_at_utc = excluded.modified_at_utc;
                """;
            SqlMapping.AddParameter(command, "$id", profile.ProfileId);
            SqlMapping.AddParameter(command, "$name", profile.Name);
            SqlMapping.AddParameter(command, "$length", profile.BodyLengthMm);
            SqlMapping.AddParameter(command, "$created", SqlMapping.ToText(profile.CreatedAtUtc));
            SqlMapping.AddParameter(command, "$modified", SqlMapping.ToText(profile.ModifiedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ProfileSegmentMapping.WriteAsync(
            connection, transaction, ProfileSegmentTables.Library, profile.ProfileId, profile.Profile, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> FindIdByNameAsync(string name, string? exceptId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT profile_id FROM roll_profile
            WHERE trim(name) = trim($name) COLLATE NOCASE
              AND ($except IS NULL OR profile_id <> $except)
            LIMIT 1;
            """;
        SqlMapping.AddParameter(command, "$name", name);
        SqlMapping.AddParameter(command, "$except", exceptId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task DeleteAsync(string profileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await ProfileSegmentMapping.DeleteAsync(
            connection, transaction, ProfileSegmentTables.Library, profileId, cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM roll_profile WHERE profile_id = $id;";
            SqlMapping.AddParameter(command, "$id", profileId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
