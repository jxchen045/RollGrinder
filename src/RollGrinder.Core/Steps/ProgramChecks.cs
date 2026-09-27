using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Steps;

/// <summary>跨工序检查发现的一类问题。</summary>
public enum ProgramFindingKind
{
    /// <summary>各道磨削量合计与总余量对不上（给了总余量才查）。<see cref="ProgramFinding.Value"/> 是合计，<see cref="ProgramFinding.Reference"/> 是总余量（直径 µm）。</summary>
    StockDoesNotAddUp = 0,

    /// <summary>最后一道去量的磨削之后没有测量：磨完量不到成品辊形。<see cref="ProgramFinding.StepOrder"/> 是那一道。</summary>
    NoMeasurementAfterGrinding = 1,

    /// <summary>后面一道（更精的）磨削量比前面一道还大：余量分配倒过来了。</summary>
    StockIncreasesTowardsFinish = 2,
}

/// <summary>跨工序检查的一条结果（修改稿 5.3"跨工序校验"）。是**提示**，不挡保存——工艺是人定的。</summary>
/// <param name="Kind">哪一类。</param>
/// <param name="StepOrder">说的是第几道（整支程序的问题为 null）。</param>
/// <param name="Value">相关的量（直径 µm）。</param>
/// <param name="Reference">对照的量（直径 µm）。</param>
public sealed record ProgramFinding(ProgramFindingKind Kind, int? StepOrder = null, double Value = 0.0, double Reference = 0.0);

/// <summary>
/// 跨工序的检查与余量分配（修改稿 5.3，阶段 2 留到阶段 3）。
///
/// 单道工序的参数范围、"周期进给 × 道次 与 磨削量对不上"、走刀工序进给大于 0、至少一道走拖板，
/// 已经在 <see cref="GrindingJobValidator"/> 里按错误报了；这里只看工序**之间**的关系，报成提示。
/// </summary>
public static class ProgramChecks
{
    /// <summary>合计与总余量差多少以内算对得上：1 µm 与 2% 取大。</summary>
    public static double StockTolerance(double totalMicrometer) => Math.Max(1.0, Math.Abs(totalMicrometer) * 0.02);

    /// <summary>这道工序会不会去量：有磨削量参数，并且至少一路进给不是 0。</summary>
    public static bool RemovesStock(GrindingJobStep step, GrindingStepTypeRegistry stepTypes)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(stepTypes);
        IGrindingStepType type = stepTypes.Get(step.StepTypeKey);
        if (!type.Schema.TryGet(StepParameterKeys.StockDiameterMicrometer, out _))
        {
            return false;
        }

        ParameterSet values = type.Schema.ApplyDefaults(step.Parameters);
        return values.GetNumberOrDefault(StepParameterKeys.InfeedPerPassDiameterMicrometer, 0.0) > 0.0
            || values.GetNumberOrDefault(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, 0.0) > 0.0;
    }

    /// <summary>检查一支程序。<paramref name="totalStockDiameterMicrometer"/> 为空时不查合计。</summary>
    public static IReadOnlyList<ProgramFinding> Find(
        IReadOnlyList<GrindingJobStep> steps,
        GrindingStepTypeRegistry stepTypes,
        double? totalStockDiameterMicrometer)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(stepTypes);

        var findings = new List<ProgramFinding>();
        GrindingJobStep[] removing = steps.Where(step => RemovesStock(step, stepTypes)).ToArray();

        if (totalStockDiameterMicrometer is double total && removing.Length > 0)
        {
            double sum = removing.Sum(step => Stock(step, stepTypes));
            if (Math.Abs(sum - total) > StockTolerance(total))
            {
                findings.Add(new ProgramFinding(ProgramFindingKind.StockDoesNotAddUp, null, sum, total));
            }
        }

        if (removing.Length > 0)
        {
            GrindingJobStep last = removing[^1];
            int lastIndex = IndexOf(steps, last);
            bool measuredAfter = steps.Skip(lastIndex + 1).Any(step =>
                step.StepTypeKey is StepTypeKeys.Measure or StepTypeKeys.Roundness);
            if (!measuredAfter)
            {
                findings.Add(new ProgramFinding(ProgramFindingKind.NoMeasurementAfterGrinding, last.Order));
            }
        }

        for (int i = 1; i < removing.Length; i++)
        {
            double before = Stock(removing[i - 1], stepTypes);
            double after = Stock(removing[i], stepTypes);
            if (Rank(removing[i - 1].StepTypeKey) >= 0 && Rank(removing[i].StepTypeKey) > Rank(removing[i - 1].StepTypeKey) && after > before + 1e-9)
            {
                findings.Add(new ProgramFinding(ProgramFindingKind.StockIncreasesTowardsFinish, removing[i].Order, after, before));
            }
        }

        return findings;
    }

    /// <summary>
    /// 把总余量分到各道去量的工序上：按它们现在的磨削量成比例分（保住粗、半精、精的比例）；
    /// 现在全是 0 就按工序类型的常用比例分。按 0.1 µm 取整，零头补在第一道（粗磨）上，合计正好等于总余量。
    /// </summary>
    public static IReadOnlyList<double> Distribute(IReadOnlyList<double> current, IReadOnlyList<string> stepTypeKeys, double totalMicrometer)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(stepTypeKeys);
        if (current.Count != stepTypeKeys.Count)
        {
            throw new DomainException("Stock distribution needs one step type per value.");
        }

        if (current.Count == 0)
        {
            return Array.Empty<double>();
        }

        if (!(totalMicrometer >= 0.0))
        {
            throw new DomainException("The total stock must not be negative.");
        }

        double[] weights = current.Sum() > 0.0
            ? current.Select(value => Math.Max(0.0, value)).ToArray()
            : stepTypeKeys.Select(TypicalShare).ToArray();
        double weightSum = weights.Sum();
        double[] shares = weights.Select(weight => Math.Round(totalMicrometer * weight / weightSum, 1, MidpointRounding.AwayFromZero)).ToArray();
        shares[0] = Math.Round(shares[0] + (totalMicrometer - shares.Sum()), 1, MidpointRounding.AwayFromZero);
        return shares;
    }

    /// <summary>现在全是 0 时的分法：粗磨拿大头、越精越少（照说明书实例表的量级）。</summary>
    private static double TypicalShare(string stepTypeKey) => stepTypeKey switch
    {
        StepTypeKeys.ShortStroke => 3.0,
        StepTypeKeys.Rough => 6.0,
        StepTypeKeys.SemiFinish => 2.5,
        StepTypeKeys.Finish => 1.0,
        StepTypeKeys.Polish => 0.5,
        _ => 1.0,
    };

    /// <summary>越往后越精：粗 &lt; 半精 &lt; 精 &lt; 抛光。别的类型不参与"倒过来"的判断。</summary>
    private static int Rank(string stepTypeKey) => stepTypeKey switch
    {
        StepTypeKeys.ShortStroke => 0,
        StepTypeKeys.Rough => 1,
        StepTypeKeys.SemiFinish => 2,
        StepTypeKeys.Finish => 3,
        StepTypeKeys.Polish => 4,
        _ => -1,
    };

    private static double Stock(GrindingJobStep step, GrindingStepTypeRegistry stepTypes) =>
        stepTypes.Get(step.StepTypeKey).Schema.ApplyDefaults(step.Parameters)
            .GetNumberOrDefault(StepParameterKeys.StockDiameterMicrometer, 0.0);

    private static int IndexOf(IReadOnlyList<GrindingJobStep> steps, GrindingJobStep step)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (ReferenceEquals(steps[i], step))
            {
                return i;
            }
        }

        return -1;
    }
}
