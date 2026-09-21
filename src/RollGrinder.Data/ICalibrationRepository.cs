using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Data;

/// <summary>一项标定值最后一次是谁改的、什么时候改的。换砂轮是要追溯的事。</summary>
/// <param name="ParameterKey">参数键。</param>
/// <param name="ChangedAtUtc">改动时刻。</param>
/// <param name="ChangedBy">改动人的用户名。</param>
public sealed record CalibrationAudit(string ParameterKey, DateTimeOffset ChangedAtUtc, string ChangedBy);

/// <summary>现场标定值的仓储。</summary>
public interface ICalibrationRepository
{
    /// <summary>读回全部标定值；一条都没有时返回空集，由上层补默认值。</summary>
    Task<ParameterSet> LoadAsync(CancellationToken cancellationToken);

    /// <summary>读回每一项的改动记录。</summary>
    Task<IReadOnlyList<CalibrationAudit>> LoadAuditAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 只写**确实变了**的那几项，并记下改动人与时刻。
    /// 没变的项不重写——否则每次打开设置页按一下保存，所有项的"最后改动"都会被刷成今天。
    /// </summary>
    Task SaveAsync(ParameterSet values, string changedBy, CancellationToken cancellationToken);
}
