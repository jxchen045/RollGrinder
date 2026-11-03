using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Jobs;

/// <summary>方案列表里的一行。</summary>
/// <param name="Plan">方案。</param>
/// <param name="IsCurrent">是这支辊的当前方案（★）。</param>
/// <param name="Usage">用了几次、最近一次。</param>
/// <param name="ProfileName">辊形名。</param>
/// <param name="ProgramName">工艺名。</param>
/// <param name="NominalLengthMm">辊形的名义长度。</param>
public sealed record PlanOverview(
    GrindingPlanRecord Plan,
    bool IsCurrent,
    PlanUsage Usage,
    string ProfileName,
    string ProgramName,
    double NominalLengthMm)
{
    /// <summary>被作业用过：内容冻结，改就另存为新方案。</summary>
    public bool IsUsed => Usage.UseCount > 0;
}

/// <summary>方案连同它自己的辊形与工艺。</summary>
public sealed record PlanContent(GrindingPlanRecord Plan, RollProfileDefinition Profile, GrindingProgram Program);

/// <summary>保存方案（辊形或工艺）的结果。</summary>
/// <param name="Plan">存进去的方案：没用过的是原方案；用过的是另存出来的新方案。</param>
/// <param name="SavedAsNew">原方案已用于磨削，内容不改，另存为了 <see cref="Plan"/>。</param>
/// <param name="OriginalPlanNo">原方案号。</param>
/// <param name="IsCurrent">保存后它是不是当前方案。</param>
public sealed record PlanSaveOutcome(GrindingPlanRecord Plan, bool SavedAsNew, int OriginalPlanNo, bool IsCurrent);

/// <summary>
/// 一次保存方案的改动（设计稿 v4 P2：辊形与工艺共用一份草稿、一次保存）。没改的那一样为 null。
/// </summary>
/// <param name="Profile">改过的辊形；没改为 null。</param>
/// <param name="Program">改过的工艺；没改为 null。</param>
/// <param name="EndZoneChanged">端部非工作区宽度改了。</param>
/// <param name="EndZoneMm">新的端部非工作区宽度（mm）；null 取默认（端部圆柱段长度）。</param>
public sealed record PlanEdit(RollProfileDefinition? Profile, GrindingProgram? Program, bool EndZoneChanged = false, double? EndZoneMm = null)
{
    /// <summary>什么都没改。</summary>
    public bool IsEmpty => Profile is null && Program is null && !EndZoneChanged;
}

/// <summary>按模板建方案的结果。</summary>
/// <param name="Plan">建出来的方案；建不了为 null。</param>
/// <param name="ProblemKey">建不了的原因（资源键）。</param>
/// <param name="Alignment">模板名义长度对到本辊名义长度的结论。</param>
public sealed record PlanCreateOutcome(GrindingPlanRecord? Plan, string? ProblemKey, LengthAlignment? Alignment);

/// <summary>
/// 磨削方案（设计说明《轧辊与磨削方案》第 4 节）：方案归辊所有 = 辊形 + 工艺；一支辊一个当前方案；
/// 被作业用过即冻结——改就另存为新方案（新编号，来源记为原方案），不设版本链。
/// 模板（辊形库 / 程序库里的条目）只是复制的起点，不被轧辊引用。
/// </summary>
public interface IGrindingPlanService
{
    /// <summary>一支辊的方案（旧库的辊第一次用到时按原计划建方案 1）。当前在前，其余按方案号倒序。</summary>
    Task<IReadOnlyList<PlanOverview>> ListAsync(string rollId, CancellationToken cancellationToken);

    /// <summary>当前方案（同上，必要时先建方案 1）；没有为 null。</summary>
    Task<GrindingPlanRecord?> CurrentAsync(string rollId, CancellationToken cancellationToken);

    /// <summary>取方案与它的辊形、工艺；缺任何一样为 null。</summary>
    Task<PlanContent?> GetContentAsync(string planId, CancellationToken cancellationToken);

    /// <summary>方案用了几次。</summary>
    Task<PlanUsage> UsageAsync(string planId, CancellationToken cancellationToken);

    /// <summary>
    /// 从模板（一条辊形 + 一支程序）复制出一个新方案。辊形的名义长度按本辊名义长度以中心为基准对齐（截取 / 延伸，不伸缩），
    /// 对不上（超出容差）不建。只给一样模板时，另一样取当前方案的（没有当前方案就建不了）。
    /// </summary>
    Task<PlanCreateOutcome> CreateFromTemplatesAsync(
        string rollId, string? profileTemplateId, string? programTemplateId, bool setCurrent, string user, CancellationToken cancellationToken);

