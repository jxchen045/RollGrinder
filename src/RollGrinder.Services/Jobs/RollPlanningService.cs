using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Jobs;

/// <summary>待磨清单里一支辊为什么排在前面。</summary>
public enum QueueMark
{
    /// <summary>普通：按最近下线排。</summary>
    None = 0,

    /// <summary>中断待续：置顶。</summary>
    Interrupted = 1,

    /// <summary>不合格待返磨：置顶。</summary>
    Regrind = 2,
}

/// <summary>待磨清单的一行。</summary>
/// <param name="Roll">这支辊。</param>
/// <param name="Mark">置顶原因。</param>
/// <param name="JobId">中断 / 返磨时是哪一份作业；普通为 null。</param>
/// <param name="LastGroundAtUtc">上次磨削时刻。</param>
/// <param name="GrindCount">磨过几次。</param>
/// <param name="LastPassed">上次合格判定。</param>
public sealed record QueueEntry(RollRecord Roll, QueueMark Mark, string? JobId, DateTimeOffset? LastGroundAtUtc, int GrindCount, bool? LastPassed)
{
    /// <summary>剩余可磨量（mm）：当前直径 − 报废直径；没登记报废直径为 null。</summary>
    public double? RemainingMm => Roll.ScrapDiameterMm is double scrap ? Roll.StartDiameterMm - scrap : null;
}

/// <summary>记录区总览（界面修订稿 v3 6.8）：临近报废 · 今日计划变更 · 不合格待返磨。</summary>
/// <param name="NearScrap">剩余可磨量不到两次标准余量的辊（辊号、剩余 mm）。</param>
/// <param name="PlanChanges">指定时刻以来的计划变更（改动记录原样）。</param>
/// <param name="Failed">上次不合格的辊：辊号、那份作业、返磨是否已建。</param>
public sealed record RollOverview(
    IReadOnlyList<(string RollId, double RemainingMm)> NearScrap,
    IReadOnlyList<ChangeLogEntry> PlanChanges,
    IReadOnlyList<(string RollId, string? JobId, bool RegrindCreated)> Failed);

/// <summary>改计划时一支辊的结论。</summary>
/// <param name="RollId">辊号。</param>
/// <param name="Changed">改了。</param>
/// <param name="ProblemKey">没改的原因（资源键）；改了为 null。可以改但要提醒时是提示键（以 "PlanNotice_" 开头）。</param>
public sealed record PlanChangeOutcome(string RollId, bool Changed, string? ProblemKey);

/// <summary>导入预览里的一行。</summary>
/// <param name="LineNumber">文件里第几行（从 1 起，含表头）。</param>
/// <param name="Kind">新增 / 更新 / 错误。</param>
/// <param name="Roll">读出来的辊（错误行可能为 null）。</param>
/// <param name="ProblemKeys">问题（资源键），没有为空。</param>
public sealed record LedgerImportRow(int LineNumber, LedgerImportKind Kind, RollRecord? Roll, IReadOnlyList<string> ProblemKeys);

/// <summary>导入预览一行的性质。</summary>
public enum LedgerImportKind
{
    Add = 0,
    Update = 1,
    Error = 2,
}

/// <summary>
/// 以轧辊为中心的作业规划（关系设计第 5 节）：待磨清单、改计划（逐支或多支同一条路）、
/// 下发后写回、返磨、结束中断、台账导入。作业核对本身在 <see cref="JobChecklist"/>。
/// </summary>
public interface IRollPlanningService
{
    /// <summary>待磨清单：中断、待返磨的置顶，其余在用的辊按最近下线排。</summary>
    Task<IReadOnlyList<QueueEntry>> LoadQueueAsync(int limit, CancellationToken cancellationToken);

    /// <summary>台账里已有的用途（去重、排序），竖键里列出来让人选，避免同一用途两种写法。</summary>
    Task<IReadOnlyList<string>> PurposesAsync(CancellationToken cancellationToken);

