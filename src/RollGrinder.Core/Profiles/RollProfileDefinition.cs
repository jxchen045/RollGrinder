using System;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// 辊形库里的一条辊形：一个名字 + 一条可叠加的多段曲线。
///
/// 辊形是**可复用的模板**，不属于任何一支辊：同一条 CVC 辊形会被几十支辊用到。
/// 作业引用它的时候复制一份快照（<c>GrindingJob.Profile</c>），
/// 所以库里之后改了名、改了段，已经磨过的那支辊的记录不会跟着变。
/// </summary>
/// <param name="ProfileId">辊形标识。</param>
/// <param name="Name">辊形名，现场按这个名字找。</param>
/// <param name="BodyLengthMm">
/// 设计长度（mm）：画这条辊形时按的辊身长度，取自参考轧辊的台账（<see cref="ReferenceRollId"/>），不在辊形里手填。
/// 用到别的辊上按 2% 规则套：两端锥度段保持绝对长度，中间段伸缩铺满辊身。
/// </param>
/// <param name="Profile">多段叠加的曲线本体。</param>
/// <param name="CreatedAtUtc">建立时刻。</param>
/// <param name="ModifiedAtUtc">最后修改时刻，列表按它排序。</param>
public sealed record RollProfileDefinition(
    string ProfileId,
    string Name,
    double BodyLengthMm,
    CompositeRollProfile Profile,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc)
{
    /// <summary>
    /// 参考轧辊：这条辊形按哪支辊的辊身长度画的（台账辊号）。长度只有台账一个出处——辊形不收直径、不手填长度。
    /// 没记（旧条目、导入的条目）为 null。
    /// </summary>
    public string? ReferenceRollId { get; init; }

    /// <summary>辊形公差（直径量 µm）：形状规格的一部分，不属于某一支辊。没填为 null。</summary>
    public double? ToleranceMicrometer { get; init; }

    /// <summary>版本号：每保存一次 +1，作业快照记下用的是第几版。</summary>
    public int Version { get; init; } = 1;

    /// <summary>停用：被引用的辊形不能删，只能停用；停用后不出现在登记、换辊形的列表里。</summary>
    public bool Disabled { get; init; }

    /// <summary>主辊形（第一段）的曲线类型键，列表上显示用。</summary>
    public string ProfileTypeKey => Profile.Segments[0].ProfileTypeKey;

    /// <summary>叠了几段。</summary>
    public int SegmentCount => Profile.Segments.Count;

    /// <summary>建一条新辊形并校验。</summary>
    public static RollProfileDefinition Create(
        string profileId,
        string name,
        double bodyLengthMm,
        CompositeRollProfile profile,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(profile);

        if (bodyLengthMm <= 0.0)
        {
            throw new DomainException($"Roll profile '{name}' needs a positive reference body length.");
        }

        return new RollProfileDefinition(profileId, name, bodyLengthMm, profile, nowUtc, nowUtc);
    }
}
