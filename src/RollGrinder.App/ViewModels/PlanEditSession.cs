using System;
using RollGrinder.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Services.Jobs;

namespace RollGrinder.App.ViewModels;

/// <summary>一次方案保存要带上的改动，各页往里填自己那一份。</summary>
public sealed class PlanEditBuilder
{
    public RollProfileDefinition? Profile { get; set; }

    public GrindingProgram? Program { get; set; }

    public bool EndZoneChanged { get; set; }

    public double? EndZoneMm { get; set; }

    /// <summary>改过的可调范围（整份）；没改为 null。</summary>
    public IReadOnlyList<PlanParamRange>? Ranges { get; set; }

    public PlanEdit Build() => new(Profile, Program, EndZoneChanged, EndZoneMm) { Ranges = Ranges };
}

/// <summary>按方案编辑的一页（辊形页、工艺页）在共用草稿里的那一份。</summary>
public interface IPlanDraftPart
{
    /// <summary>正在编辑的方案；按模板编辑时为 null。</summary>
    string? PlanId { get; }

    /// <summary>这一份改了还没存。</summary>
    bool IsDirty { get; }

    /// <summary>这一份现在存不了的原因（资源键）；能存为 null。</summary>
    string? DraftProblem { get; }

    /// <summary>"辊形" / "工艺"（资源键），确认时写"改了什么"。</summary>
    string PartLabelKey { get; }

    /// <summary>把改过的内容填进去。</summary>
    void Contribute(PlanEditBuilder builder);

    /// <summary>存好以后按（可能是另存出来的）方案重读，草稿清空。</summary>
    Task ReloadPlanAsync(string planId, CancellationToken cancellationToken);

    /// <summary>放弃这一份的改动。</summary>
    void DiscardPlanDraft();
}

/// <summary>
/// 辊形与工艺共用一份草稿、一次保存（设计稿 v4 P2）：两页各管各的编辑，保存时从哪一页按都把两页对同一方案的改动一起存，
/// 用过的方案只另存出一个新方案；在两页之间切换草稿跟着走，离开方案区才问"保存 / 放弃"。
/// </summary>
public sealed class PlanEditSession
{
    private readonly IGrindingPlanService plans;
    private readonly List<IPlanDraftPart> parts = new();

    public PlanEditSession(IGrindingPlanService plans)
    {
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
    }

    public IGrindingPlanService Plans => this.plans;

    public void Register(IPlanDraftPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (!this.parts.Contains(part))
        {
            this.parts.Add(part);
        }
    }

    /// <summary>对这个方案改了还没存的那几页。</summary>
    public IReadOnlyList<IPlanDraftPart> DirtyParts(string planId) =>
        this.parts.Where(part => part.PlanId == planId && part.IsDirty).ToArray();

    public bool HasUnsavedWork(string planId) => DirtyParts(planId).Count > 0;

    /// <summary>第一个存不了的原因；都能存为 null。</summary>
    public string? Problem(string planId) => DirtyParts(planId).Select(part => part.DraftProblem).FirstOrDefault(problem => problem is not null);

    /// <summary>放弃这个方案的全部改动（两页一起）。</summary>
    public void DiscardAll(string planId)
    {
        foreach (IPlanDraftPart part in DirtyParts(planId))
        {
            part.DiscardPlanDraft();
        }
    }

    /// <summary>把两页的改动一起存一次；存好后两页按存进去的方案重读。</summary>
    public async Task<PlanSaveOutcome> SaveAsync(string planId, bool setCurrentIfNew, string user, CancellationToken cancellationToken)
    {
        var builder = new PlanEditBuilder();
        foreach (IPlanDraftPart part in DirtyParts(planId))
        {
            part.Contribute(builder);
        }

        PlanSaveOutcome outcome = await this.plans.SavePlanAsync(planId, builder.Build(), setCurrentIfNew, user, cancellationToken).ConfigureAwait(true);
        foreach (IPlanDraftPart part in this.parts.Where(part => part.PlanId == planId).ToArray())
        {
            await part.ReloadPlanAsync(outcome.Plan.PlanId, cancellationToken).ConfigureAwait(true);
        }

        return outcome;
    }
}
