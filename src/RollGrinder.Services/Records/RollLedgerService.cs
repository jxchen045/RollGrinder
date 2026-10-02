using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Profiles;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Records;

/// <summary>台账里一支辊存不进去的原因。界面按它取文案。</summary>
public enum RollLedgerProblem
{
    /// <summary>辊号没填。</summary>
    MissingRollId = 0,

    /// <summary>新登记的辊号已经有了。</summary>
    RollIdTaken = 1,

    /// <summary>辊身长度超出本台机床能磨的范围。</summary>
    BodyLengthOutOfRange = 2,

    /// <summary>公称直径超出本台机床能磨的范围。</summary>
    DiameterOutOfRange = 3,

    /// <summary>当前直径超出本台机床能磨的范围。</summary>
    CurrentDiameterOutOfRange = 4,

    /// <summary>重量是负数。</summary>
    NegativeWeight = 5,

    /// <summary>新登记的辊没选目标辊形或磨削程序（计划必填）。</summary>
    PlanMissing = 6,

    /// <summary>报废直径不小于公称直径。</summary>
    ScrapNotBelowNominal = 7,

    /// <summary>重量超出本台机床能磨的范围。</summary>
    WeightOutOfRange = 8,

    /// <summary>选的辊形设计长度与辊身长度差得太多（2% 规则拦住）。</summary>
    ProfileLengthMismatch = 9,

    /// <summary>选的程序不适用这种轧辊类型。</summary>
    ProgramKindMismatch = 10,

    /// <summary>选的辊形或程序已停用。</summary>
    PlanDisabled = 11,
}

/// <summary>存台账的结果。</summary>
/// <param name="Saved">存进去了。</param>
/// <param name="Problems">没存的原因，逐条。</param>
public sealed record RollLedgerSaveResult(bool Saved, IReadOnlyList<RollLedgerProblem> Problems);

/// <summary>
/// 轧辊台账（阶段 1，修改稿 5.1）：尺寸属于轧辊本身，在这里登记、修改——
/// 以前长度与直径填在工序编程页，重量在"轧辊数据"子页，两处各一份、互相不知道。
/// 作业只是"选一支台账里的辊"。
/// </summary>
public interface IRollLedgerService
{
    Task<RollRecord?> GetAsync(string rollId, CancellationToken cancellationToken);

    /// <summary>这支辊的磨削履历，最近的在前。</summary>
    Task<IReadOnlyList<GrindingRecord>> HistoryAsync(string rollId, int limit, CancellationToken cancellationToken);

    /// <summary>校验并存。<paramref name="isNew"/> 时辊号不能和已有的重复。</summary>
    Task<RollLedgerSaveResult> SaveAsync(RollRecord roll, bool isNew, CancellationToken cancellationToken);

