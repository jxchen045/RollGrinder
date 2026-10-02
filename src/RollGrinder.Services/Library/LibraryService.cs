using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Library;

/// <summary>
/// 辊形库、程序库的维护规则（关系设计第 4 节，T1、T2）：
/// 保存即生效、版本号 +1、旧版本只读留档；能查出被哪些辊的计划引用（在用清单）；
/// 被引用的不能删，只能停用；改动记录记谁、何时、版本几到几。不设审批流。
/// </summary>
public interface ILibraryService
{
    /// <summary>在用清单：计划里用这条辊形的辊。</summary>
    Task<IReadOnlyList<RollRecord>> ProfileUsersAsync(string profileId, CancellationToken cancellationToken);

    /// <summary>在用清单：计划里用这支程序的辊。</summary>
    Task<IReadOnlyList<RollRecord>> ProgramUsersAsync(string programId, CancellationToken cancellationToken);

    /// <summary>保存辊形：已有的先把旧版留档、版本 +1；新的从 1 起。返回存进去的那一份（带新版本号）。</summary>
    Task<RollProfileDefinition> SaveProfileAsync(RollProfileDefinition profile, string changedBy, CancellationToken cancellationToken);

    /// <summary>保存程序：同上。</summary>
    Task<GrindingProgram> SaveProgramAsync(GrindingProgram program, string changedBy, CancellationToken cancellationToken);

    /// <summary>停用 / 启用辊形。</summary>
    Task SetProfileDisabledAsync(string profileId, bool disabled, string changedBy, CancellationToken cancellationToken);

    /// <summary>停用 / 启用程序。</summary>
    Task SetProgramDisabledAsync(string programId, bool disabled, string changedBy, CancellationToken cancellationToken);

    /// <summary>删辊形：被计划引用的不删，返回 false（只能停用）。</summary>
    Task<bool> TryDeleteProfileAsync(string profileId, string changedBy, CancellationToken cancellationToken);

    /// <summary>删程序：被计划引用的不删，返回 false。</summary>
    Task<bool> TryDeleteProgramAsync(string programId, string changedBy, CancellationToken cancellationToken);