    /// <summary>记录区总览：临近报废、<paramref name="sinceUtc"/> 以来的计划变更、不合格待返磨。</summary>
    Task<RollOverview> OverviewAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>改计划前逐支核对：作废的、长度差太多的、类型不符的列出来不改。</summary>
    Task<IReadOnlyList<PlanChangeOutcome>> PreviewPlanChangeAsync(
        IReadOnlyList<string> rollIds, string? profileId, string? programId, CancellationToken cancellationToken);

    /// <summary>改计划（逐支 = 只给一支）：核对通过的才改，每支各记一条改动记录。</summary>
    Task<IReadOnlyList<PlanChangeOutcome>> ChangePlanAsync(
        IReadOnlyList<string> rollIds, string? profileId, string? programId, string? reason, string changedBy, CancellationToken cancellationToken);

    /// <summary>
    /// 下发成功之后：选了"变更这支辊的工艺"的把计划写回台账并记改动；按计划下发的把"由历史推断"标记去掉（人已经确认过了）。
    /// </summary>
    Task AfterDownloadAsync(GrindingJob job, string changedBy, CancellationToken cancellationToken);

    /// <summary>不合格一键返磨：按同一计划新开一份作业（草稿），进待磨清单。返回新作业号。</summary>
    Task<string> CreateRegrindAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>中断的作业"结束并记录"：不再出现在待磨清单（记录已标中断，台账已按最后测量更新）。</summary>
    Task EndInterruptedAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>作废 / 恢复一支辊。</summary>
    Task SetRetiredAsync(string rollId, bool retired, string changedBy, CancellationToken cancellationToken);

    /// <summary>读台账导入文件（CSV，Excel 另存为 CSV 即可），逐行预览问题；什么都不写。</summary>
    Task<IReadOnlyList<LedgerImportRow>> PreviewImportAsync(string csvText, CancellationToken cancellationToken);

    /// <summary>把预览里没有错误的行写进台账，返回写了几支。有错的行不导入。</summary>
    Task<int> ImportAsync(IReadOnlyList<LedgerImportRow> rows, string changedBy, CancellationToken cancellationToken);

