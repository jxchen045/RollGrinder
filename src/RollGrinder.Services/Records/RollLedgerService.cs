using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
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
}

/// <inheritdoc cref="IRollLedgerService"/>
public sealed class RollLedgerService : IRollLedgerService
{
    private readonly IRollRepository rolls;
    private readonly IGrindingRecordRepository records;
    private readonly MachineDescription machine;

    public RollLedgerService(IRollRepository rolls, IGrindingRecordRepository records, MachineDescription machine)
    {
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.records = records ?? throw new ArgumentNullException(nameof(records));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
    }

    public Task<RollRecord?> GetAsync(string rollId, CancellationToken cancellationToken) =>
        this.rolls.GetAsync(rollId, cancellationToken);

    public Task<IReadOnlyList<GrindingRecord>> HistoryAsync(string rollId, int limit, CancellationToken cancellationToken) =>
        this.records.QueryByRollAsync(rollId, limit, cancellationToken);

    public async Task<RollLedgerSaveResult> SaveAsync(RollRecord roll, bool isNew, CancellationToken cancellationToken)
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

        if (problems.Count > 0)
        {
            return new RollLedgerSaveResult(false, problems);
        }

        await this.rolls.UpsertAsync(roll with { RollId = roll.RollId.Trim() }, cancellationToken).ConfigureAwait(false);
        return new RollLedgerSaveResult(true, Array.Empty<RollLedgerProblem>());
    }

    private static bool Within(double value, double minimum, double maximum) => value >= minimum && value <= maximum;
}
