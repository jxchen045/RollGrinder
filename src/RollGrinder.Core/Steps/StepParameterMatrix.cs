using System;
using System.Collections.Generic;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Steps;

/// <summary>矩阵里的一格：某道工序在某个参数上的取值。</summary>
/// <param name="StepOrder">工序序号，从 1 起。</param>
/// <param name="Value">取值；这道工序的类型没有这个参数时为 null（界面上留空）。</param>
public sealed record StepMatrixCell(int StepOrder, ParameterValue? Value)
{
    /// <summary>这道工序有没有这个参数。</summary>
    public bool IsApplicable => Value is not null;
}

/// <summary>矩阵里的一行：一个参数横着看过去。</summary>
/// <param name="Descriptor">参数描述（名字、单位、上下限都在里面）。</param>
/// <param name="Cells">按工序顺序排列的格子，长度等于工序数。</param>
public sealed record StepMatrixRow(ParameterDescriptor Descriptor, IReadOnlyList<StepMatrixCell> Cells);

/// <summary>
/// 把一支作业摊成「行 = 参数，列 = 工序」的矩阵。
///
/// 自动磨削时操作工要的是**一屏看完**：哪一道在跑、下一道是什么、
/// 各道的拖板速度是怎么一路降下来的。一次只显示一道工序的参数格，
/// 这些都得靠翻页在脑子里拼。
///
/// 编程时相反——一次专心改一道，所以工序编程页仍然是单工序视图。
///
/// 这里是纯投影：不认识界面，也不改任何东西，进来什么样出去还是什么样。
/// </summary>
public sealed record StepParameterMatrix(
    IReadOnlyList<GrindingJobStep> Steps,
    IReadOnlyList<StepMatrixRow> Rows)
{
    /// <summary>空矩阵：还没装载作业时用。</summary>
    public static StepParameterMatrix Empty { get; } =
        new(Array.Empty<GrindingJobStep>(), Array.Empty<StepMatrixRow>());

    /// <summary>
    /// 按作业的工序序列建矩阵。
    ///
    /// 行的顺序按**工序里第一次出现**排：开始工序没有参数，所以第一道纵磨工序的
    /// schema 顺序就成了主序（砂轮线速度、头架转速、拖板速度、两路进给、道次……），
    /// 后面的工序带来的新参数依次接在后面。这样一支正常的程序排出来，
    /// 行的顺序与工艺人员看参数的顺序是一致的。
    /// </summary>
    public static StepParameterMatrix Build(GrindingJob job, GrindingStepTypeRegistry stepTypes)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(stepTypes);

        var order = new List<ParameterDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valuesByStep = new List<Dictionary<string, ParameterValue>>(job.Steps.Count);

        foreach (GrindingJobStep step in job.Steps)
        {
            IGrindingStepType stepType = stepTypes.Get(step.StepTypeKey);
            ParameterSet values = stepType.Schema.ApplyDefaults(step.Parameters);
            var byKey = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);

            foreach (ParameterDescriptor descriptor in stepType.Schema.Descriptors)
            {
                if (seen.Add(descriptor.Key))
                {
                    order.Add(descriptor);
                }

                byKey[descriptor.Key] = values.Get(descriptor.Key);
            }

            valuesByStep.Add(byKey);
        }

        var rows = new List<StepMatrixRow>(order.Count);
        foreach (ParameterDescriptor descriptor in order)
        {
            var cells = new List<StepMatrixCell>(job.Steps.Count);
            for (int i = 0; i < job.Steps.Count; i++)
            {
                cells.Add(new StepMatrixCell(
                    job.Steps[i].Order,
                    valuesByStep[i].TryGetValue(descriptor.Key, out ParameterValue? value) ? value : null));
            }

            rows.Add(new StepMatrixRow(descriptor, cells));
        }

        return new StepParameterMatrix(job.Steps, rows);
    }

    /// <summary>某个参数那一行；没有这个参数时为 null。</summary>
    public StepMatrixRow? RowOf(string parameterKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameterKey);

        foreach (StepMatrixRow row in Rows)
        {
            if (string.Equals(row.Descriptor.Key, parameterKey, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }
}
