using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Steps;

/// <summary>作业中的一道工序。</summary>
/// <param name="Order">执行顺序，从 1 开始。</param>
/// <param name="StepTypeKey">工序类型键。</param>
/// <param name="Parameters">该工序的参数（界面量）。</param>
public sealed record GrindingJobStep(int Order, string StepTypeKey, ParameterSet Parameters);

/// <summary>
/// 一支辊的磨削作业：辊件几何 + 目标辊形 + 工序序列。
/// </summary>
/// <param name="JobId">作业标识。</param>
/// <param name="RollId">辊件标识。</param>
/// <param name="Geometry">辊件几何。</param>
/// <param name="ProfileTypeKey">目标辊形类型键。</param>
/// <param name="ProfileParameters">辊形参数（界面量）。</param>
/// <param name="Steps">工序序列，按 Order 升序。</param>
public sealed record GrindingJob(
    string JobId,
    string RollId,
    RollGeometry Geometry,
    string ProfileTypeKey,
    ParameterSet ProfileParameters,
    IReadOnlyList<GrindingJobStep> Steps)
{
    /// <summary>构造并做结构性校验（顺序连续、非空）。</summary>
    public static GrindingJob Create(
        string jobId,
        string rollId,
        RollGeometry geometry,
        string profileTypeKey,
        ParameterSet profileParameters,
        IEnumerable<GrindingJobStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileTypeKey);
        ArgumentNullException.ThrowIfNull(profileParameters);
        ArgumentNullException.ThrowIfNull(steps);

        GrindingJobStep[] ordered = steps.OrderBy(step => step.Order).ToArray();
        if (ordered.Length == 0)
        {
            throw new DomainException($"Job '{jobId}' has no steps.");
        }

        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Order != i + 1)
            {
                throw new DomainException($"Job '{jobId}' has a gap or duplicate at step order {i + 1}.");
            }
        }

        return new GrindingJob(jobId, rollId, geometry, profileTypeKey, profileParameters, ordered);
    }
}
