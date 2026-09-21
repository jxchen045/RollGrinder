using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Data;

/// <summary>账号仓储的 SQLite 实现。用户名列声明为 COLLATE NOCASE，大小写不敏感。</summary>
public sealed class SqliteUserRepository : IUserRepository
{
    private readonly SqliteDatabase database;

    public SqliteUserRepository(SqliteDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT user_name, role, password_hash IS NULL, created_at_utc
            FROM app_user ORDER BY user_name;
            """;

        var accounts = new List<UserAccount>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            accounts.Add(new UserAccount(
                reader.GetString(0),
                (UserRole)reader.GetInt32(1),
                reader.GetInt64(2) != 0,
                SqlMapping.ToTimestamp(reader.GetString(3))));
        }

        return accounts;
    }

    public async Task<StoredUser?> FindAsync(string userName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT user_name, role, password_hash, password_salt, iterations, created_at_utc
            FROM app_user WHERE user_name = $name;
            """;
        SqlMapping.AddParameter(command, "$name", userName);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        byte[]? hash = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2);
        byte[]? salt = reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3);

        return new StoredUser(
            new UserAccount(
                reader.GetString(0),
                (UserRole)reader.GetInt32(1),
                hash is null,
                SqlMapping.ToTimestamp(reader.GetString(5))),
            hash,
            salt,
            reader.GetInt32(4));
    }

    public async Task UpsertAsync(StoredUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO app_user (user_name, role, password_hash, password_salt, iterations, created_at_utc)
            VALUES ($name, $role, $hash, $salt, $iterations, $created)
            ON CONFLICT(user_name) DO UPDATE SET
                role = excluded.role,
                password_hash = excluded.password_hash,
                password_salt = excluded.password_salt,
                iterations = excluded.iterations;
            """;
        SqlMapping.AddParameter(command, "$name", user.Account.UserName);
        SqlMapping.AddParameter(command, "$role", (int)user.Account.Role);
        SqlMapping.AddParameter(command, "$hash", user.PasswordHash);
        SqlMapping.AddParameter(command, "$salt", user.PasswordSalt);
        SqlMapping.AddParameter(command, "$iterations", user.Iterations);
        SqlMapping.AddParameter(command, "$created", SqlMapping.ToText(user.Account.CreatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string userName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_user WHERE user_name = $name;";
        SqlMapping.AddParameter(command, "$name", userName);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await this.database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM app_user;";

        object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }
}