    /// <summary>留档的旧版本，新的在前。</summary>
    Task<IReadOnlyList<LibraryVersionEntry>> VersionsAsync(string kind, string itemId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILibraryService"/>
public sealed class LibraryService : ILibraryService
{
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IRollRepository rolls;
    private readonly ILibraryVersionRepository versions;
    private readonly IChangeLogRepository changeLog;
    private readonly TimeProvider timeProvider;

    public LibraryService(
        IRollProfileRepository profiles,
        IProgramRepository programs,
        IRollRepository rolls,
        ILibraryVersionRepository versions,
        IChangeLogRepository changeLog,
        TimeProvider timeProvider)
    {
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.versions = versions ?? throw new ArgumentNullException(nameof(versions));
        this.changeLog = changeLog ?? throw new ArgumentNullException(nameof(changeLog));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<IReadOnlyList<RollRecord>> ProfileUsersAsync(string profileId, CancellationToken cancellationToken) =>
        this.rolls.ListUsingAsync(profileId, null, cancellationToken);

    public Task<IReadOnlyList<RollRecord>> ProgramUsersAsync(string programId, CancellationToken cancellationToken) =>
        this.rolls.ListUsingAsync(null, programId, cancellationToken);

    public async Task<RollProfileDefinition> SaveProfileAsync(RollProfileDefinition profile, string changedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        RollProfileDefinition? existing = await this.profiles.GetAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
        int version = 1;
        if (existing is not null)
        {
            version = existing.Version + 1;
            await this.versions.AddAsync(new LibraryVersionEntry(
                SqliteLibraryVersionRepository.ProfileKind, existing.ProfileId, existing.Version, existing.Name, now, changedBy,
                LibrarySnapshotJson.Write(existing)), cancellationToken).ConfigureAwait(false);
        }

        RollProfileDefinition saved = profile with { Version = version, ModifiedAtUtc = now };
        await this.profiles.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        await LogAsync(ChangeLogAreas.ProfileLibrary, saved.Name, existing is null ? null : "v" + existing.Version, "v" + version, changedBy, now, cancellationToken)
            .ConfigureAwait(false);
        return saved;
    }

    public async Task<GrindingProgram> SaveProgramAsync(GrindingProgram program, string changedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(program);

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        GrindingProgram? existing = await this.programs.GetAsync(program.ProgramId, cancellationToken).ConfigureAwait(false);
        int version = 1;
        if (existing is not null)
        {
            version = existing.Version + 1;
            await this.versions.AddAsync(new LibraryVersionEntry(
                SqliteLibraryVersionRepository.ProgramKind, existing.ProgramId, existing.Version, existing.Name, now, changedBy,
                LibrarySnapshotJson.Write(existing)), cancellationToken).ConfigureAwait(false);
        }

        GrindingProgram saved = program with { Version = version, ModifiedAtUtc = now };
        await this.programs.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        await LogAsync(ChangeLogAreas.ProgramLibrary, saved.Name, existing is null ? null : "v" + existing.Version, "v" + version, changedBy, now, cancellationToken)
            .ConfigureAwait(false);
        return saved;
    }

    public async Task SetProfileDisabledAsync(string profileId, bool disabled, string changedBy, CancellationToken cancellationToken)
    {
        RollProfileDefinition? profile = await this.profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null || profile.Disabled == disabled)
        {
            return;
        }

        await this.profiles.SaveAsync(profile with { Disabled = disabled }, cancellationToken).ConfigureAwait(false);
        await LogAsync(ChangeLogAreas.ProfileLibrary, profile.Name, Flag(!disabled), Flag(disabled), changedBy, this.timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetProgramDisabledAsync(string programId, bool disabled, string changedBy, CancellationToken cancellationToken)
    {
        GrindingProgram? program = await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(false);
        if (program is null || program.Disabled == disabled)
        {
            return;
        }

        await this.programs.SaveAsync(program with { Disabled = disabled }, cancellationToken).ConfigureAwait(false);
        await LogAsync(ChangeLogAreas.ProgramLibrary, program.Name, Flag(!disabled), Flag(disabled), changedBy, this.timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryDeleteProfileAsync(string profileId, string changedBy, CancellationToken cancellationToken)
    {
        if ((await ProfileUsersAsync(profileId, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return false;
        }

        RollProfileDefinition? profile = await this.profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        await this.profiles.DeleteAsync(profileId, cancellationToken).ConfigureAwait(false);
        await LogAsync(ChangeLogAreas.ProfileLibrary, profile?.Name ?? profileId, "v" + (profile?.Version ?? 0), null, changedBy, this.timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryDeleteProgramAsync(string programId, string changedBy, CancellationToken cancellationToken)
    {
        if ((await ProgramUsersAsync(programId, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return false;
        }

        GrindingProgram? program = await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(false);
        await this.programs.DeleteAsync(programId, cancellationToken).ConfigureAwait(false);
        await LogAsync(ChangeLogAreas.ProgramLibrary, program?.Name ?? programId, "v" + (program?.Version ?? 0), null, changedBy, this.timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public Task<IReadOnlyList<LibraryVersionEntry>> VersionsAsync(string kind, string itemId, CancellationToken cancellationToken) =>
        this.versions.ListAsync(kind, itemId, cancellationToken);

    private static string Flag(bool disabled) => disabled ? "disabled" : "enabled";

    private Task LogAsync(string area, string item, string? oldValue, string? newValue, string changedBy, DateTimeOffset at, CancellationToken cancellationToken) =>
        this.changeLog.AddAsync(
            new[] { new ChangeLogEntry(Guid.NewGuid().ToString("N"), at, changedBy, area, item, oldValue, newValue) },
            cancellationToken);
}