    /// <summary>只看能不能按模板建（不落库）：返回原因资源键，能建为 null。</summary>
    Task<string?> CheckTemplatesAsync(string rollId, string? profileTemplateId, string? programTemplateId, CancellationToken cancellationToken);

    /// <summary>复制本辊的一个方案为新方案（新编号，来源记为原方案）。</summary>
    Task<GrindingPlanRecord> CopyAsync(string planId, string? name, bool setCurrent, string user, CancellationToken cancellationToken);

    /// <summary>保存方案的辊形：没用过的就地改；用过的另存为新方案（工艺一起复制）。</summary>
    Task<PlanSaveOutcome> SaveProfileAsync(
        string planId, RollProfileDefinition profile, bool setCurrentIfNew, string user, CancellationToken cancellationToken);

    /// <summary>保存方案的工艺：同上（辊形一起复制）。</summary>
    Task<PlanSaveOutcome> SaveProgramAsync(
        string planId, GrindingProgram program, bool setCurrentIfNew, string user, CancellationToken cancellationToken);

    /// <summary>
    /// 一次保存辊形、工艺、端部非工作区的改动：没用过的就地改；用过的另存为一个新方案（没改的那样照原方案复制），
    /// <paramref name="setCurrentIfNew"/> 决定新方案是否设为当前。
    /// </summary>
    Task<PlanSaveOutcome> SavePlanAsync(
        string planId, PlanEdit edit, bool setCurrentIfNew, string user, CancellationToken cancellationToken);

    /// <summary>设为当前方案（记一条改动）。</summary>
    Task SetCurrentAsync(string planId, string? reason, string user, CancellationToken cancellationToken);

    /// <summary>改名 / 用途。名字在同一支辊内不能重复：重复返回 false。</summary>
    Task<bool> RenameAsync(string planId, string name, string? purpose, string user, CancellationToken cancellationToken);

    /// <summary>删除：只删没用过、不是当前的方案；删不了返回原因资源键，删了返回 null。</summary>
    Task<string?> DeleteAsync(string planId, string user, CancellationToken cancellationToken);

