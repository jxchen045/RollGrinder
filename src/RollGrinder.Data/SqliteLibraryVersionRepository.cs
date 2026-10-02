using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace RollGrinder.Data;

/// <summary>库条目留档的一版（只读）。</summary>
/// <param name="Kind">"profile" 或 "program"。</param>
/// <param name="ItemId">条目标识。</param>
/// <param name="Version">版本号。</param>
/// <param name="Name">当时的名字。</param>
/// <param name="SavedAtUtc">这一版被新版本替下的时刻。</param>
/// <param name="SavedBy">谁保存的新版本。</param>
/// <param name="Payload">这一版的完整内容（库交换文件格式 JSON）。</param>
public sealed record LibraryVersionEntry(
    string Kind, string ItemId, int Version, string Name, DateTimeOffset SavedAtUtc, string SavedBy, string Payload);

/// <summary>库条目的版本留档（关系设计第 4 节：保存即生效，旧版本只读留档）。</summary>
public interface ILibraryVersionRepository
{
    Task AddAsync(LibraryVersionEntry entry, CancellationToken cancellationToken);

    /// <summary>某条目留档的各版，新的在前。</summary>
    Task<IReadOnlyList<LibraryVersionEntry>> ListAsync(string kind, string itemId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILibraryVersionRepository"/>
public sealed class SqliteLibraryVersionRepository : ILibraryVersionRepository
{
    public const string ProfileKind = "profile";
    public const string ProgramKind = "program";

    private readonly SqliteDatabase database;

    public SqliteLibraryVersionRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task AddAsync(LibraryVersionEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR REPLACE INTO library_version (kind, item_id, version, name, saved_at_utc, saved_by, payload) "
            + "VALUES ($kind, $id, $version, $name, $at, $by, $payload);";
        SqlMapping.AddParameter(command, "$kind", entry.Kind);
        SqlMapping.AddParameter(command, "$id", entry.ItemId);
        SqlMapping.AddParameter(command, "$version", entry.Version);
        SqlMapping.AddParameter(command, "$name", entry.Name);
        SqlMapping.AddParameter(command, "$at", SqlMapping.ToText(entry.SavedAtUtc));
        SqlMapping.AddParameter(command, "$by", entry.SavedBy);
        SqlMapping.AddParameter(command, "$payload", entry.Payload);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LibraryVersionEntry>> ListAsync(string kind, string itemId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT version, name, saved_at_utc, saved_by, payload FROM library_version "
            + "WHERE kind = $kind AND item_id = $id ORDER BY version DESC;";
        SqlMapping.AddParameter(command, "$kind", kind);
        SqlMapping.AddParameter(command, "$id", itemId);

        var entries = new List<LibraryVersionEntry>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new LibraryVersionEntry(
                kind, itemId, reader.GetInt32(0), reader.GetString(1), SqlMapping.ToTimestamp(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4)));
        }

        return entries;
    }
}
