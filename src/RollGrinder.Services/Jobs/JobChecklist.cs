using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Jobs;

/// <summary>核对一项的结论。</summary>
public enum JobCheckStatus
{
    /// <summary>通过。</summary>
    Pass = 0,

    /// <summary>提示：不拦，但要让人看见（橙）。</summary>
    Notice = 1,

    /// <summary>不通过：不能下发（红）。</summary>
    Block = 2,
}

/// <summary>核对清单的一项。</summary>
/// <param name="Item">哪一项（<see cref="JobCheckItems"/>）。</param>
/// <param name="Status">结论。</param>
/// <param name="MessageKey">说明的资源键（"Check_" + 项 + "_" + 情形），用 <paramref name="Args"/> 格式化。</param>
/// <param name="Args">格式化参数（数值按界面量）。</param>
public sealed record JobCheck(string Item, JobCheckStatus Status, string MessageKey, IReadOnlyList<object> Args);

/// <summary>核对清单的项名（资源键 "CheckItem_" + 项名）。</summary>
public static class JobCheckItems
{
    public const string Roll = "Roll";
    public const string StartDiameter = "StartDiameter";
    public const string Stock = "Stock";
    public const string StockSplit = "StockSplit";
    public const string Length = "Length";
    public const string RollKind = "RollKind";
    public const string Material = "Material";
    public const string WorkSpeed = "WorkSpeed";
    public const string WeightSpeed = "WeightSpeed";
    public const string Stroke = "Stroke";
    public const string Library = "Library";
    public const string Deviation = "Deviation";
    public const string RemainingLife = "RemainingLife";
    public const string Parameters = "Parameters";
}

/// <summary>核对的输入：这支辊、这一次用的辊形与程序、磨前直径与本次磨削量。</summary>
/// <param name="Roll">台账里的这支辊。</param>
/// <param name="Profile">这一次用的辊形。</param>
/// <param name="Program">这一次用的程序。</param>
/// <param name="StartDiameterMm">磨前直径（mm）。</param>
/// <param name="StartMeasured">磨前直径是实测的（不是台账值）。</param>
/// <param name="StockMicrometer">本次磨削量（直径量 µm）。</param>
/// <param name="Deviation">与台账计划的关系。</param>
/// <param name="DeviationReason">仅本次的原因。</param>
public sealed record JobCheckInput(
    RollRecord Roll,
    RollProfileDefinition Profile,
    GrindingProgram Program,
    double StartDiameterMm,
    bool StartMeasured,
    double StockMicrometer,
    JobDeviation Deviation,
    string? DeviationReason);

/// <summary>核对结果：清单 + 按规则套好的辊形与调整好的工序（全部通过时才下发）。</summary>
/// <param name="Checks">逐项结论。</param>
/// <param name="Fit">辊形套到辊身上的结果。</param>
/// <param name="Stock">按本次磨削量调整后的工序。</param>
public sealed record JobCheckResult(IReadOnlyList<JobCheck> Checks, BodyFitResult Fit, StockAdjustmentResult Stock)
{
    /// <summary>没有拦住的项。</summary>
    public bool CanDownload => Checks.All(check => check.Status != JobCheckStatus.Block);

    /// <summary>通过的项数。</summary>
    public int PassCount => Checks.Count(check => check.Status == JobCheckStatus.Pass);
}

/// <summary>
/// 作业核对清单（关系设计第 6 节）：一处算全部规则，核对页、下发、自检都用它。
/// 纯计算，不读库、不碰机床——输入里给什么就核对什么。
/// </summary>
public static class JobChecklist
{
    /// <summary>剩余可磨量少于几次标准余量时提醒（关系设计 5.6）。</summary>
    public const double LowLifeFactor = 2.0;

    public static JobCheckResult Evaluate(JobCheckInput input, MachineDescription machine)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(machine);

        RollRecord roll = input.Roll;
        var checks = new List<JobCheck>();

        // 作废的辊、停用的库条目：不能下作业。
        checks.Add(roll.Retired
            ? Block(JobCheckItems.Roll, "Check_Roll_Retired")
            : Pass(JobCheckItems.Roll, "Check_Roll_Ok", roll.RollId));
        if (input.Profile.Disabled || input.Program.Disabled)
        {
            checks.Add(Block(JobCheckItems.Library, "Check_Library_Disabled",
                input.Profile.Disabled ? input.Profile.Name : input.Program.Name));
        }

        // 磨前直径：仍是公称值（新辊）且没实测时提醒先测。
        bool looksNew = !input.StartMeasured && roll.CurrentDiameterMm is null;
        checks.Add(looksNew
            ? Notice(JobCheckItems.StartDiameter, "Check_StartDiameter_Nominal", input.StartDiameterMm)
            : Pass(JobCheckItems.StartDiameter, "Check_StartDiameter_Ok", input.StartDiameterMm));

