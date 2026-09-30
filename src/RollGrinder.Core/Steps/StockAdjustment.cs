using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Steps;

/// <summary>按本次余量调整工序的结果。<see cref="ProblemResourceKey"/> 非空时这份作业不能下发。</summary>
/// <param name="Steps">调整后的工序（粗磨的磨削量已改，其余原样）。</param>
/// <param name="ProgramStockMicrometer">程序里各道磨削量之和（直径量 µm）。</param>
/// <param name="FixedStockMicrometer">不动的那部分：粗磨以外各道之和。</param>
/// <param name="RoughStockMicrometer">调整后粗磨合计。</param>
/// <param name="ProblemResourceKey">拦住下发的原因（资源键），没有问题为 null。</param>
public sealed record StockAdjustmentResult(
    IReadOnlyList<GrindingJobStep> Steps,
    double ProgramStockMicrometer,
    double FixedStockMicrometer,
    double RoughStockMicrometer,
    string? ProblemResourceKey);

/// <summary>
/// 本次余量与程序标准余量不同时怎么分（分辨率适配与流程调整方案第 5 节）：
/// 半精磨、精磨、光磨等"精"工序决定表面质量，量保持程序里的值；<b>差额全部由粗磨吸收</b>，
/// 粗磨道次由工序按磨削量与每道进给自己重算（每道进给不变）。
/// 有几道粗磨时按它们原来的比例分；本次余量比精工序之和还少时拦住——不能靠少磨精磨凑数。
/// </summary>
public static class StockAdjustment
{
    /// <summary>1 µm 以内的差当作一样，不改程序。</summary>
    public const double ToleranceMicrometer = 1.0;

    public static StockAdjustmentResult Apply(IReadOnlyList<GrindingJobStep> steps, double actualStockMicrometer)
    {
        ArgumentNullException.ThrowIfNull(steps);

        double Stock(GrindingJobStep step) =>
            step.Parameters.GetNumberOrDefault(StepParameterKeys.StockDiameterMicrometer, 0.0);
        bool HasStock(GrindingJobStep step) => step.Parameters.TryGet(StepParameterKeys.StockDiameterMicrometer, out _);

        GrindingJobStep[] rough = steps.Where(step => step.StepTypeKey == StepTypeKeys.Rough && HasStock(step)).ToArray();
        double program = steps.Where(HasStock).Sum(Stock);
        double fixedPart = steps.Where(step => HasStock(step) && step.StepTypeKey != StepTypeKeys.Rough).Sum(Stock);
        double roughNow = rough.Sum(Stock);

        // 先比是否与程序一样：只测量不去量的程序（合计 0）配本次余量 0 也是对的。
        if (double.IsFinite(actualStockMicrometer) && Math.Abs(actualStockMicrometer - program) <= ToleranceMicrometer)
        {
            return new(steps, program, fixedPart, roughNow, null);
        }

        if (!double.IsFinite(actualStockMicrometer) || actualStockMicrometer <= 0.0)
        {
            return new(steps, program, fixedPart, roughNow, "Stock_ActualInvalid");
        }

        if (actualStockMicrometer < fixedPart - ToleranceMicrometer)
        {
            return new(steps, program, fixedPart, roughNow, "Stock_BelowFinishing");
        }

        if (rough.Length == 0)
        {
            return new(steps, program, fixedPart, roughNow, "Stock_NoRoughStep");
        }

        double roughTarget = Math.Max(actualStockMicrometer - fixedPart, 0.0);
        var adjusted = new List<GrindingJobStep>(steps.Count);
        foreach (GrindingJobStep step in steps)
        {
            if (Array.IndexOf(rough, step) < 0)
            {
                adjusted.Add(step);
                continue;
            }

            double share = roughNow > 0.0 ? Stock(step) / roughNow : 1.0 / rough.Length;
            double amount = Math.Round(roughTarget * share, 1);
            adjusted.Add(step with
            {
                Parameters = step.Parameters.With(StepParameterKeys.StockDiameterMicrometer, ParameterValue.FromNumber(amount)),
            });
        }

        return new(adjusted, program, fixedPart, roughTarget, null);
    }
}