    /// <summary>只校验不存（导入预览用）。</summary>
    Task<RollLedgerSaveResult> ValidateAsync(RollRecord roll, bool isNew, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRollLedgerService"/>
public sealed class RollLedgerService : IRollLedgerService
{
    private readonly IRollRepository rolls;
    private readonly IGrindingRecordRepository records;
    private readonly MachineDescription machine;
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;

    public RollLedgerService(
        IRollRepository rolls,
        IGrindingRecordRepository records,
        MachineDescription machine,
        IRollProfileRepository profiles,
        IProgramRepository programs)
    {
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
    }

    public Task<RollRecord?> GetAsync(string rollId, CancellationToken cancellationToken) =>
        this.rolls.GetAsync(rollId, cancellationToken);

    public Task<IReadOnlyList<GrindingRecord>> HistoryAsync(string rollId, int limit, CancellationToken cancellationToken) =>
        this.records.QueryByRollAsync(rollId, limit, cancellationToken);

    public async Task<RollLedgerSaveResult> SaveAsync(RollRecord roll, bool isNew, CancellationToken cancellationToken)
    {
        RollLedgerSaveResult result = await ValidateAsync(roll, isNew, cancellationToken).ConfigureAwait(false);
        if (result.Saved)
        {
            await this.rolls.UpsertAsync(roll with { RollId = roll.RollId.Trim() }, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<RollLedgerSaveResult> ValidateAsync(RollRecord roll, bool isNew, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roll);

        var problems = new List<RollLedgerProblem>();
        if (string.IsNullOrWhiteSpace(roll.RollId))
        {
            problems.Add(RollLedgerProblem.MissingRollId);
        }
        else if (isNew && await this.rolls.GetAsync(roll.RollId.Trim(), cancellationToken).ConfigureAwait(false) is not null)
        {
            problems.Add(RollLedgerProblem.RollIdTaken);
        }

        WorkpieceLimits limits = this.machine.Workpiece;
        if (!Within(roll.Geometry.BodyLengthMm, limits.MinBodyLengthMm, limits.MaxBodyLengthMm))
        {
            problems.Add(RollLedgerProblem.BodyLengthOutOfRange);
        }

        if (!Within(roll.Geometry.NominalDiameterMm, limits.MinDiameterMm, limits.MaxDiameterMm))
        {
            problems.Add(RollLedgerProblem.DiameterOutOfRange);
        }

        if (roll.CurrentDiameterMm is double current && !Within(current, limits.MinDiameterMm, limits.MaxDiameterMm))
        {
            problems.Add(RollLedgerProblem.CurrentDiameterOutOfRange);
        }

        RollDataSheet data = roll.Data;
        if (data.NetWeightKg < 0.0 || data.HeadBoxWeightKg < 0.0 || data.TailBoxWeightKg < 0.0)
        {
            problems.Add(RollLedgerProblem.NegativeWeight);
        }
        else if (limits.MaxWeightKg > 0.0 && (data.TotalWeightKg ?? data.NetWeightKg) > limits.MaxWeightKg)
        {
            problems.Add(RollLedgerProblem.WeightOutOfRange);
        }

        if (roll.ScrapDiameterMm is double scrap && scrap >= roll.Geometry.NominalDiameterMm)
        {
            problems.Add(RollLedgerProblem.ScrapNotBelowNominal);
        }

        await CheckPlanAsync(roll, isNew, problems, cancellationToken).ConfigureAwait(false);

        if (problems.Count > 0)
        {
            return new RollLedgerSaveResult(false, problems);
        }

        return new RollLedgerSaveResult(true, Array.Empty<RollLedgerProblem>());
    }

    private static bool Within(double value, double minimum, double maximum) => value >= minimum && value <= maximum;

    /// <summary>
    /// 计划核对（关系设计 5.1）：新登记必选目标辊形与磨削程序；选了的要对得上这支辊——
    /// 辊形长度按 2% 规则、程序适用类型；停用的不能选。
    /// </summary>
    private async Task CheckPlanAsync(RollRecord roll, bool isNew, List<RollLedgerProblem> problems, CancellationToken cancellationToken)
    {
        if (isNew && (string.IsNullOrWhiteSpace(roll.TargetProfileId) || string.IsNullOrWhiteSpace(roll.ProgramId)))
        {
            problems.Add(RollLedgerProblem.PlanMissing);
        }

        if (!string.IsNullOrWhiteSpace(roll.TargetProfileId)
            && await this.profiles.GetAsync(roll.TargetProfileId, cancellationToken).ConfigureAwait(false) is { } profile)
        {
            if (profile.Disabled)
            {
                problems.Add(RollLedgerProblem.PlanDisabled);
            }

            double tolerance = this.machine.Threshold(MachineDescription.LengthTolerancePercentKey) ?? BodyLengthFit.DefaultTolerancePercent;
            if (roll.Geometry.BodyLengthMm > 0.0
                && BodyLengthFit.Fit(profile.Profile, profile.BodyLengthMm, roll.Geometry.BodyLengthMm, tolerance).Kind == BodyFitKind.TooDifferent)
            {
                problems.Add(RollLedgerProblem.ProfileLengthMismatch);
            }
        }

        if (!string.IsNullOrWhiteSpace(roll.ProgramId)
            && await this.programs.GetAsync(roll.ProgramId, cancellationToken).ConfigureAwait(false) is { } program)
        {
            if (program.Disabled && !problems.Contains(RollLedgerProblem.PlanDisabled))
            {
                problems.Add(RollLedgerProblem.PlanDisabled);
            }

            if (program.ApplicableRollKind != RollKind.Unspecified && roll.Kind != RollKind.Unspecified && program.ApplicableRollKind != roll.Kind)
            {
                problems.Add(RollLedgerProblem.ProgramKindMismatch);
            }
        }
    }
}
