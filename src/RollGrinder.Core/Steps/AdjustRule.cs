using System;
using System.Collections.Generic;
using System.Globalization;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Steps;

/// <summary>作业里这个参数可以怎么调（设计说明 5.2 / 5.3）。</summary>
public enum AdjustMode
{
    /// <summary>不可调：要改就改方案（🔒）。</summary>
    Locked = 0,

    /// <summary>在方案值上下浮动百分之几（±%）。</summary>
    Percent = 1,

    /// <summary>只能调小（切深类：小一点不会出事）。</summary>
    DecreaseOnly = 2,

    /// <summary>只能调大（光磨道次：多磨两道不会出事）。</summary>
    IncreaseOnly = 3,

    /// <summary>方案里写死的上下限（绝对值）。</summary>
    Absolute = 4,
}

/// <summary>
/// 一个参数在作业里的可调范围规则。方案没单独设时取机床配置的默认范围表（设计稿 v4 5.10.2）。
/// 文本写法（配置与存库共用）：<c>locked</c> · <c>±10%</c> · <c>decrease</c> · <c>increase</c> · <c>4..8</c>。
/// </summary>
/// <param name="Mode">可调方式。</param>
/// <param name="Percent">±% 的幅度（百分数，如 10）。</param>
/// <param name="Lower">绝对下限。</param>
/// <param name="Upper">绝对上限。</param>
public sealed record AdjustRule(AdjustMode Mode, double Percent = 0.0, double? Lower = null, double? Upper = null)
{
    public static AdjustRule Locked { get; } = new(AdjustMode.Locked);

    public static AdjustRule PlusMinus(double percent) => new(AdjustMode.Percent, Math.Abs(percent));

    public static AdjustRule DecreaseOnly { get; } = new(AdjustMode.DecreaseOnly);

    public static AdjustRule IncreaseOnly { get; } = new(AdjustMode.IncreaseOnly);

    public static AdjustRule Between(double lower, double upper) =>
        lower > upper ? throw new DomainException("An adjust range needs lower <= upper.") : new(AdjustMode.Absolute, 0.0, lower, upper);

    /// <summary>存库 / 配置用的文本。</summary>
    public string ToText() => Mode switch
    {
        AdjustMode.Percent => string.Create(CultureInfo.InvariantCulture, $"±{Percent:0.##}%"),
        AdjustMode.DecreaseOnly => "decrease",
        AdjustMode.IncreaseOnly => "increase",
        AdjustMode.Absolute => string.Create(CultureInfo.InvariantCulture, $"{Lower:0.###}..{Upper:0.###}"),
        _ => "locked",
    };

    /// <summary>读文本；读不懂返回 null（不猜）。</summary>
    public static AdjustRule? Parse(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return null;
        }

        switch (t.ToLowerInvariant())
        {
            case "locked":
                return Locked;
            case "decrease":
                return DecreaseOnly;
            case "increase":
                return IncreaseOnly;
        }

        if ((t.StartsWith('±') || t.StartsWith('+')) && t.EndsWith('%')
            && double.TryParse(t[1..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) && percent >= 0.0)
        {
            return PlusMinus(percent);
        }

        int dots = t.IndexOf("..", StringComparison.Ordinal);
        if (dots > 0
            && double.TryParse(t[..dots], NumberStyles.Float, CultureInfo.InvariantCulture, out double lower)
            && double.TryParse(t[(dots + 2)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double upper)
            && lower <= upper)
        {
            return Between(lower, upper);
        }

        return null;
    }
}

/// <summary>按方案值换算成绝对值的允许范围（设计稿 v4 O10：范围写绝对值）。</summary>
/// <param name="Adjustable">能调（不是 🔒）。</param>
/// <param name="Lower">下限（含）；无下限为 null。</param>
/// <param name="Upper">上限（含）；无上限为 null。</param>
public sealed record AdjustBounds(bool Adjustable, double? Lower, double? Upper)
{
    public bool Contains(double value) =>
        Adjustable && (Lower is not double low || value >= low - 1e-9) && (Upper is not double high || value <= high + 1e-9);
}

/// <summary>调整被拒的原因（资源键 "Adjust_" + 名字）。</summary>
public enum AdjustRefusal
{
    None = 0,

    /// <summary>不可调（要改就改方案）。</summary>
    Locked = 1,

    /// <summary>低于允许范围。</summary>
    BelowRange = 2,