        // 本次磨削量 → 目标直径，不低于报废直径。
        double target = input.StartDiameterMm - (input.StockMicrometer / 1000.0);
        if (!double.IsFinite(input.StockMicrometer) || input.StockMicrometer < 0.0)
        {
            checks.Add(Block(JobCheckItems.Stock, "Check_Stock_Invalid"));
        }
        else if (roll.ScrapDiameterMm is double scrap && target < scrap - 1e-9)
        {
            double maxMm = Math.Max(input.StartDiameterMm - scrap, 0.0);
            checks.Add(Block(JobCheckItems.Stock, "Check_Stock_BelowScrap", target, scrap, maxMm));
        }
        else
        {
            checks.Add(Pass(JobCheckItems.Stock, "Check_Stock_Ok", input.StockMicrometer / 1000.0, target));
        }

        // 余量分配：差额由粗磨吸收，精工序不变。
        StockAdjustmentResult stock = StockAdjustment.Apply(input.Program.Steps, input.StockMicrometer);
        checks.Add(stock.ProblemResourceKey is { } problem
            ? Block(JobCheckItems.StockSplit, "Check_" + problem)
            : Pass(JobCheckItems.StockSplit, "Check_StockSplit_Ok",
                stock.RoughStockMicrometer / 1000.0, stock.FixedStockMicrometer / 1000.0));

        // 辊形长度：2% 规则。
        double tolerance = machine.Threshold(MachineDescription.LengthTolerancePercentKey) ?? BodyLengthFit.DefaultTolerancePercent;
        BodyFitResult fit = BodyLengthFit.Fit(input.Profile.Profile, input.Profile.BodyLengthMm, roll.Geometry.BodyLengthMm, tolerance);
        checks.Add(fit.Kind switch
        {
            BodyFitKind.Exact => Pass(JobCheckItems.Length, "Check_Length_Exact", fit.BodyLengthMm),
            BodyFitKind.MiddleAdjusted => Pass(JobCheckItems.Length, "Check_Length_Adjusted", fit.DesignLengthMm, fit.BodyLengthMm, fit.DifferencePercent),
            _ => Block(JobCheckItems.Length, "Check_Length_TooDifferent", fit.DesignLengthMm, fit.BodyLengthMm, fit.DifferencePercent, tolerance),
        });

        // 程序适用类型（不符拦）/ 材质（不符提示）。
        RollKind wanted = input.Program.ApplicableRollKind;
        if (wanted != RollKind.Unspecified && roll.Kind != RollKind.Unspecified && wanted != roll.Kind)
        {
            checks.Add(Block(JobCheckItems.RollKind, "Check_RollKind_Mismatch", "RollKind_" + wanted, "RollKind_" + roll.Kind));
        }
        else
        {
            checks.Add(Pass(JobCheckItems.RollKind, "Check_RollKind_Ok"));
        }

        if (!string.IsNullOrWhiteSpace(input.Program.ApplicableMaterial) && !string.IsNullOrWhiteSpace(roll.Material)
            && !string.Equals(input.Program.ApplicableMaterial.Trim(), roll.Material.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Notice(JobCheckItems.Material, "Check_Material_Mismatch", input.Program.ApplicableMaterial, roll.Material));
        }

        CheckWorkpieceSpeed(input, machine, checks);

        // 行程：整根辊身换算机床能磨的长度。
        WorkpieceLimits limits = machine.Workpiece;
        checks.Add(roll.Geometry.BodyLengthMm > limits.MaxBodyLengthMm + 1e-9 || roll.Geometry.BodyLengthMm < limits.MinBodyLengthMm - 1e-9
            ? Block(JobCheckItems.Stroke, "Check_Stroke_OutOfRange", roll.Geometry.BodyLengthMm, limits.MinBodyLengthMm, limits.MaxBodyLengthMm)
            : Pass(JobCheckItems.Stroke, "Check_Stroke_Ok", roll.Geometry.BodyLengthMm));

        // 与计划不同：仅本次必须有原因。
        if (input.Deviation == JobDeviation.ThisTimeOnly && string.IsNullOrWhiteSpace(input.DeviationReason))
        {
            checks.Add(Block(JobCheckItems.Deviation, "Check_Deviation_NoReason"));
        }
        else if (input.Deviation != JobDeviation.None)
        {
            checks.Add(Notice(JobCheckItems.Deviation,
                input.Deviation == JobDeviation.ThisTimeOnly ? "Check_Deviation_ThisTime" : "Check_Deviation_PlanChanged"));
        }
        else if (roll.PlanInferred)
        {
            checks.Add(Notice(JobCheckItems.Deviation, "Check_Deviation_Inferred"));
        }

