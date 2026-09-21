using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;

namespace RollGrinder.Core.Steps;

/// <summary>作业中的一道工序。</summary>
/// <param name="Order">执行顺序，从 1 开始。</param>
/// <param name="StepTypeKey">工序类型键。</param>
/// <param name="Parameters">该工序的参数（界面量）。</param>
public sealed record GrindingJobStep(int Order, string StepTypeKey, ParameterSet Parameters);

/// <summary>
/// 一支辊的磨削作业：辊件几何 + 目标辊形 + 工序序列。
///
/// 辊形是一条<b>可叠加的多段曲线</b>（<see cref="CompositeRollProfile"/>），不是单一曲线类型：
/// 主辊形铺满全长，端部锥度只作用在两端，倒角再叠一层。
/// 作业里存的是**快照**——从辊形库调出来的那一刻复制一份。
/// 库里的辊形之后改了，已经磨过的这支辊的记录不能跟着变。
/// </summary>
/// <param name="JobId">作业标识。</param>
/// <param name="RollId">辊件标识。</param>
/// <param name="Geometry">辊件几何。</param>
/// <param name="Profile">目标辊形（多段叠加）。</param>
/// <param name="Steps">工序序列，按 Order 升序。同样是**快照**：从程序库调出来时复制一份。</param>
/// <param name="ProgramOptions">程序步骤开关（自动磨削前的取舍），见 <see cref="ProgramOptionCatalog"/>。</param>
public sealed record GrindingJob(
    string JobId,
    string RollId,
    RollGeometry Geometry,
    CompositeRollProfile Profile,
    IReadOnlyList<GrindingJobStep> Steps,
    ParameterSet ProgramOptions)
{
    /// <summary>这支辊形来自辊形库的哪一条；现编现用时为 null。只作追溯，不参与计算。</summary>
    public string? ProfileId { get; init; }

    /// <summary>调出来时那条辊形叫什么。库里改了名也不影响这里——记录要记当时的名字。</summary>
    public string? ProfileName { get; init; }

    /// <summary>这支作业的工序来自程序库的哪一支；现编现用时为 null。只作追溯，不参与计算。</summary>
    public string? ProgramId { get; init; }

    /// <summary>调出来时那支程序叫什么。同上，记录要记当时的名字。</summary>
    public string? ProgramName { get; init; }

    /// <summary>
    /// 主辊形（第一段）的曲线类型键。列表显示与旧库的 <c>job.profile_type_key</c> 列用它，
    /// 下发与补偿一律走合成后的整条 <see cref="Profile"/>。
    /// </summary>
    public string ProfileTypeKey => Profile.Segments[0].ProfileTypeKey;

    /// <summary>某个程序步骤开关开着没有。没存过这个键时取它的默认值。</summary>
    public bool IsProgramOptionEnabled(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return ProgramOptions.TryGet(key, out ParameterValue? value) && value is { Kind: ParameterValueKind.Boolean }
            ? value.Boolean
            : ProgramOptionCatalog.Get(key).DefaultEnabled;
    }

    /// <summary>
    /// 只有一段主辊形的作业。绝大多数平辊、锥辊就是这一种，
    /// 也让"单曲线"这个常见情形不用先包一层 <see cref="CompositeRollProfile"/>。
    /// </summary>
    public static GrindingJob Create(
        string jobId,
        string rollId,
        RollGeometry geometry,
        string profileTypeKey,
        ParameterSet profileParameters,
        IEnumerable<GrindingJobStep> steps,
        ParameterSet? programOptions = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileTypeKey);
        ArgumentNullException.ThrowIfNull(profileParameters);

        return Create(
            jobId,
            rollId,
            geometry,
            CompositeRollProfile.Single(profileTypeKey, geometry, profileParameters),
            steps,
            programOptions);
    }

    /// <summary>构造并做结构性校验（顺序连续、非空）。</summary>
    /// <param name="programOptions">程序步骤开关；传 null 取全套默认值。</param>
    public static GrindingJob Create(
        string jobId,
        string rollId,
        RollGeometry geometry,
        CompositeRollProfile profile,
        IEnumerable<GrindingJobStep> steps,
        ParameterSet? programOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(profile);
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

        return new GrindingJob(
            jobId,
            rollId,
            geometry,
            profile,
            ordered,

            // 没给就补全默认值：少一个键不该让"这个开关开没开"变成未定义。
            ProgramOptionCatalog.Schema.ApplyDefaults(programOptions ?? ParameterSet.Empty));
    }
}