    /// <summary>导入模板 / 台账导出（同一格式）：表头 + 现有的辊。</summary>
    Task<string> ExportCsvAsync(bool includeRetired, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRollPlanningService"/>
public sealed class RollPlanningService : IRollPlanningService
{
    /// <summary>CSV 的列，导入导出共用。名称用于对照库里的辊形 / 程序。</summary>
    public static readonly IReadOnlyList<string> CsvColumns = new[]
    {
        "rollId", "kind", "bodyLengthMm", "nominalDiameterMm", "currentDiameterMm", "scrapDiameterMm",
        "material", "purpose", "profileName", "programName", "netWeightKg",
    };

    private readonly IRollRepository rolls;
    private readonly IJobRepository jobs;
    private readonly IGrindingRecordRepository records;
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IChangeLogRepository changeLog;
    private readonly Records.IRollLedgerService ledger;
    private readonly MachineDescription machine;
    private readonly TimeProvider timeProvider;

    public RollPlanningService(
        IRollRepository rolls,
        IJobRepository jobs,
        IGrindingRecordRepository records,
        IRollProfileRepository profiles,
        IProgramRepository programs,
        IChangeLogRepository changeLog,
        Records.IRollLedgerService ledger,
        MachineDescription machine,
        TimeProvider timeProvider)
    {
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.changeLog = changeLog ?? throw new ArgumentNullException(nameof(changeLog));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<QueueEntry>> LoadQueueAsync(int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<RollRecord> all = await this.rolls.ListAsync(limit, cancellationToken).ConfigureAwait(false);
        Dictionary<string, RollRecord> byId = all.ToDictionary(roll => roll.RollId, StringComparer.Ordinal);

        var pinned = new List<QueueEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JobListEntry job in await this.jobs.ListByStateAsync(JobState.Interrupted, limit, cancellationToken).ConfigureAwait(false))
        {
            if (byId.TryGetValue(job.RollId, out RollRecord? roll) && !roll.Retired && seen.Add(roll.RollId))
            {
                pinned.Add(await EntryAsync(roll, QueueMark.Interrupted, job.JobId, cancellationToken).ConfigureAwait(false));
            }
        }

        foreach (JobListEntry job in await this.jobs.ListByStateAsync(JobState.Draft, limit, cancellationToken).ConfigureAwait(false))
        {
            if (job.RegrindOf is not null && byId.TryGetValue(job.RollId, out RollRecord? roll) && !roll.Retired && seen.Add(roll.RollId))
            {
                pinned.Add(await EntryAsync(roll, QueueMark.Regrind, job.JobId, cancellationToken).ConfigureAwait(false));
            }
        }

        var rest = new List<QueueEntry>();
        foreach (RollRecord roll in all.Where(roll => !roll.Retired && !seen.Contains(roll.RollId)))
        {
            rest.Add(await EntryAsync(roll, QueueMark.None, null, cancellationToken).ConfigureAwait(false));
        }

        // 最近下线（上次磨削）在前；从没磨过的新辊按登记时间。
        return pinned.Concat(rest.OrderByDescending(entry => entry.LastGroundAtUtc ?? entry.Roll.CreatedAtUtc)).ToArray();
    }

    public async Task<RollOverview> OverviewAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        IReadOnlyList<QueueEntry> queue = await LoadQueueAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var perGrind = new Dictionary<string, double>(StringComparer.Ordinal);
        var nearScrap = new List<(string, double)>();
        foreach (QueueEntry entry in queue)
        {
            if (entry.RemainingMm is not double remaining || entry.Roll.ProgramId is not { } programId)
            {
                continue;
            }

            if (!perGrind.TryGetValue(programId, out double stockMm))
            {
                GrindingProgram? program = await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(false);
                stockMm = program is null ? 0.0
                    : (program.StandardStockMicrometer ?? StockAdjustment.Apply(program.Steps, 1.0).ProgramStockMicrometer) / 1000.0;
                perGrind[programId] = stockMm;
            }

            if (stockMm > 0.0 && remaining < JobChecklist.LowLifeFactor * stockMm)
            {
                nearScrap.Add((entry.Roll.RollId, remaining));
            }
        }

        IReadOnlyList<ChangeLogEntry> changes = (await this.changeLog.ListAsync(1000, cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.Area == ChangeLogAreas.RollPlan && entry.ChangedAtUtc >= sinceUtc)
            .OrderByDescending(entry => entry.ChangedAtUtc)
            .ToArray();

        var failed = queue
            .Where(entry => entry.Mark == QueueMark.Regrind || (entry.LastPassed == false && entry.Mark != QueueMark.Interrupted))
            .Select(entry => (entry.Roll.RollId, entry.JobId, entry.Mark == QueueMark.Regrind))
            .ToArray();

        return new RollOverview(nearScrap.OrderBy(item => item.Item2).ToArray(), changes, failed);
    }

    public async Task<IReadOnlyList<string>> PurposesAsync(CancellationToken cancellationToken) =>
        (await this.rolls.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
            .Select(roll => roll.Purpose?.Trim())
            .Where(purpose => !string.IsNullOrEmpty(purpose))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(purpose => purpose, StringComparer.CurrentCulture)
            .ToArray()!;

    public async Task<IReadOnlyList<PlanChangeOutcome>> PreviewPlanChangeAsync(
        IReadOnlyList<string> rollIds, string? profileId, string? programId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rollIds);

        RollProfileDefinition? profile = profileId is null ? null : await this.profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        GrindingProgram? program = programId is null ? null : await this.programs.GetAsync(programId, cancellationToken).ConfigureAwait(false);
        double tolerance = this.machine.Threshold(MachineDescription.LengthTolerancePercentKey) ?? BodyLengthFit.DefaultTolerancePercent;

        var outcomes = new List<PlanChangeOutcome>(rollIds.Count);
        foreach (string rollId in rollIds)
        {
            RollRecord? roll = await this.rolls.GetAsync(rollId, cancellationToken).ConfigureAwait(false);
            string? problem = roll switch
            {
                null => "PlanProblem_Missing",
                { Retired: true } => "PlanProblem_Retired",
                _ when profileId is not null && profile is null => "PlanProblem_Missing",
                _ when programId is not null && program is null => "PlanProblem_Missing",
                _ when profile is { Disabled: true } || program is { Disabled: true } => "PlanProblem_Disabled",
                _ when profile is not null
                    && BodyLengthFit.Fit(profile.Profile, profile.BodyLengthMm, roll.Geometry.BodyLengthMm, tolerance).Kind == BodyFitKind.TooDifferent
                    => "PlanProblem_Length",
                _ when program is not null && program.ApplicableRollKind != RollKind.Unspecified
                    && roll.Kind != RollKind.Unspecified && program.ApplicableRollKind != roll.Kind => "PlanProblem_Kind",
                _ => null,
            };

            if (problem is null && roll is not null && await HasOpenRegrindAsync(roll.RollId, cancellationToken).ConfigureAwait(false))
            {
                outcomes.Add(new PlanChangeOutcome(rollId, true, "PlanNotice_Regrind"));
                continue;
            }

            outcomes.Add(new PlanChangeOutcome(rollId, problem is null, problem));
        }

        return outcomes;
    }

    public async Task<IReadOnlyList<PlanChangeOutcome>> ChangePlanAsync(
        IReadOnlyList<string> rollIds, string? profileId, string? programId, string? reason, string changedBy, CancellationToken cancellationToken)
    {
        IReadOnlyList<PlanChangeOutcome> preview = await PreviewPlanChangeAsync(rollIds, profileId, programId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        var entries = new List<ChangeLogEntry>();
        foreach (PlanChangeOutcome outcome in preview.Where(o => o.Changed))
        {
            RollRecord roll = (await this.rolls.GetAsync(outcome.RollId, cancellationToken).ConfigureAwait(false))!;
            RollRecord changed = roll with
            {
                TargetProfileId = profileId ?? roll.TargetProfileId,
                ProgramId = programId ?? roll.ProgramId,
                PlanInferred = false,
            };
            await this.rolls.UpsertAsync(changed, cancellationToken).ConfigureAwait(false);
            entries.AddRange(await PlanLogAsync(roll, changed, reason, changedBy, now, cancellationToken).ConfigureAwait(false));
        }

        if (entries.Count > 0)
        {
            await this.changeLog.AddAsync(entries, cancellationToken).ConfigureAwait(false);
        }

        return preview;
    }

    public async Task AfterDownloadAsync(GrindingJob job, string changedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        RollRecord? roll = await this.rolls.GetAsync(job.RollId, cancellationToken).ConfigureAwait(false);
        if (roll is null)
        {
            return;
        }

        if (job.Deviation == JobDeviation.PlanChanged)
        {
            RollRecord changed = roll with { TargetProfileId = job.ProfileId, ProgramId = job.ProgramId, PlanInferred = false };
            await this.rolls.UpsertAsync(changed, cancellationToken).ConfigureAwait(false);
            await this.changeLog.AddAsync(
                await PlanLogAsync(roll, changed, job.JobId, changedBy, this.timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        else if (job.Deviation == JobDeviation.None && roll.PlanInferred)
        {
            await this.rolls.UpsertAsync(roll with { PlanInferred = false }, cancellationToken).ConfigureAwait(false);
        }
        else if (job.Deviation == JobDeviation.None && roll.TargetProfileId is null && roll.ProgramId is null)
        {
            // 旧辊没有计划：第一次作业用的就是它的计划。
            await this.rolls.UpsertAsync(roll with { TargetProfileId = job.ProfileId, ProgramId = job.ProgramId }, cancellationToken).ConfigureAwait(false);
        }

        // 中断续磨、返磨用的草稿到此为止：下发出去的就是那一份作业本身（同一作业号），状态已是已下发。
    }

    public async Task<string> CreateRegrindAsync(string jobId, CancellationToken cancellationToken)
    {
        (GrindingJob Job, JobState State) stored = await this.jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new DataStoreException($"Job '{jobId}' does not exist.");
        string newId = string.Create(CultureInfo.InvariantCulture, $"J{this.timeProvider.GetLocalNow():yyyyMMddHHmmss}R");
        GrindingJob draft = stored.Job with
        {
            JobId = newId,
            RegrindOfJobId = jobId,
            Deviation = JobDeviation.None,
            DeviationReason = null,
            StartDiameterMm = null,
            StockMicrometer = null,
        };
        await this.jobs.SaveAsync(draft, JobState.Draft, cancellationToken).ConfigureAwait(false);
        return newId;
    }

    public Task EndInterruptedAsync(string jobId, CancellationToken cancellationToken) =>
        this.jobs.SetStateAsync(jobId, JobState.Abandoned, cancellationToken);

    public async Task SetRetiredAsync(string rollId, bool retired, string changedBy, CancellationToken cancellationToken)
    {
        RollRecord? roll = await this.rolls.GetAsync(rollId, cancellationToken).ConfigureAwait(false);
        if (roll is null || roll.Retired == retired)
        {
            return;
        }

        await this.rolls.UpsertAsync(roll with { Retired = retired }, cancellationToken).ConfigureAwait(false);
        await this.changeLog.AddAsync(
            new[]
            {
                new ChangeLogEntry(Guid.NewGuid().ToString("N"), this.timeProvider.GetUtcNow(), changedBy, ChangeLogAreas.RollLedger,
                    rollId, retired ? "inUse" : "retired", retired ? "retired" : "inUse"),
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LedgerImportRow>> PreviewImportAsync(string csvText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(csvText);

        IReadOnlyList<IReadOnlyList<string>> table = Csv.Parse(csvText);
        if (table.Count == 0)
        {
            return Array.Empty<LedgerImportRow>();
        }

        Dictionary<string, int> column = table[0]
            .Select((name, index) => (name: name.Trim(), index))
            .Where(c => c.name.Length > 0)
            .GroupBy(c => c.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        var profileByName = (await this.profiles.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ProfileId, StringComparer.OrdinalIgnoreCase);
        var programByName = (await this.programs.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ProgramId, StringComparer.OrdinalIgnoreCase);

        string Cell(IReadOnlyList<string> row, string name) =>
            column.TryGetValue(name, out int index) && index < row.Count ? row[index].Trim() : string.Empty;

        var rows = new List<LedgerImportRow>();
        var idsInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        for (int i = 1; i < table.Count; i++)
        {
            IReadOnlyList<string> row = table[i];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var problems = new List<string>();
            string rollId = Cell(row, "rollId");
            if (rollId.Length == 0)
            {
                problems.Add("Import_Problem_MissingRollId");
            }
            else if (!idsInFile.Add(rollId))
            {
                problems.Add("Import_Problem_Duplicate");
            }

            double? length = Number(Cell(row, "bodyLengthMm"), problems, required: true);
            double? nominal = Number(Cell(row, "nominalDiameterMm"), problems, required: true);
            double? current = Number(Cell(row, "currentDiameterMm"), problems, required: false);
            double? scrap = Number(Cell(row, "scrapDiameterMm"), problems, required: false);
            double? weight = Number(Cell(row, "netWeightKg"), problems, required: false);
            RollKind kind = ParseKind(Cell(row, "kind"), problems);
            string? profileId = Lookup(Cell(row, "profileName"), profileByName, "Import_Problem_UnknownProfile", problems);
            string? programId = Lookup(Cell(row, "programName"), programByName, "Import_Problem_UnknownProgram", problems);

            RollRecord? roll = null;
            bool exists = false;
            if (rollId.Length > 0 && length is > 0.0 && nominal is > 0.0)
            {
                RollRecord? existing = await this.rolls.GetAsync(rollId, cancellationToken).ConfigureAwait(false);
                exists = existing is not null;
                roll = (existing ?? new RollRecord(rollId, rollId, Core.Geometry.RollGeometry.FromDiameter(length.Value, nominal.Value), null, now)) with
                {
                    Geometry = Core.Geometry.RollGeometry.FromDiameter(length.Value, nominal.Value),
                    Kind = kind,
                    CurrentDiameterMm = current ?? existing?.CurrentDiameterMm,
                    ScrapDiameterMm = scrap ?? existing?.ScrapDiameterMm,
                    Material = NullIfEmpty(Cell(row, "material")) ?? existing?.Material,
                    Purpose = NullIfEmpty(Cell(row, "purpose")) ?? existing?.Purpose,
                    TargetProfileId = profileId ?? existing?.TargetProfileId,
                    ProgramId = programId ?? existing?.ProgramId,
                    Data = (existing?.Data ?? RollDataSheet.Empty) with { NetWeightKg = weight ?? existing?.Data.NetWeightKg },
                };

                // 与登记同一套核对（关系设计 M3）：机床能力、计划必填、长度、类型。
                Records.RollLedgerSaveResult check = await this.ledger.ValidateAsync(roll, !exists, cancellationToken).ConfigureAwait(false);
                problems.AddRange(check.Problems.Select(problem => "Ledger_Problem_" + problem));
            }

            rows.Add(new LedgerImportRow(
                i + 1,
                problems.Count > 0 ? LedgerImportKind.Error : exists ? LedgerImportKind.Update : LedgerImportKind.Add,
                roll,
                problems));
        }

        return rows;
    }

    public async Task<int> ImportAsync(IReadOnlyList<LedgerImportRow> rows, string changedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int count = 0;
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        var entries = new List<ChangeLogEntry>();
        foreach (LedgerImportRow row in rows.Where(r => r.Kind != LedgerImportKind.Error && r.Roll is not null))
        {
            await this.rolls.UpsertAsync(row.Roll!, cancellationToken).ConfigureAwait(false);
            entries.Add(new ChangeLogEntry(Guid.NewGuid().ToString("N"), now, changedBy, ChangeLogAreas.RollLedger,
                row.Roll!.RollId, row.Kind == LedgerImportKind.Update ? "import:update" : null, "import"));
            count++;
        }

        if (entries.Count > 0)
        {
            await this.changeLog.AddAsync(entries, cancellationToken).ConfigureAwait(false);
        }

        return count;
    }

    public async Task<string> ExportCsvAsync(bool includeRetired, CancellationToken cancellationToken)
    {
        var profileNames = (await this.profiles.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false)).ToDictionary(p => p.ProfileId, p => p.Name);
        var programNames = (await this.programs.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false)).ToDictionary(p => p.ProgramId, p => p.Name);
        var lines = new List<IReadOnlyList<string>> { CsvColumns };
        foreach (RollRecord roll in (await this.rolls.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
                     .Where(roll => includeRetired || !roll.Retired).OrderBy(roll => roll.RollId, StringComparer.Ordinal))
        {
            lines.Add(new[]
            {
                roll.RollId,
                roll.Kind.ToString(),
                Text(roll.Geometry.BodyLengthMm),
                Text(roll.Geometry.NominalDiameterMm),
                Text(roll.CurrentDiameterMm),
                Text(roll.ScrapDiameterMm),
                roll.Material ?? string.Empty,
                roll.Purpose ?? string.Empty,
                roll.TargetProfileId is { } p && profileNames.TryGetValue(p, out string? pn) ? pn : string.Empty,
                roll.ProgramId is { } g && programNames.TryGetValue(g, out string? gn) ? gn : string.Empty,
                Text(roll.Data.NetWeightKg),
            });
        }

        return Csv.Write(lines);
    }

    private async Task<QueueEntry> EntryAsync(RollRecord roll, QueueMark mark, string? jobId, CancellationToken cancellationToken)
    {
        IReadOnlyList<GrindingRecord> history = await this.records.QueryByRollAsync(roll.RollId, 200, cancellationToken).ConfigureAwait(false);
        GrindingRecord? last = history.FirstOrDefault();
        return new QueueEntry(roll, mark, jobId, last?.FinishedAtUtc ?? last?.StartedAtUtc, history.Count, last?.Passed);
    }

    private async Task<bool> HasOpenRegrindAsync(string rollId, CancellationToken cancellationToken) =>
        (await this.jobs.ListByStateAsync(JobState.Draft, 200, cancellationToken).ConfigureAwait(false))
            .Any(job => job.RollId == rollId && job.RegrindOf is not null);

    private async Task<IReadOnlyList<ChangeLogEntry>> PlanLogAsync(
        RollRecord before, RollRecord after, string? reason, string changedBy, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var entries = new List<ChangeLogEntry>();
        if (before.TargetProfileId != after.TargetProfileId)
        {
            entries.Add(new ChangeLogEntry(Guid.NewGuid().ToString("N"), at, changedBy, ChangeLogAreas.RollPlan,
                before.RollId + " · profile" + (reason is null ? string.Empty : " · " + reason),
                await ProfileNameAsync(before.TargetProfileId, cancellationToken).ConfigureAwait(false),
                await ProfileNameAsync(after.TargetProfileId, cancellationToken).ConfigureAwait(false)));
        }

        if (before.ProgramId != after.ProgramId)
        {
            entries.Add(new ChangeLogEntry(Guid.NewGuid().ToString("N"), at, changedBy, ChangeLogAreas.RollPlan,
                before.RollId + " · program" + (reason is null ? string.Empty : " · " + reason),
                await ProgramNameAsync(before.ProgramId, cancellationToken).ConfigureAwait(false),
                await ProgramNameAsync(after.ProgramId, cancellationToken).ConfigureAwait(false)));
        }

        return entries;
    }

    private async Task<string?> ProfileNameAsync(string? id, CancellationToken cancellationToken) =>
        id is null ? null : (await this.profiles.GetAsync(id, cancellationToken).ConfigureAwait(false)) is { } p ? $"{p.Name} v{p.Version}" : id;

    private async Task<string?> ProgramNameAsync(string? id, CancellationToken cancellationToken) =>
        id is null ? null : (await this.programs.GetAsync(id, cancellationToken).ConfigureAwait(false)) is { } p ? $"{p.Name} v{p.Version}" : id;

    private static double? Number(string text, List<string> problems, bool required)
    {
        if (text.Length == 0)
        {
            if (required)
            {
                problems.Add("Import_Problem_MissingNumber");
            }

            return null;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return value;
        }

        problems.Add("Import_Problem_NotANumber");
        return null;
    }

    private static RollKind ParseKind(string text, List<string> problems)
    {
        if (text.Length == 0)
        {
            return RollKind.WorkRoll;
        }

        if (Enum.TryParse(text, true, out RollKind kind) && Enum.IsDefined(kind))
        {
            return kind;
        }

        // 现场表格常写中文。
        switch (text)
        {
            case "工作辊":
                return RollKind.WorkRoll;
            case "支承辊":
                return RollKind.BackupRoll;
            default:
                problems.Add("Import_Problem_Kind");
                return RollKind.Unspecified;
        }
    }

    private static string? Lookup(string name, IReadOnlyDictionary<string, string> byName, string problemKey, List<string> problems)
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (byName.TryGetValue(name, out string? id))
        {
            return id;
        }

        problems.Add(problemKey);
        return null;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private static string Text(double? value) => value is double number ? number.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
}

/// <summary>最小的 CSV 读写（RFC 4180：逗号分隔、双引号包裹、两个引号表示一个引号）。</summary>
public static class Csv
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        string body = text.TrimStart('﻿');
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < body.Length && body[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    cell.Append(c);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    public static string Write(IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new System.Text.StringBuilder();
        foreach (IReadOnlyList<string> row in rows)
        {
            builder.AppendJoin(',', row.Select(Quote));
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    private static string Quote(string cell) =>
        cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
}
