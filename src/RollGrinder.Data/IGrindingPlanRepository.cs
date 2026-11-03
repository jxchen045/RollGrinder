using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RollGrinder.Data;

/// <summary>方案从哪里来（设计说明 4.4）。</summary>
public enum PlanSourceKind
{
    /// <summary>空白新建。</summary>
    Blank = 0,

    /// <summary>复制自本辊方案 n（含"改了用过的方案自动另存"）。</summary>
    ThisRollPlan = 1,

    /// <summary>复制自另一支辊的方案。</summary>
    OtherRollPlan = 2,

    /// <summary>复制自标准模板（辊形模板 + 工艺模板）。</summary>
    Template = 3,

    /// <summary>存自作业 J 的调整。</summary>
    Job = 4,

    /// <summary>旧库升级：按台账原计划（库里的辊形与程序）建的方案 1。</summary>
    Legacy = 5,
}

/// <summary>一个磨削方案（归一支辊所有）：一条辊形 + 一支工艺，都是方案自己的那一份。</summary>
/// <param name="PlanId">方案标识。</param>
/// <param name="RollId">所属轧辊。</param>
/// <param name="PlanNo">这支辊内的编号（"方案 3"）。</param>
/// <param name="Name">名称（同一支辊内不重复）。</param>
/// <param name="Purpose">用途 / 机架标签（可选）。</param>
/// <param name="ProfileId">方案的辊形（辊形表里方案自己的那一份）。</param>
/// <param name="ProgramId">方案的工艺（程序表里方案自己的那一份）。</param>
/// <param name="SourceKind">来源。</param>
/// <param name="SourceRef">来源引用：方案号、"辊号/方案号"、模板名、作业号。</param>
/// <param name="EndZoneMm">端部非工作区宽度（mm）；null 取默认（端部圆柱段长度）。</param>
/// <param name="CreatedAtUtc">建立时间。</param>
/// <param name="CreatedBy">建立人。</param>
public sealed record GrindingPlanRecord(
    string PlanId,
    string RollId,
    int PlanNo,
    string Name,
    string? Purpose,
    string ProfileId,
    string ProgramId,
    PlanSourceKind SourceKind,
    string? SourceRef,
    double? EndZoneMm,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy);

/// <summary>方案被作业用了几次、最近一次。</summary>
/// <param name="UseCount">用过几次（0 = 没用过，可以改、可以删）。</param>
/// <param name="LastUsedUtc">最近一次。</param>
public sealed record PlanUsage(int UseCount, DateTimeOffset? LastUsedUtc);

/// <summary>磨削方案的存取。</summary>
public interface IGrindingPlanRepository
{
    /// <summary>一支辊的方案，按方案号。</summary>
    Task<IReadOnlyList<GrindingPlanRecord>> ListByRollAsync(string rollId, CancellationToken cancellationToken);

    /// <summary>取一个方案；不存在为 null。</summary>
    Task<GrindingPlanRecord?> GetAsync(string planId, CancellationToken cancellationToken);

    /// <summary>这支辊下一个方案号（作业记过的号不复用）。</summary>
    Task<int> NextPlanNoAsync(string rollId, CancellationToken cancellationToken);

    /// <summary>新建或整条覆盖。</summary>
    Task SaveAsync(GrindingPlanRecord plan, CancellationToken cancellationToken);

    /// <summary>删掉一个方案（只删方案行；方案自己的辊形 / 工艺由调用方删）。</summary>
    Task DeleteAsync(string planId, CancellationToken cancellationToken);

    /// <summary>一支辊各方案被作业用了几次（作业表 plan_id）。没用过的不在字典里。</summary>
    Task<IReadOnlyDictionary<string, PlanUsage>> UsageAsync(string rollId, CancellationToken cancellationToken);
}
