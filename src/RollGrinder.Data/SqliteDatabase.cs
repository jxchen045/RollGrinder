using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace RollGrinder.Data;

/// <summary>
/// 数据库连接与结构版本管理。数据库文件放在 data/ 下，升级时保留。
/// 结构变更一律追加一条迁移，PRAGMA user_version 记录已应用到第几条。
/// </summary>
public sealed class SqliteDatabase
{
    /// <summary>数据库文件名。</summary>
    public const string FileName = "rollgrinder.db";

    private static readonly string[] Migrations =
    {
        // 1：初始结构。参数按键值存，新增辊形/工序类型不改表。
        """
        CREATE TABLE roll (
            roll_id            TEXT PRIMARY KEY,
            code               TEXT NOT NULL,
            body_length_mm     REAL NOT NULL,
            nominal_radius_mm  REAL NOT NULL,
            material           TEXT NULL,
            created_at_utc     TEXT NOT NULL
        );

        CREATE TABLE job (
            job_id             TEXT PRIMARY KEY,
            roll_id            TEXT NOT NULL REFERENCES roll(roll_id),
            profile_type_key   TEXT NOT NULL,
            body_length_mm     REAL NOT NULL,
            nominal_radius_mm  REAL NOT NULL,
            state              INTEGER NOT NULL,
            created_at_utc     TEXT NOT NULL
        );

        CREATE TABLE job_step (
            job_id             TEXT NOT NULL REFERENCES job(job_id) ON DELETE CASCADE,
            step_order         INTEGER NOT NULL,
            step_type_key      TEXT NOT NULL,
            PRIMARY KEY (job_id, step_order)
        );

        CREATE TABLE job_parameter (
            job_id             TEXT NOT NULL REFERENCES job(job_id) ON DELETE CASCADE,
            step_order         INTEGER NOT NULL,
            parameter_key      TEXT NOT NULL,
            value_kind         INTEGER NOT NULL,
            value_text         TEXT NOT NULL,
            PRIMARY KEY (job_id, step_order, parameter_key)
        );

        CREATE TABLE grinding_record (
            record_id          TEXT PRIMARY KEY,
            job_id             TEXT NOT NULL REFERENCES job(job_id) ON DELETE CASCADE,
            started_at_utc     TEXT NOT NULL,
            finished_at_utc    TEXT NULL,
            state              INTEGER NOT NULL,
            note               TEXT NULL
        );

        CREATE TABLE measurement (
            measurement_id     TEXT PRIMARY KEY,
            job_id             TEXT NOT NULL REFERENCES job(job_id) ON DELETE CASCADE,
            recorded_at_utc    TEXT NOT NULL,
            source             TEXT NOT NULL
        );

        CREATE TABLE measurement_point (
            measurement_id     TEXT NOT NULL REFERENCES measurement(measurement_id) ON DELETE CASCADE,
            body_position_mm   REAL NOT NULL,
            measured_radius_mm REAL NOT NULL,
            PRIMARY KEY (measurement_id, body_position_mm)
        );

        CREATE TABLE compensation (
            compensation_id    TEXT PRIMARY KEY,
            job_id             TEXT NOT NULL REFERENCES job(job_id) ON DELETE CASCADE,
            created_at_utc     TEXT NOT NULL,
            measurement_id     TEXT NULL REFERENCES measurement(measurement_id)
        );

        CREATE TABLE compensation_point (
            compensation_id    TEXT NOT NULL REFERENCES compensation(compensation_id) ON DELETE CASCADE,
            body_position_mm   REAL NOT NULL,
            offset_radius_mm   REAL NOT NULL,
            PRIMARY KEY (compensation_id, body_position_mm)
        );

        CREATE TABLE alarm (
            alarm_id           INTEGER PRIMARY KEY AUTOINCREMENT,
            raised_at_utc      TEXT NOT NULL,
            severity           INTEGER NOT NULL,
            message_key        TEXT NOT NULL,
            detail             TEXT NULL
        );

        CREATE INDEX ix_job_roll ON job(roll_id);
        CREATE INDEX ix_record_job ON grinding_record(job_id);
        CREATE INDEX ix_record_started ON grinding_record(started_at_utc);
        CREATE INDEX ix_measurement_job ON measurement(job_id);
        CREATE INDEX ix_alarm_raised ON alarm(raised_at_utc);
        """,

        // 2：报警号。上位机自己的报警占 800000–800999 号段，现场按号查。
        """
        ALTER TABLE alarm ADD COLUMN code INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX ix_alarm_code ON alarm(code);
        """,
    };

    private readonly string connectionString;

    public SqliteDatabase(string databaseFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFilePath);
        DatabaseFilePath = databaseFilePath;

        string? directory = Path.GetDirectoryName(databaseFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        this.connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>数据库文件的完整路径。</summary>
    public string DatabaseFilePath { get; }

    /// <summary>当前代码期望的结构版本。</summary>
    public static int ExpectedSchemaVersion => Migrations.Length;

    /// <summary>打开一个连接（调用方负责释放）。</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(this.connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>把结构迁移到最新版本；已是最新则什么都不做。升级不丢现场数据。</summary>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        int currentVersion = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (currentVersion > Migrations.Length)
        {
            throw new DataStoreException(
                $"Database '{DatabaseFilePath}' has schema version {currentVersion}, newer than this build supports ({Migrations.Length}).");
        }

        for (int version = currentVersion; version < Migrations.Length; version++)
        {
            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, Migrations[version], cancellationToken, transaction).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {version + 1};"),
                cancellationToken,
                transaction).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Migrations.Length;
    }

    /// <summary>读取数据库当前的结构版本。</summary>
    public async Task<int> ReadSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