        // 剩余可磨量：磨后距报废不到两次标准余量时提醒。
        if (roll.ScrapDiameterMm is double scrapMm)
        {
            double remainingMm = target - scrapMm;
            double perGrindMm = (input.Program.StandardStockMicrometer ?? stock.ProgramStockMicrometer) / 1000.0;
            if (remainingMm >= 0.0 && perGrindMm > 0.0 && remainingMm < LowLifeFactor * perGrindMm)
            {
                checks.Add(Notice(JobCheckItems.RemainingLife, "Check_RemainingLife_Low", remainingMm, Math.Floor(remainingMm / perGrindMm)));
            }
            else if (remainingMm >= 0.0)
            {
                checks.Add(Pass(JobCheckItems.RemainingLife, "Check_RemainingLife_Ok", remainingMm,
                    perGrindMm > 0.0 ? Math.Floor(remainingMm / perGrindMm) : 0.0));
            }
        }
        else
        {
            checks.Add(Notice(JobCheckItems.RemainingLife, "Check_RemainingLife_NoScrap"));
        }

        return new JobCheckResult(checks, fit, stock);
    }

    /// <summary>参数校验（工序参数超机床能力等）并进清单：每一条违规一项"不通过"。</summary>
    public static JobCheckResult WithViolations(JobCheckResult result, IEnumerable<ParameterViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(violations);

        List<JobCheck> checks = result.Checks.ToList();
        foreach (ParameterViolation violation in violations)
        {
            checks.Add(Block(JobCheckItems.Parameters, "Check_Parameters_Violation",
                "Param_" + violation.ParameterKey, "Violation_" + violation.Kind, violation.Limit ?? double.NaN));
        }

        return result with { Checks = checks };
    }

    /// <summary>工件线速度（按直径换算）与按辊重限转速。只看有磨削量的工序（磨削时的转速）。</summary>
    private static void CheckWorkpieceSpeed(JobCheckInput input, MachineDescription machine, List<JobCheck> checks)
    {
        double[] rpms = input.Program.Steps
            .Where(step => step.Parameters.TryGet(StepParameterKeys.StockDiameterMicrometer, out _))
            .Select(step => step.Parameters.GetNumberOrDefault(StepParameterKeys.WorkpieceSpeedRpm, double.NaN))
            .Where(double.IsFinite)
            .Where(rpm => rpm > 0.0)
            .ToArray();
        if (rpms.Length == 0)
        {
            return;
        }

        double diameter = input.StartDiameterMm;
        double low = Math.PI * diameter * rpms.Min() / 1000.0;
        double high = Math.PI * diameter * rpms.Max() / 1000.0;
        double? min = machine.Threshold(MachineDescription.MinWorkpieceSurfaceSpeedKey);
        double? max = machine.Threshold(MachineDescription.MaxWorkpieceSurfaceSpeedKey);
        if ((min is double lo && low < lo - 1e-9) || (max is double hi && high > hi + 1e-9))
        {
            checks.Add(Block(JobCheckItems.WorkSpeed, "Check_WorkSpeed_OutOfRange", low, high, min ?? 0.0, max ?? double.PositiveInfinity));
        }
        else
        {
            checks.Add(Pass(JobCheckItems.WorkSpeed, "Check_WorkSpeed_Ok", low, high));
        }

        IReadOnlyList<HeadstockSpeedLimit>? table = machine.HeadstockRpmByWeight;
        if (table is null || table.Count == 0)
        {
            return;
        }

        if (input.Roll.WeightKg is not double weight)
        {
            checks.Add(Notice(JobCheckItems.WeightSpeed, "Check_WeightSpeed_NoWeight"));
            return;
        }

        HeadstockSpeedLimit? limit = table.FirstOrDefault(row => weight <= row.MaxWeightKg + 1e-9);
        if (limit is null)
        {
            checks.Add(Block(JobCheckItems.WeightSpeed, "Check_WeightSpeed_TooHeavy", weight, table[^1].MaxWeightKg));
        }
        else if (rpms.Max() > limit.MaxRpm + 1e-9)
        {
            checks.Add(Block(JobCheckItems.WeightSpeed, "Check_WeightSpeed_TooFast", rpms.Max(), limit.MaxRpm, weight));
        }
        else
        {
            checks.Add(Pass(JobCheckItems.WeightSpeed, "Check_WeightSpeed_Ok", rpms.Max(), limit.MaxRpm));
        }
    }

    private static JobCheck Pass(string item, string key, params object[] args) => new(item, JobCheckStatus.Pass, key, args);

    private static JobCheck Notice(string item, string key, params object[] args) => new(item, JobCheckStatus.Notice, key, args);

    private static JobCheck Block(string item, string key, params object[] args) => new(item, JobCheckStatus.Block, key, args);
}
