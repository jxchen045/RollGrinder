using System;

namespace RollGrinder.Services.Records;

/// <summary>
/// 一段时间的磨削汇总。日报与月报是同一件事，差别只在取的是哪一段时间——
/// 不必为"日"与"月"各写一份。
/// </summary>
/// <param name="FromUtc">起（含）。</param>
/// <param name="ToUtc">止（含）。</param>
/// <param name="TotalCount">这段时间开了几支辊。</param>
/// <param name="CompletedCount">其中磨完并判为合格的有几支。</param>
/// <param name="TotalDuration">磨削总时长（只算已收尾的）。</param>
/// <param name="DistinctRollCount">涉及几支不同的辊——同一支返修两次算一支。</param>
public sealed record GrindingSummary(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TotalCount,
    int CompletedCount,
    TimeSpan TotalDuration,
    int DistinctRollCount)
{
    /// <summary>
    /// 合格率。一支都没磨时是 null，不是 0——
    /// "这段时间没干活"与"干了活全不合格"是两回事。
    /// </summary>
    public double? CompletionRate => TotalCount == 0 ? null : (double)CompletedCount / TotalCount;

    /// <summary>平均每支多久。一支都没磨时为 null。</summary>
    public TimeSpan? AverageDuration =>
        CompletedCount == 0 ? null : TotalDuration / CompletedCount;
}

/// <summary>
/// 轧辊台账里的一行：这支辊是什么、被磨过几次、上次什么时候。
/// </summary>
/// <param name="RollId">辊件标识。</param>
/// <param name="Code">辊号。</param>
/// <param name="NominalDiameterMm">公称直径（mm）。</param>
/// <param name="BodyLengthMm">辊身长度（mm）。</param>
/// <param name="Material">材质；没登记时为 null。</param>
/// <param name="GrindCount">磨过几次。</param>
/// <param name="LastGroundAtUtc">最近一次是什么时候；一次都没磨过为 null。</param>
public sealed record RollLedgerRow(
    string RollId,
    string Code,
    double NominalDiameterMm,
    double BodyLengthMm,
    string? Material,
    int GrindCount,
    DateTimeOffset? LastGroundAtUtc);