    /// <summary>这支辊里名字被别的方案占了。</summary>
    Task<bool> NameTakenAsync(string rollId, string name, string? exceptPlanId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGrindingPlanService"/>
public sealed class GrindingPlanService : IGrindingPlanService
{
    private readonly IGrindingPlanRepository plans;
    private readonly IRollRepository rolls;
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IChangeLogRepository changeLog;
    private readonly RollProfileTypeRegistry profileTypes;
    private readonly MachineDescription machine;
    private readonly HmiSettings settings;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);

    public GrindingPlanService(
        IGrindingPlanRepository plans,
        IRollRepository rolls,
        IRollProfileRepository profiles,
        IProgramRepository programs,
        IChangeLogRepository changeLog,
        RollProfileTypeRegistry profileTypes,
        MachineDescription machine,
        HmiSettings settings,
        TimeProvider timeProvider)
    {
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.changeLog = changeLog ?? throw new ArgumentNullException(nameof(changeLog));
        this.profileTypes = profileTypes ?? throw new ArgumentNullException(nameof(profileTypes));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>下发容差（mm）：名义长度 × 百分比，配了绝对值取小的。</summary>
    public static double DownloadToleranceMm(MachineDescription machine, double nominalLengthMm)
    {
        ArgumentNullException.ThrowIfNull(machine);
        double percent = machine.Threshold(MachineDescription.LengthTolerancePercentKey) ?? BodyLengthFit.DefaultTolerancePercent;
        double tolerance = Math.Abs(nominalLengthMm) * percent / 100.0;
        return machine.Threshold(MachineDescription.LengthToleranceMmKey) is double cap && cap >= 0.0 ? Math.Min(tolerance, cap) : tolerance;
    }

    /// <summary>方案辊形对到这一次辊身长度（有实测用实测）上。</summary>
    public static LengthAlignment AlignForJob(
        RollProfileDefinition profile, double bodyLengthMm, double? endZoneMm, MachineDescription machine, RollProfileTypeRegistry registry, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ProfileLengthAlignment.Align(
            profile.Profile, profile.BodyLengthMm, bodyLengthMm, DownloadToleranceMm(machine, profile.BodyLengthMm), endZoneMm, registry, sampleCount);
    }

    public async Task<IReadOnlyList<PlanOverview>> ListAsync(string rollId, CancellationToken cancellationToken)
    {
        RollRecord? roll = await EnsurePlansAsync(rollId, cancellationToken).ConfigureAwait(false);
        if (roll is null)
        {
            return Array.Empty<PlanOverview>();
        }

        IReadOnlyList<GrindingPlanRecord> list = await this.plans.ListByRollAsync(rollId, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, PlanUsage> usage = await this.plans.UsageAsync(rollId, cancellationToken).ConfigureAwait(false);
        var rows = new List<PlanOverview>(list.Count);
        foreach (GrindingPlanRecord plan in list)
        {
            RollProfileDefinition? profile = await this.profiles.GetAsync(plan.ProfileId, cancellationToken).ConfigureAwait(false);
            GrindingProgram? program = await this.programs.GetAsync(plan.ProgramId, cancellationToken).ConfigureAwait(false);
            rows.Add(new PlanOverview(
                plan,
                plan.PlanId == roll.CurrentPlanId,
                usage.TryGetValue(plan.PlanId, out PlanUsage? used) ? used : new PlanUsage(0, null),
                profile?.Name ?? "--",
                program?.Name ?? "--",
                profile?.BodyLengthMm ?? roll.Geometry.BodyLengthMm));
        }

        return rows.OrderByDescending(row => row.IsCurrent).ThenByDescending(row => row.Plan.PlanNo).ToArray();
    }

    public async Task<GrindingPlanRecord?> CurrentAsync(string rollId, CancellationToken cancellationToken)
    {
        RollRecord? roll = await EnsurePlansAsync(rollId, cancellationToken).ConfigureAwait(false);
        return roll?.CurrentPlanId is { } id ? await this.plans.GetAsync(id, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task<PlanContent?> GetContentAsync(string planId, CancellationToken cancellationToken)
    {
        if (await this.plans.GetAsync(planId, cancellationToken).ConfigureAwait(false) is not { } plan)
        {
            return null;
        }

        RollProfileDefinition? profile = await this.profiles.GetAsync(plan.ProfileId, cancellationToken).ConfigureAwait(false);
        GrindingProgram? program = await this.programs.GetAsync(plan.ProgramId, cancellationToken).ConfigureAwait(false);
        return profile is null || program is null ? null : new PlanContent(plan, profile, program);
    }

    public async Task<PlanUsage> UsageAsync(string planId, CancellationToken cancellationToken)
    {
        if (await this.plans.GetAsync(planId, cancellationToken).ConfigureAwait(false) is not { } plan)
        {
            return new PlanUsage(0, null);
        }

        IReadOnlyDictionary<string, PlanUsage> usage = await this.plans.UsageAsync(plan.RollId, cancellationToken).ConfigureAwait(false);
        return usage.TryGetValue(planId, out PlanUsage? used) ? used : new PlanUsage(0, null);
    }

    public async Task<PlanCreateOutcome> CreateFromTemplatesAsync(
        string rollId, string? profileTemplateId, string? programTemplateId, bool setCurrent, string user, CancellationToken cancellationToken)
    {
        (RollRecord? roll, RollProfileDefinition? profile, GrindingProgram? program, string? sourceRef, string? problem, LengthAlignment? alignment) =
            await ResolveTemplatesAsync(rollId, profileTemplateId, programTemplateId, cancellationToken).ConfigureAwait(false);
        if (problem is not null || roll is null || profile is null || program is null)
        {
            return new PlanCreateOutcome(null, problem ?? "PlanProblem_Missing", alignment);
        }

        GrindingPlanRecord plan = await CreateAsync(
            roll, profile, program, PlanName(profile.Name, program.Name), roll.Purpose,
            PlanSourceKind.Template, sourceRef, null, setCurrent, user, cancellationToken).ConfigureAwait(false);
        return new PlanCreateOutcome(plan, null, alignment);
    }

    public async Task<string?> CheckTemplatesAsync(
        string rollId, string? profileTemplateId, string? programTemplateId, CancellationToken cancellationToken) =>
        (await ResolveTemplatesAsync(rollId, profileTemplateId, programTemplateId, cancellationToken).ConfigureAwait(false)).Problem;

    private async Task<(RollRecord? Roll, RollProfileDefinition? Profile, GrindingProgram? Program, string? SourceRef, string? Problem, LengthAlignment? Alignment)>
        ResolveTemplatesAsync(string rollId, string? profileTemplateId, string? programTemplateId, CancellationToken cancellationToken)
    {
        RollRecord? roll = await EnsurePlansAsync(rollId, cancellationToken).ConfigureAwait(false);
        if (roll is null)
        {
            return (null, null, null, null, "PlanProblem_Missing", null);
        }

        if (roll.Retired)
        {
            return (roll, null, null, null, "PlanProblem_Retired", null);
        }

        PlanContent? current = roll.CurrentPlanId is { } currentId
            ? await GetContentAsync(currentId, cancellationToken).ConfigureAwait(false)
            : null;
        RollProfileDefinition? template = profileTemplateId is null
            ? null
            : await this.profiles.GetAsync(profileTemplateId, cancellationToken).ConfigureAwait(false);
        GrindingProgram? programTemplate = programTemplateId is null
            ? null
            : await this.programs.GetAsync(programTemplateId, cancellationToken).ConfigureAwait(false);
        if ((profileTemplateId is not null && template is null) || (programTemplateId is not null && programTemplate is null))
        {
            return (roll, null, null, null, "PlanProblem_Missing", null);
        }

        if (template is { Disabled: true } || programTemplate is { Disabled: true })
        {
            return (roll, null, null, null, "PlanProblem_Disabled", null);
        }

        GrindingProgram? program = programTemplate ?? current?.Program;
        if (program is not null && program.ApplicableRollKind != RollKind.Unspecified
            && roll.Kind != RollKind.Unspecified && program.ApplicableRollKind != roll.Kind)
        {
            return (roll, null, null, null, "PlanProblem_Kind", null);
        }

        RollProfileDefinition? profile = current?.Profile;
        LengthAlignment? alignment = null;
        if (template is not null)
        {
            // 模板按名义长度画；本辊名义长度不同时以中心为基准截取 / 延伸（与下发同一条规则），超出容差不建。
            alignment = ProfileLengthAlignment.Align(
                template.Profile, template.BodyLengthMm, roll.Geometry.BodyLengthMm,
                DownloadToleranceMm(this.machine, template.BodyLengthMm), null, this.profileTypes, this.settings.ProfileSampleCount);
            if (alignment.Profile is not { } aligned)
            {
                return (roll, null, null, null, "PlanProblem_Length", alignment);
            }

            profile = template with { BodyLengthMm = roll.Geometry.BodyLengthMm, Profile = aligned };
        }

        if (profile is null || program is null)
        {
            return (roll, null, null, null, "PlanProblem_Missing", alignment);
        }

        string sourceRef = (template?.Name ?? current!.Plan.Name) + " · " + (programTemplate?.Name ?? current!.Plan.Name);
        return (roll, profile, program, sourceRef, null, alignment);
    }

    public async Task<GrindingPlanRecord> CopyAsync(string planId, string? name, bool setCurrent, string user, CancellationToken cancellationToken)
    {
        PlanContent content = await GetContentAsync(planId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException($"Plan '{planId}' is missing or incomplete.");
        RollRecord roll = await this.rolls.GetAsync(content.Plan.RollId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException($"Roll '{content.Plan.RollId}' does not exist.");
        return await CreateAsync(
            roll, content.Profile, content.Program, string.IsNullOrWhiteSpace(name) ? content.Plan.Name : name.Trim(), content.Plan.Purpose,
            PlanSourceKind.ThisRollPlan, content.Plan.PlanNo.ToString(CultureInfo.InvariantCulture), content.Plan.EndZoneMm,
            setCurrent, user, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlanSaveOutcome> SaveProfileAsync(
        string planId, RollProfileDefinition profile, bool setCurrentIfNew, string user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return await SavePlanAsync(planId, new PlanEdit(profile, null), setCurrentIfNew, user, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlanSaveOutcome> SaveProgramAsync(
        string planId, GrindingProgram program, bool setCurrentIfNew, string user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(program);
        return await SavePlanAsync(planId, new PlanEdit(null, program), setCurrentIfNew, user, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCurrentAsync(string planId, string? reason, string user, CancellationToken cancellationToken)
    {
        GrindingPlanRecord plan = await this.plans.GetAsync(planId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException($"Plan '{planId}' does not exist.");
        RollRecord roll = await this.rolls.GetAsync(plan.RollId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException($"Roll '{plan.RollId}' does not exist.");
        await MakeCurrentAsync(roll, plan, reason, user, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RenameAsync(string planId, string name, string? purpose, string user, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        GrindingPlanRecord plan = await this.plans.GetAsync(planId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException($"Plan '{planId}' does not exist.");
        if (await NameTakenAsync(plan.RollId, name, planId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        GrindingPlanRecord renamed = plan with { Name = name.Trim(), Purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim() };
        await this.plans.SaveAsync(renamed, cancellationToken).ConfigureAwait(false);
        if (renamed.Name != plan.Name)
        {
            await LogAsync(plan.RollId, Label(plan), plan.Name, renamed.Name, user, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<string?> DeleteAsync(string planId, string user, CancellationToken cancellationToken)
    {
        if (await this.plans.GetAsync(planId, cancellationToken).ConfigureAwait(false) is not { } plan)
        {
            return null;
        }

        RollRecord? roll = await this.rolls.GetAsync(plan.RollId, cancellationToken).ConfigureAwait(false);
        if (roll?.CurrentPlanId == planId)
        {
            return "Plan_DeleteCurrent";
        }

        if ((await UsageAsync(planId, cancellationToken).ConfigureAwait(false)).UseCount > 0)
        {
            return "Plan_DeleteUsed";
        }

        await this.plans.DeleteAsync(planId, cancellationToken).ConfigureAwait(false);
        await this.profiles.DeleteAsync(plan.ProfileId, cancellationToken).ConfigureAwait(false);
        await this.programs.DeleteAsync(plan.ProgramId, cancellationToken).ConfigureAwait(false);
        await LogAsync(plan.RollId, Label(plan), plan.Name, null, user, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<bool> NameTakenAsync(string rollId, string name, string? exceptPlanId, CancellationToken cancellationToken)
    {
        string wanted = (name ?? string.Empty).Trim();
        return (await this.plans.ListByRollAsync(rollId, cancellationToken).ConfigureAwait(false))
            .Any(plan => plan.PlanId != exceptPlanId && string.Equals(plan.Name.Trim(), wanted, StringComparison.CurrentCultureIgnoreCase));
    }

    // ───────────── 内部 ─────────────

    public async Task<PlanSaveOutcome> SavePlanAsync(
        string planId, PlanEdit edit, bool setCurrentIfNew, string user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        RollProfileDefinition? profile = edit.Profile;
        GrindingProgram? program = edit.Program;
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PlanContent content = await GetContentAsync(planId, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainException($"Plan '{planId}' is missing or incomplete.");
            RollRecord roll = await this.rolls.GetAsync(content.Plan.RollId, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainException($"Roll '{content.Plan.RollId}' does not exist.");
            bool isCurrent = roll.CurrentPlanId == planId;
            DateTimeOffset now = this.timeProvider.GetUtcNow();
            if (edit.EndZoneMm is double zone && (zone < 0.0 || !double.IsFinite(zone)))
            {
                throw new DomainException("The end zone width must be zero or positive.");
            }

            if (edit.IsEmpty)
            {
                return new PlanSaveOutcome(content.Plan, false, content.Plan.PlanNo, isCurrent);
            }

            double? endZone = edit.EndZoneChanged ? edit.EndZoneMm : content.Plan.EndZoneMm;

            if ((await UsageAsync(planId, cancellationToken).ConfigureAwait(false)).UseCount == 0)
            {
                // 没用过：就地改，方案号不变。
                if (profile is not null)
                {
                    await this.profiles.SaveAsync(
                        profile with { ProfileId = content.Profile.ProfileId, CreatedAtUtc = content.Profile.CreatedAtUtc, ModifiedAtUtc = now, Version = 1, ReferenceRollId = roll.RollId },
                        cancellationToken).ConfigureAwait(false);
                }

                if (program is not null)
                {
                    await this.programs.SaveAsync(
                        program with { ProgramId = content.Program.ProgramId, CreatedAtUtc = content.Program.CreatedAtUtc, ModifiedAtUtc = now, Version = 1 },
                        cancellationToken).ConfigureAwait(false);
                }

                GrindingPlanRecord kept = content.Plan;
                if (edit.EndZoneChanged && endZone != content.Plan.EndZoneMm)
                {
                    kept = content.Plan with { EndZoneMm = endZone };
                    await this.plans.SaveAsync(kept, cancellationToken).ConfigureAwait(false);
                }

                string what = string.Join(
                    "+",
                    new[] { profile is not null ? "profile" : null, program is not null ? "program" : null, edit.EndZoneChanged ? "endZone" : null }
                        .Where(part => part is not null));
                await LogAsync(roll.RollId, Label(content.Plan), null, what, user, cancellationToken).ConfigureAwait(false);
                return new PlanSaveOutcome(kept, false, content.Plan.PlanNo, isCurrent);
            }

            // 用过即冻结：原方案内容不改，另存为新方案（辊形与工艺一起），来源记为原方案。
            GrindingPlanRecord created = await CreateAsync(
                roll,
                profile ?? content.Profile,
                program ?? content.Program,
                content.Plan.Name,
                content.Plan.Purpose,
                PlanSourceKind.ThisRollPlan,
                content.Plan.PlanNo.ToString(CultureInfo.InvariantCulture),
                endZone,
                setCurrentIfNew,
                user,
                cancellationToken,
                uniqueName: true).ConfigureAwait(false);
            return new PlanSaveOutcome(created, true, content.Plan.PlanNo, setCurrentIfNew);
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>建一个方案：辊形、工艺各复制一份（新标识），方案号取下一个。</summary>
    private async Task<GrindingPlanRecord> CreateAsync(
        RollRecord roll,
        RollProfileDefinition profile,
        GrindingProgram program,
        string name,
        string? purpose,
        PlanSourceKind sourceKind,
        string? sourceRef,
        double? endZoneMm,
        bool setCurrent,
        string user,
        CancellationToken cancellationToken,
        bool uniqueName = true)
    {
        DateTimeOffset now = this.timeProvider.GetUtcNow();
        int planNo = await this.plans.NextPlanNoAsync(roll.RollId, cancellationToken).ConfigureAwait(false);
        string stem = string.Create(CultureInfo.InvariantCulture, $"{roll.RollId}#{planNo}#{now:yyyyMMddHHmmssfff}");
        string planName = uniqueName ? await UniqueNameAsync(roll.RollId, name, planNo, cancellationToken).ConfigureAwait(false) : name;

        RollProfileDefinition ownProfile = profile with
        {
            ProfileId = "PP-" + stem,
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
            Version = 1,
            Disabled = false,
            ReferenceRollId = roll.RollId,
        };
        GrindingProgram ownProgram = program with
        {
            ProgramId = "PG-" + stem,
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
            Version = 1,
            Disabled = false,
        };
        var plan = new GrindingPlanRecord(
            "PL-" + stem, roll.RollId, planNo, planName, purpose, ownProfile.ProfileId, ownProgram.ProgramId,
            sourceKind, sourceRef, endZoneMm, now, user ?? string.Empty);

        // 先写方案行：辊形 / 工艺一落库就被它认领，不会在模板列表里闪一下。
        await this.plans.SaveAsync(plan, cancellationToken).ConfigureAwait(false);
        await this.profiles.SaveAsync(ownProfile, cancellationToken).ConfigureAwait(false);
        await this.programs.SaveAsync(ownProgram, cancellationToken).ConfigureAwait(false);
        await LogAsync(roll.RollId, Label(plan), null, plan.Name, user ?? string.Empty, cancellationToken).ConfigureAwait(false);

        if (setCurrent)
        {
            await MakeCurrentAsync(roll, plan, null, user ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        return plan;
    }

    private async Task MakeCurrentAsync(RollRecord roll, GrindingPlanRecord plan, string? reason, string user, CancellationToken cancellationToken)
    {
        RollRecord fresh = await this.rolls.GetAsync(roll.RollId, cancellationToken).ConfigureAwait(false) ?? roll;
        if (fresh.CurrentPlanId == plan.PlanId && !fresh.PlanInferred)
        {
            return;
        }

        string? old = fresh.CurrentPlanId is { } oldId && await this.plans.GetAsync(oldId, cancellationToken).ConfigureAwait(false) is { } previous
            ? Label(previous)
            : null;

        // 台账的目标辊形 / 程序跟着当前方案走（方案自己的那一份），老的读路径照常可用。
        await this.rolls.UpsertAsync(
            fresh with { CurrentPlanId = plan.PlanId, TargetProfileId = plan.ProfileId, ProgramId = plan.ProgramId, PlanInferred = false },
            cancellationToken).ConfigureAwait(false);
        await this.changeLog.AddAsync(
            new[]
            {
                new ChangeLogEntry(
                    Guid.NewGuid().ToString("N"), this.timeProvider.GetUtcNow(), user, ChangeLogAreas.RollPlan,
                    fresh.RollId + " · current" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : " · " + reason),
                    old, Label(plan)),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 台账计划指的是库里的条目（旧库升级、登记新辊、导入时选的模板），还没有方案：第一次用到时按它建方案 1、设为当前；
    /// "推断来的计划"标记跟着保留，核对页照样标黄。
    /// </summary>
    private async Task<RollRecord?> EnsurePlansAsync(string rollId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);
        RollRecord? roll = await this.rolls.GetAsync(rollId, cancellationToken).ConfigureAwait(false);
        if (roll is null || roll.CurrentPlanId is not null || roll.TargetProfileId is null || roll.ProgramId is null)
        {
            return roll;
        }

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            roll = await this.rolls.GetAsync(rollId, cancellationToken).ConfigureAwait(false);
            if (roll is null || roll.CurrentPlanId is not null || roll.TargetProfileId is null || roll.ProgramId is null)
            {
                return roll;
            }

            RollProfileDefinition? profile = await this.profiles.GetAsync(roll.TargetProfileId, cancellationToken).ConfigureAwait(false);
            GrindingProgram? program = await this.programs.GetAsync(roll.ProgramId, cancellationToken).ConfigureAwait(false);
            if (profile is null || program is null)
            {
                return roll;
            }

            // 名义长度能对上就按本辊名义长度存（截取 / 延伸，不伸缩）；对不上原样存，留给作业核对去拦。
            LengthAlignment alignment = ProfileLengthAlignment.Align(
                profile.Profile, profile.BodyLengthMm, roll.Geometry.BodyLengthMm,
                DownloadToleranceMm(this.machine, profile.BodyLengthMm), null, this.profileTypes, this.settings.ProfileSampleCount);
            RollProfileDefinition own = alignment.Profile is { } aligned
                ? profile with { BodyLengthMm = roll.Geometry.BodyLengthMm, Profile = aligned }
                : profile;

            // 推断来的（旧库升级）记"旧计划"；登记、导入时选的模板记"模板"。
            bool inferred = roll.PlanInferred;
            GrindingPlanRecord plan = await CreateAsync(
                roll, own, program, PlanName(profile.Name, program.Name), roll.Purpose,
                inferred ? PlanSourceKind.Legacy : PlanSourceKind.Template,
                profile.Name + " · " + program.Name, null, setCurrent: true, "system", cancellationToken).ConfigureAwait(false);
            RollRecord migrated = (await this.rolls.GetAsync(rollId, cancellationToken).ConfigureAwait(false))!;
            if (inferred)
            {
                migrated = migrated with { PlanInferred = true };
                await this.rolls.UpsertAsync(migrated, cancellationToken).ConfigureAwait(false);
            }

            _ = plan;
            return migrated;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task<string> UniqueNameAsync(string rollId, string name, int planNo, CancellationToken cancellationToken)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? string.Create(CultureInfo.InvariantCulture, $"#{planNo}") : name.Trim();
        if (!await NameTakenAsync(rollId, trimmed, null, cancellationToken).ConfigureAwait(false))
        {
            return trimmed;
        }

        // 同一支辊里名字不重复：另存出来的新方案先带上方案号，工艺员可以再改名。
        return string.Create(CultureInfo.InvariantCulture, $"{trimmed} ({planNo})");
    }

    private static string PlanName(string profileName, string programName) => profileName + " · " + programName;

    private static string Label(GrindingPlanRecord plan) =>
        string.Create(CultureInfo.InvariantCulture, $"#{plan.PlanNo} {plan.Name}");

    private Task LogAsync(string rollId, string item, string? oldValue, string? newValue, string user, CancellationToken cancellationToken) =>
        this.changeLog.AddAsync(
            new[]
            {
                new ChangeLogEntry(
                    Guid.NewGuid().ToString("N"), this.timeProvider.GetUtcNow(), user, ChangeLogAreas.RollPlan, rollId + " · " + item, oldValue, newValue),
            },
            cancellationToken);
}
