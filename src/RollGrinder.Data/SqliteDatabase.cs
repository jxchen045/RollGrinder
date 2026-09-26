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

        // 3：辊形改成可叠加的多段曲线，并建辊形库。
        //
        // 辊形是可复用的模板（roll_profile），作业引用它的时候复制一份快照
        // （job_profile_segment）——库里之后改了，已经磨过的那支辊的记录不能跟着变。
        //
        // job.profile_type_key 保留不动：旧库里的作业还只有单曲线，靠它读回来；
        // 新作业往这一列写第一段的类型键，列表显示仍然照旧。
        """
        ALTER TABLE job ADD COLUMN profile_id TEXT NULL;
        ALTER TABLE job ADD COLUMN profile_name TEXT NULL;

        CREATE TABLE roll_profile (
            profile_id         TEXT PRIMARY KEY,
            name               TEXT NOT NULL,
            body_length_mm     REAL NOT NULL,
            created_at_utc     TEXT NOT NULL,
            modified_at_utc    TEXT NOT NULL
        );

        CREATE TABLE roll_profile_segment (
            profile_id         TEXT NOT NULL REFERENCES roll_profile(profile_id) ON DELETE CASCADE,
            segment_order      INTEGER NOT NULL,
            profile_type_key   TEXT NOT NULL,
            from_mm            REAL NOT NULL,
            to_mm              REAL NOT NULL,
            is_mirrored        INTEGER NOT NULL,
            PRIMARY KEY (profile_id, segment_order)
        );

        CREATE TABLE roll_profile_segment_parameter (
            profile_id         TEXT NOT NULL,
            segment_order      INTEGER NOT NULL,
            parameter_key      TEXT NOT NULL,
            value_kind         INTEGER NOT NULL,
            value_text         TEXT NOT NULL,
            PRIMARY KEY (profile_id, segment_order, parameter_key),
            FOREIGN KEY (profile_id, segment_order)
                REFERENCES roll_profile_segment(profile_id, segment_order) ON DELETE CASCADE
        );

        CREATE TABLE job_profile_segment (
            job_id             TEXT NOT NULL REFERENCES job(job_id) ON DELETE CASCADE,
            segment_order      INTEGER NOT NULL,
            profile_type_key   TEXT NOT NULL,
            from_mm            REAL NOT NULL,
            to_mm              REAL NOT NULL,
            is_mirrored        INTEGER NOT NULL,
            PRIMARY KEY (job_id, segment_order)
        );

        CREATE TABLE job_profile_segment_parameter (
            job_id             TEXT NOT NULL,
            segment_order      INTEGER NOT NULL,
            parameter_key      TEXT NOT NULL,
            value_kind         INTEGER NOT NULL,
            value_text         TEXT NOT NULL,
            PRIMARY KEY (job_id, segment_order, parameter_key),
            FOREIGN KEY (job_id, segment_order)
                REFERENCES job_profile_segment(job_id, segment_order) ON DELETE CASCADE
        );

        CREATE INDEX ix_roll_profile_modified ON roll_profile(modified_at_utc);
        """,

        // 4：程序库。程序是可复用的模板（一串工序 + 整支程序的取舍开关），
        // 作业引用它的时候把工序复制成快照存进 job_step / job_parameter——
        // 库里之后改了程序，已经磨过的那支辊的记录不会跟着变。
        //
        // program_parameter 的 step_order 与 job_parameter 同一套约定：
        // 工序从 1 起，-1 是整支程序的取舍开关。
        """
        ALTER TABLE job ADD COLUMN program_id TEXT NULL;
        ALTER TABLE job ADD COLUMN program_name TEXT NULL;

        CREATE TABLE program (
            program_id         TEXT PRIMARY KEY,
            name               TEXT NOT NULL,
            created_at_utc     TEXT NOT NULL,
            modified_at_utc    TEXT NOT NULL
        );

        CREATE TABLE program_step (
            program_id         TEXT NOT NULL REFERENCES program(program_id) ON DELETE CASCADE,
            step_order         INTEGER NOT NULL,
            step_type_key      TEXT NOT NULL,
            PRIMARY KEY (program_id, step_order)
        );

        CREATE TABLE program_parameter (
            program_id         TEXT NOT NULL REFERENCES program(program_id) ON DELETE CASCADE,
            step_order         INTEGER NOT NULL,
            parameter_key      TEXT NOT NULL,
            value_kind         INTEGER NOT NULL,
            value_text         TEXT NOT NULL,
            PRIMARY KEY (program_id, step_order, parameter_key)
        );

        CREATE INDEX ix_program_modified ON program(modified_at_utc);
        """,

        // 5：账号。口令只存 PBKDF2 的导出密钥与盐，库里没有明文。
        // 迭代次数跟着每个口令走，将来调高了老口令照样验得动。
        // password_hash 为 NULL 表示这个账号还没设过口令——首次启动种下的
        // 管理账号就是这个状态，登录时先让人设一个再放行。
        """
        CREATE TABLE app_user (
            user_name          TEXT PRIMARY KEY COLLATE NOCASE,
            role               INTEGER NOT NULL,
            password_hash      BLOB NULL,
            password_salt      BLOB NULL,
            iterations         INTEGER NOT NULL,
            created_at_utc     TEXT NOT NULL
        );
        """,

        // 6：现场标定值。与 machine.json 描述的机床固有能力分开——
        // 这些换一次砂轮就变，现场要在界面上改，还要记谁在什么时候改的。
        """
        CREATE TABLE machine_calibration (
            parameter_key      TEXT PRIMARY KEY,
            value_kind         INTEGER NOT NULL,
            value_text         TEXT NOT NULL,
            changed_at_utc     TEXT NOT NULL,
            changed_by         TEXT NOT NULL
        );
        """,

        // 7：记录页那 12 项指标的数据底座。
        //
        // 三件事：
        // - 测量分磨前 / 磨中 / 磨后。先前只有一个 source，分不出"这一次是磨前
        //   量的还是磨后量的"，磨前直径与锥度因此根本算不出来；
        // - 圆度与偏心单独存一张表。它们是沿辊身一个位置一个数，与辊形测量的
        //   半径不是一回事，塞进同一张表会让"这一行到底是什么"变得要靠 source 猜；
        // - 记录上留一格砂轮直径。砂轮天天在磨小，事后回头查这支辊是用多大的
        //   砂轮磨的，只能靠当时记下来。
        """
        ALTER TABLE measurement ADD COLUMN stage INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE grinding_record ADD COLUMN wheel_diameter_mm REAL NULL;

        CREATE TABLE roundness_measurement (
            roundness_id       TEXT PRIMARY KEY,
            job_id             TEXT NOT NULL REFERENCES job(job_id) ON DELETE CASCADE,
            recorded_at_utc    TEXT NOT NULL,
            source             TEXT NOT NULL
        );

        CREATE TABLE roundness_point (
            roundness_id           TEXT NOT NULL
                                   REFERENCES roundness_measurement(roundness_id) ON DELETE CASCADE,
            body_position_mm       REAL NOT NULL,
            roundness_micrometer   REAL NOT NULL,
            eccentricity_micrometer REAL NOT NULL,
            PRIMARY KEY (roundness_id, body_position_mm)
        );

        CREATE INDEX ix_roundness_job ON roundness_measurement(job_id);
        """,

        // 8：轧辊数据。实机"轧辊数据"屏上的那几项里，先前只存了长度与直径。
        //
        // 补上的都是**这支辊本身的属性**，不是工艺参数：起磨点在哪、曲线作用
        // 在哪一段、允许差多少、三个重量（吊装与中心架托瓦压力要按重量定）。
        // 全部可空——现场不一定每支辊都登记得齐，逼着填只会让人乱填一个数。
        """
        ALTER TABLE roll ADD COLUMN grind_start_position_mm REAL NULL;
        ALTER TABLE roll ADD COLUMN curve_length_mm         REAL NULL;
        ALTER TABLE roll ADD COLUMN curve_tolerance_um      REAL NULL;
        ALTER TABLE roll ADD COLUMN net_weight_kg           REAL NULL;
        ALTER TABLE roll ADD COLUMN head_box_weight_kg      REAL NULL;
        ALTER TABLE roll ADD COLUMN tail_box_weight_kg      REAL NULL;
        """,

        // 9：辊形的拼法（阶段 1）。0 = 叠加（以前存的都是这种，区间可以重叠、重叠处相加），
        // 1 = 顺接（起点 Z + 段长、首尾相接）。旧行默认 0，读回来照旧按叠加求值，
        // 已经磨过的辊的记录、下发过的作业快照一个点都不变。
        // 同一条辊形的各段写同一个值；放在段表上，是因为辊形库和作业快照共用同一套段的读写。
        """
        ALTER TABLE roll_profile_segment ADD COLUMN layout INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE job_profile_segment  ADD COLUMN layout INTEGER NOT NULL DEFAULT 0;
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
