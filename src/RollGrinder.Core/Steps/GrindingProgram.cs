using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 程序库里的一支程序：一个名字 + 一串工序 + 整支程序的取舍开关。
///
/// 程序是**可复用的模板**，不属于任何一支辊：同一支"热轧工作辊粗磨到精磨"的程序
/// 会被几百支辊用到。作业引用它的时候复制一份快照（<c>GrindingJob.Steps</c>），
/// 所以库里之后改了程序，已经磨过的那支辊的记录不会跟着变。
///
/// 工序记录与作业共用 <see cref="GrindingJobStep"/>——作业里的工序就是从这里复制过去的，
/// 两边用同一个类型才不会在复制时悄悄丢掉什么。
/// </summary>
/// <param name="ProgramId">程序标识。</param>
/// <param name="Name">程序名，现场按这个名字找。</param>
/// <param name="Steps">工序序列，按 Order 升序。</param>
/// <param name="ProgramOptions">程序步骤开关，见 <see cref="ProgramOptionCatalog"/>。</param>
/// <param name="CreatedAtUtc">建立时刻。</param>
/// <param name="ModifiedAtUtc">最后修改时刻，列表按它排序。</param>
public sealed record GrindingProgram(
    string ProgramId,
    string Name,
    IReadOnlyList<GrindingJobStep> Steps,
    ParameterSet ProgramOptions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc)
{
    /// <summary>这支程序有几道工序。</summary>
    public int StepCount => Steps.Count;

    /// <summary>某个程序步骤开关开着没有。没存过这个键时取它的默认值。</summary>
    public bool IsProgramOptionEnabled(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return ProgramOptions.TryGet(key, out ParameterValue? value) && value is { Kind: ParameterValueKind.Boolean }
            ? value.Boolean
            : ProgramOptionCatalog.Get(key).DefaultEnabled;
    }

    /// <summary>建一支程序并做结构性校验（顺序连续、非空）。</summary>
    /// <param name="programOptions">程序步骤开关；传 null 取全套默认值。</param>
    public static GrindingProgram Create(
        string programId,
        string name,
        IEnumerable<GrindingJobStep> steps,
        DateTimeOffset nowUtc,
        ParameterSet? programOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(steps);

        GrindingJobStep[] ordered = steps.OrderBy(step => step.Order).ToArray();
        if (ordered.Length == 0)
        {
            throw new DomainException($"Program '{name}' has no steps.");
        }

        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Order != i + 1)
            {
                throw new DomainException($"Program '{name}' has a gap or duplicate at step order {i + 1}.");
            }
        }

        return new GrindingProgram(
            programId,
            name,
            ordered,

            // 没给就补全默认值：少一个键不该让"这个开关开没开"变成未定义。
            ProgramOptionCatalog.Schema.ApplyDefaults(programOptions ?? ParameterSet.Empty),
            nowUtc,
            nowUtc);
    }
}