    /// <summary>高于允许范围。</summary>
    AboveRange = 3,
}

/// <summary>
/// 三道限制（设计说明 5.2）：机床限制（参数自身的上下限，制造商硬上限）、方案范围（工艺员）、作业取值（操作工在前两道以内调）。
/// 默认可调范围（设计说明 5.3 初稿）：拖板速度、头架转速 ±10%；周期 / 连续进给只能调小；光磨道次只能加；折返时间 ±50%；其余不能调。
/// </summary>
public static class AdjustRanges
{
    /// <summary>默认范围表（机床配置没写时用）。</summary>
    public static IReadOnlyDictionary<string, AdjustRule> Defaults { get; } = new Dictionary<string, AdjustRule>(StringComparer.Ordinal)
    {
        [StepParameterKeys.FeedMmPerMin] = AdjustRule.PlusMinus(10.0),
        [StepParameterKeys.WorkpieceSpeedRpm] = AdjustRule.PlusMinus(10.0),
        [StepParameterKeys.InfeedPerPassDiameterMicrometer] = AdjustRule.DecreaseOnly,
        [StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin] = AdjustRule.DecreaseOnly,
        [StepParameterKeys.SparkOutPassCount] = AdjustRule.IncreaseOnly,
        [StepParameterKeys.ReversalDwellSeconds] = AdjustRule.PlusMinus(50.0),
    };

    /// <summary>
    /// 某参数的规则：方案覆盖 → 机床配置的默认表 → 内置默认表 → 不可调。
    /// </summary>
    public static AdjustRule RuleFor(
        string parameterKey, AdjustRule? planOverride, IReadOnlyDictionary<string, AdjustRule>? machineDefaults)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameterKey);
        if (planOverride is not null)
        {
            return planOverride;
        }

        if (machineDefaults is not null && machineDefaults.TryGetValue(parameterKey, out AdjustRule? configured))
        {
            return configured;
        }

        return Defaults.TryGetValue(parameterKey, out AdjustRule? builtIn) ? builtIn : AdjustRule.Locked;
    }

    /// <summary>按方案值换算成绝对范围，再与参数自身的上下限（机床硬限）取交集。不是数值参数的一律不可调。</summary>
    public static AdjustBounds Resolve(AdjustRule rule, double planValue, ParameterDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (rule.Mode == AdjustMode.Locked || descriptor.Kind != ParameterValueKind.Number)
        {
            return new AdjustBounds(false, planValue, planValue);
        }

        (double? lower, double? upper) = rule.Mode switch
        {
            AdjustMode.Percent => (planValue - (Math.Abs(planValue) * rule.Percent / 100.0), planValue + (Math.Abs(planValue) * rule.Percent / 100.0)),
            AdjustMode.DecreaseOnly => ((double?)null, (double?)planValue),
            AdjustMode.IncreaseOnly => ((double?)planValue, (double?)null),
            _ => (rule.Lower, rule.Upper),
        };

        if (descriptor.MinValue is double min)
        {
            lower = lower is double l ? Math.Max(l, min) : min;
        }

        if (descriptor.MaxValue is double max)
        {
            upper = upper is double u ? Math.Min(u, max) : max;
        }

        return new AdjustBounds(true, lower, upper);
    }

    /// <summary>作业取值能不能收：不可调、低于、高于都不收。等于方案值总是收。</summary>
    public static AdjustRefusal Check(AdjustRule rule, double planValue, double value, ParameterDescriptor descriptor)
    {
        if (Math.Abs(value - planValue) <= 1e-9)
        {
            return AdjustRefusal.None;
        }

        AdjustBounds bounds = Resolve(rule, planValue, descriptor);
        if (!bounds.Adjustable)
        {
            return AdjustRefusal.Locked;
        }

        if (bounds.Lower is double low && value < low - 1e-9)
        {
            return AdjustRefusal.BelowRange;
        }

        return bounds.Upper is double high && value > high + 1e-9 ? AdjustRefusal.AboveRange : AdjustRefusal.None;
    }
}

/// <summary>作业里的一项调整（设计说明 5.4：参数、方案值、本次值、人、时间；核对时与运行中都算）。</summary>
/// <param name="StepOrder">哪道工序。</param>
/// <param name="ParameterKey">哪个参数。</param>
/// <param name="PlanValue">方案值。</param>
/// <param name="JobValue">本次值。</param>
/// <param name="Running">运行中改的（磨削当中，NC 下一道起采用）；核对时改的为假。</param>
/// <param name="ChangedBy">谁改的。</param>
/// <param name="ChangedAtUtc">什么时候改的。</param>
public sealed record JobAdjustment(
    int StepOrder,
    string ParameterKey,
    double PlanValue,
    double JobValue,
    bool Running,
    string ChangedBy,
    DateTimeOffset ChangedAtUtc)
{
    /// <summary>运行中改的：这次改参数的序号（接口约定第 3 节）；核对时改的为 null。</summary>
    public int? EditSeq { get; init; }

    /// <summary>NC 从哪一道工序起采用（已采用序号追上 <see cref="EditSeq"/> 时记下）；还没采用为 null。</summary>
    public int? EffectiveStepOrder { get; init; }

    /// <summary>NC 从那道工序的第几道次起采用（从 1 起）；还没采用为 null。</summary>
    public int? EffectivePass { get; init; }

    /// <summary>运行中改的、NC 还没采用（作业结束时仍是这样就是"未生效"）。</summary>
    public bool IsPending => Running && EditSeq is not null && EffectivePass is null;
}
