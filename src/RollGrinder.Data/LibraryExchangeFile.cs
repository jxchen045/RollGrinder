using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;

namespace RollGrinder.Data;

/// <summary>一个交换文件里的内容。</summary>
/// <param name="Profiles">辊形。</param>
/// <param name="Programs">工艺程序。</param>
public sealed record LibraryExchangeContent(
    IReadOnlyList<RollProfileDefinition> Profiles,
    IReadOnlyList<GrindingProgram> Programs);

/// <summary>
/// 库区"导出到 U 盘 / 从 U 盘导入"（界面最终稿 5.9）用的交换文件：就是一个独立的 SQLite 库文件，
/// 结构与现场库相同，辊形与程序经现有的仓储读写——不另造一套格式，也就不会出现"导出能读、导入读错"。
///
/// 写完把日志并回主文件、退出 WAL，保证 U 盘上只有这一个文件，拔下来就能拿走。
/// </summary>
public static class LibraryExchangeFile
{
    /// <summary>交换文件的扩展名。</summary>
    public const string Extension = ".rgxlib";

    /// <summary>把选中的辊形与程序写进 <paramref name="filePath"/>（已有同名文件先删掉）。</summary>
    public static async Task ExportAsync(
        string filePath,
        IEnumerable<RollProfileDefinition> profiles,
        IEnumerable<GrindingProgram> programs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(programs);

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var database = new SqliteDatabase(filePath);
            await database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            var profileRepository = new SqliteRollProfileRepository(database);
            foreach (RollProfileDefinition profile in profiles)
            {
                await profileRepository.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
            }

            var programRepository = new SqliteProgramRepository(database);
            foreach (GrindingProgram program in programs)
            {
                await programRepository.SaveAsync(program, cancellationToken).ConfigureAwait(false);
            }

            await SealAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw new DataStoreException($"Could not write the library exchange file '{filePath}': {ex.Message}", ex);
        }
    }

    /// <summary>读出交换文件里的全部辊形与程序。</summary>
    public static async Task<LibraryExchangeContent> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new DataStoreException($"The library exchange file '{filePath}' does not exist.");
        }

        try
        {
            var database = new SqliteDatabase(filePath);
            await database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            var profileRepository = new SqliteRollProfileRepository(database);
            var profiles = new List<RollProfileDefinition>();
            foreach (RollProfileSummary summary in await profileRepository.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
            {
                if (await profileRepository.GetAsync(summary.ProfileId, cancellationToken).ConfigureAwait(false) is { } profile)
                {
                    profiles.Add(profile);
                }
            }

            var programRepository = new SqliteProgramRepository(database);
            var programs = new List<GrindingProgram>();
            foreach (ProgramSummary summary in await programRepository.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
            {
                if (await programRepository.GetAsync(summary.ProgramId, cancellationToken).ConfigureAwait(false) is { } program)
                {
                    programs.Add(program);
                }
            }

            await SealAsync(database, cancellationToken).ConfigureAwait(false);
            return new LibraryExchangeContent(profiles, programs);
        }
        catch (SqliteException ex)
        {
            throw new DataStoreException($"'{filePath}' is not a readable library exchange file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 从交换文件导入时，库里已经有同名的那一条要换个名字：在名字后面加"(2)""(3)"……，
    /// 不覆盖现场已有的（现场那一条可能正被作业用着）。纯函数，单测覆盖。
    /// </summary>
    public static string UniqueName(string name, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(taken);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{name} ({suffix})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>并回日志、退出 WAL、释放连接池：U 盘上只留这一个文件，可以直接拔。</summary>
    private static async Task SealAsync(SqliteDatabase database, CancellationToken cancellationToken)
    {
        await using (SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            SqliteConnection.ClearPool(connection);
        }
    }
}
