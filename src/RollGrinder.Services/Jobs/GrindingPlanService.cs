using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Parameters;
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
    /// <summary>改过的可调范围（整份，设计稿 v4 5.5.3）；没改为 null。</summary>
    public IReadOnlyList<PlanParamRange>? Ranges { get; init; }

    /// <summary>什么都没改。</summary>
    public bool IsEmpty => Profile is null && Program is null && !EndZoneChanged && Ranges is null;
}

/// <summary>"存为新方案"（设计稿 v4 5.8.4）的结果。</summary>
/// <param name="Plan">建出来的方案；没建为 null。</param>
/// <param name="ProblemKey">建不了的原因（资源键）：作业不在、作业没有方案、名字重复。</param>
public sealed record PlanFromJobOutcome(GrindingPlanRecord? Plan, string? ProblemKey);

/// <summary>按模板建方案 / 复制方案的结果。</summary>
/// <param name="Plan">建出来的方案；建不了为 null。</param>
/// <param name="ProblemKey">建不了的原因（资源键）。</param>
/// <param name="Alignment">来源名义长度对到本辊名义长度的结论（复制对齐）。</param>
public sealed record PlanCreateOutcome(GrindingPlanRecord? Plan, string? ProblemKey, CopyAlignment? Alignment);

/// <summary>复制范围（设计稿 v4 5.5.5 P10）：辊形 + 工艺、只辊形、只工艺（没复制的那样取本辊当前方案的）。</summary>
public enum CopyScope
{
    All = 0,
    ProfileOnly = 1,
    ProgramOnly = 2,
}

/// <summary>复制从哪来：某支辊的某个方案，或者辊形模板 / 工艺模板（给哪样复制哪样，另一样取当前方案的）。</summary>
public sealed record CopySource(string? PlanId = null, string? ProfileTemplateId = null, string? ProgramTemplateId = null)
{
    public static CopySource FromPlan(string planId) => new(PlanId: planId);

    public static CopySource FromTemplates(string? profileTemplateId, string? programTemplateId) => new(null, profileTemplateId, programTemplateId);
}

/// <summary>复制预览（设计稿 v4 5.5.5 / 5.4.3 / 5.4.4 的长度对齐一行）。</summary>
/// <param name="Alignment">复制对齐（按名义长度）；没复制辊形时为 null。</param>
/// <param name="ProblemKey">复制不了的原因；能复制为 null（须确认看 <see cref="CopyAlignment.NeedsConfirm"/>）。</param>
/// <param name="SourceText">来源一句话（辊号 / 方案号或模板名）。</param>
/// <param name="ProfileName">复制后的辊形名。</param>
/// <param name="ProgramName">复制后的工艺名。</param>
/// <param name="Download">按本辊实测长度预告以后每次下发的截取 / 延伸；没有实测为 null。</param>
public sealed record CopyPreview(
    CopyAlignment? Alignment,
    string? ProblemKey,
    string SourceText,
    string ProfileName,
    string ProgramName,
    LengthAlignment? Download)
{
    public bool NeedsConfirm => Alignment is { NeedsConfirm: true };
}

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
        string rollId, string? profileTemplateId, string? programTemplateId, bool setCurrent, string user, CancellationToken cancellationToken,
        bool confirmed = true);

    /// <summary>只看能不能按模板建（不落库）：返回原因资源键，能建为 null。</summary>
    Task<string?> CheckTemplatesAsync(string rollId, string? profileTemplateId, string? programTemplateId, CancellationToken cancellationToken);

    /// <summary>复制预览：长度对齐（复制门槛、解析段中间铺满、CVC / 点表截取 / 延伸 / 拒绝）、来源、下发时预告。</summary>
    Task<CopyPreview> PreviewCopyAsync(string rollId, CopySource source, CopyScope scope, CancellationToken cancellationToken);

    /// <summary>同上，给还没登记的辊（新登记表）：按表里的名义 / 实测长度与类型算。</summary>
    Task<CopyPreview> PreviewCopyForNewRollAsync(
        double nominalLengthMm, double? measuredLengthMm, RollKind kind, CopySource source, CancellationToken cancellationToken);

    /// <summary>
    /// 复制成本辊的新方案（来源记好）。须确认的（凸度类中间铺满、截 / 延超出端部非工作区）要 <paramref name="confirmed"/>；
    /// 超出容差的不复制。复制出的方案默认不设为当前（设计稿 v4 I17）。
    /// </summary>
    Task<PlanCreateOutcome> CopyToRollAsync(
        string rollId, CopySource source, CopyScope scope, bool confirmed, bool setCurrent, string user, CancellationToken cancellationToken);

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

    /// <summary>方案改过的可调范围（没改的按默认范围表）。</summary>
    Task<IReadOnlyList<PlanParamRange>> GetRangesAsync(string planId, CancellationToken cancellationToken);

    /// <summary>
    /// 存为新方案（设计说明 5.4 / 设计稿 v4 5.8.4）：作业所用方案 + 勾上的调整（方案值换成本次值），来源记为该作业；
    /// 范围照原方案。名字在这支辊里重复不建。
    /// </summary>
    Task<PlanFromJobOutcome> CreateFromJobAsync(
        string jobId, IReadOnlyList<JobAdjustment> adopt, string name, bool setCurrent, string user, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGrindingPlanService"/>
public sealed class GrindingPlanService : IGrindingPlanService
{
    private readonly IGrindingPlanRepository plans;
    private readonly IRollRepository rolls;
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly IChangeLogRepository changeLog;
    private readonly IJobRepository jobs;
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
        TimeProvider timeProvider,
        IJobRepository jobs)
    {
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
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

    /// <summary>复制门槛（mm）：配置里的，不写按 1。</summary>
    public static double CopyThresholdMm(MachineDescription machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine.Threshold(MachineDescription.CopyThresholdMmKey) ?? ProfileCopyAlignment.DefaultThresholdMm;
    }

    public Task<PlanCreateOutcome> CreateFromTemplatesAsync(
        string rollId, string? profileTemplateId, string? programTemplateId, bool setCurrent, string user, CancellationToken cancellationToken,
        bool confirmed = true) =>
        CopyToRollAsync(rollId, CopySource.FromTemplates(profileTemplateId, programTemplateId), CopyScope.All, confirmed, setCurrent, user, cancellationToken);

    public async Task<string?> CheckTemplatesAsync(
        string rollId, string? profileTemplateId, string? programTemplateId, CancellationToken cancellationToken) =>
        (await PreviewCopyAsync(rollId, CopySource.FromTemplates(profileTemplateId, programTemplateId), CopyScope.All, cancellationToken)
            .ConfigureAwait(false)).ProblemKey;

    public async Task<CopyPreview> PreviewCopyAsync(string rollId, CopySource source, CopyScope scope, CancellationToken cancellationToken)
    {
        RollRecord? roll = await EnsurePlansAsync(rollId, cancellationToken).ConfigureAwait(false);
        if (roll is null)
        {
            return new CopyPreview(null, "PlanProblem_Missing", "--", "--", "--", null);
        }

        return (await ResolveCopyAsync(roll, roll.Geometry.BodyLengthMm, roll.MeasuredBodyLengthMm, roll.Kind, source, scope, cancellationToken)
            .ConfigureAwait(false)).Preview;
    }

    public async Task<CopyPreview> PreviewCopyForNewRollAsync(
        double nominalLengthMm, double? measuredLengthMm, RollKind kind, CopySource source, CancellationToken cancellationToken)
    {
        if (nominalLengthMm <= 0.0)
        {
            return new CopyPreview(null, "PlanProblem_Missing", "--", "--", "--", null);
        }

        return (await ResolveCopyAsync(null, nominalLengthMm, measuredLengthMm, kind, source, CopyScope.All, cancellationToken)
            .ConfigureAwait(false)).Preview;
    }

    public async Task<PlanCreateOutcome> CopyToRollAsync(
        string rollId, CopySource source, CopyScope scope, bool confirmed, bool setCurrent, string user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        RollRecord? roll = await EnsurePlansAsync(rollId, cancellationToken).ConfigureAwait(false);
        if (roll is null)
        {
            return new PlanCreateOutcome(null, "PlanProblem_Missing", null);
        }

        CopyResolution resolved = await ResolveCopyAsync(
            roll, roll.Geometry.BodyLengthMm, roll.MeasuredBodyLengthMm, roll.Kind, source, scope, cancellationToken).ConfigureAwait(false);
        if (resolved.Preview.ProblemKey is { } problem)
        {
            return new PlanCreateOutcome(null, problem, resolved.Preview.Alignment);
        }

        if (resolved.Preview.NeedsConfirm && !confirmed)
        {
            return new PlanCreateOutcome(null, "PlanProblem_NeedsConfirm", resolved.Preview.Alignment);
        }

        // 范围跟着工艺走：工艺从哪个方案来，范围就照哪个方案的（工艺模板没有范围，取默认表）。
        string? rangePlanId = source.PlanId is not null
            ? (scope == CopyScope.ProfileOnly ? roll.CurrentPlanId : source.PlanId)
            : (source.ProgramTemplateId is null ? roll.CurrentPlanId : null);
        IReadOnlyList<PlanParamRange>? ranges = rangePlanId is null
            ? null
            : await this.plans.GetRangesAsync(rangePlanId, cancellationToken).ConfigureAwait(false);
        GrindingPlanRecord plan = await CreateAsync(
            roll, resolved.Profile!, resolved.Program!, resolved.Name, resolved.Purpose ?? roll.Purpose,
            resolved.SourceKind, resolved.SourceRef, resolved.EndZoneMm, setCurrent, user, cancellationToken, lineage: resolved.Lineage,
            ranges: ranges).ConfigureAwait(false);
        return new PlanCreateOutcome(plan, null, resolved.Preview.Alignment);
    }

    /// <summary>方案的模板来源：辊形 / 工艺各复制自哪条模板第几版、之后改没改过。</summary>
    private sealed record Lineage(
        string? ProfileTemplateId, int? ProfileTemplateVersion, bool ProfileDiverged,
        string? ProgramTemplateId, int? ProgramTemplateVersion, bool ProgramDiverged)
    {
        public static Lineage None { get; } = new(null, null, false, null, null, false);

        public static Lineage Of(GrindingPlanRecord plan) => new(
            plan.TemplateProfileId, plan.TemplateProfileVersion, plan.ProfileDiverged,
            plan.TemplateProgramId, plan.TemplateProgramVersion, plan.ProgramDiverged);

        public GrindingPlanRecord ApplyTo(GrindingPlanRecord plan) => plan with
        {
            TemplateProfileId = ProfileTemplateId,
            TemplateProfileVersion = ProfileTemplateVersion,
            ProfileDiverged = ProfileDiverged,
            TemplateProgramId = ProgramTemplateId,
            TemplateProgramVersion = ProgramTemplateVersion,
            ProgramDiverged = ProgramDiverged,
        };
    }

    private sealed record CopyResolution(
        CopyPreview Preview,
        RollProfileDefinition? Profile,
        GrindingProgram? Program,
        string Name,
        string? Purpose,
        PlanSourceKind SourceKind,
        string? SourceRef,
        double? EndZoneMm,
        Lineage Lineage);

    /// <summary>
    /// 复制的全部判断：来源取哪样、没复制的那样取本辊当前方案的；辊形按复制规则对到本辊名义长度（设计说明 4.5）；
    /// 作废的辊、停用的模板、类型不符拦下。<paramref name="roll"/> 为 null 是还没登记的辊。
    /// </summary>
    private async Task<CopyResolution> ResolveCopyAsync(
        RollRecord? roll, double targetNominalMm, double? measuredMm, RollKind rollKind, CopySource source, CopyScope scope,
        CancellationToken cancellationToken)
    {
        CopyResolution Fail(string key, CopyAlignment? alignment = null) =>
            new(new CopyPreview(alignment, key, "--", "--", "--", null), null, null, string.Empty, null, PlanSourceKind.Blank, null, null, Lineage.None);

        if (roll is { Retired: true })
        {
            return Fail("PlanProblem_Retired");
        }

        PlanContent? current = roll?.CurrentPlanId is { } currentId
            ? await GetContentAsync(currentId, cancellationToken).ConfigureAwait(false)
            : null;

        RollProfileDefinition? sourceProfile = null;
        GrindingProgram? program = null;
        string? profileTemplateId = null;
        int? profileTemplateVersion = null;
        bool profileDiverged = false;
        string? programTemplateId = null;
        int? programTemplateVersion = null;
        bool programDiverged = false;
        double? endZone = current?.Plan.EndZoneMm;
        string name;
        string? purpose = null;
        PlanSourceKind kind;
        string sourceText;

        if (source.PlanId is { } planId)
        {
            PlanContent? from = await GetContentAsync(planId, cancellationToken).ConfigureAwait(false);
            if (from is null)
            {
                return Fail("PlanProblem_Missing");
            }

            bool sameRoll = roll is not null && from.Plan.RollId == roll.RollId;
            kind = sameRoll ? PlanSourceKind.ThisRollPlan : PlanSourceKind.OtherRollPlan;
            sourceText = sameRoll
                ? from.Plan.PlanNo.ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{from.Plan.RollId} / {from.Plan.PlanNo}");
            name = from.Plan.Name;
            purpose = from.Plan.Purpose;
            if (scope != CopyScope.ProgramOnly)
            {
                sourceProfile = from.Profile;
                (profileTemplateId, profileTemplateVersion, profileDiverged) =
                    (from.Plan.TemplateProfileId, from.Plan.TemplateProfileVersion, from.Plan.ProfileDiverged);
                endZone = from.Plan.EndZoneMm;
            }

            if (scope != CopyScope.ProfileOnly)
            {
                program = from.Program;
                (programTemplateId, programTemplateVersion, programDiverged) =
                    (from.Plan.TemplateProgramId, from.Plan.TemplateProgramVersion, from.Plan.ProgramDiverged);
            }
        }
        else
        {
            RollProfileDefinition? profileTemplate = source.ProfileTemplateId is null || scope == CopyScope.ProgramOnly
                ? null
                : await this.profiles.GetAsync(source.ProfileTemplateId, cancellationToken).ConfigureAwait(false);
            GrindingProgram? programTemplate = source.ProgramTemplateId is null || scope == CopyScope.ProfileOnly
                ? null
                : await this.programs.GetAsync(source.ProgramTemplateId, cancellationToken).ConfigureAwait(false);
            if ((source.ProfileTemplateId is not null && scope != CopyScope.ProgramOnly && profileTemplate is null)
                || (source.ProgramTemplateId is not null && scope != CopyScope.ProfileOnly && programTemplate is null))
            {
                return Fail("PlanProblem_Missing");
            }

            if (profileTemplate is { Disabled: true } || programTemplate is { Disabled: true })
            {
                return Fail("PlanProblem_Disabled");
            }

            kind = PlanSourceKind.Template;
            if (profileTemplate is not null)
            {
                sourceProfile = profileTemplate;
                (profileTemplateId, profileTemplateVersion, profileDiverged) = (profileTemplate.ProfileId, profileTemplate.Version, false);
                endZone = null;
            }

            if (programTemplate is not null)
            {
                program = programTemplate;
                (programTemplateId, programTemplateVersion, programDiverged) = (programTemplate.ProgramId, programTemplate.Version, false);
            }

            sourceText = string.Join(" · ", new[] { profileTemplate?.Name, programTemplate?.Name }.Where(text => text is not null));
            name = string.Empty;
        }

        // 没复制的那样：沿用本辊当前方案的（连同它的模板来源）。
        RollProfileDefinition? profile = null;
        CopyAlignment? alignment = null;
        if (sourceProfile is not null)
        {
            alignment = ProfileCopyAlignment.Align(
                sourceProfile.Profile, sourceProfile.BodyLengthMm, targetNominalMm, CopyThresholdMm(this.machine),
                DownloadToleranceMm(this.machine, sourceProfile.BodyLengthMm), this.profileTypes, this.settings.ProfileSampleCount);
            if (alignment.Profile is not { } aligned)
            {
                return Fail("PlanProblem_Length", alignment);
            }

            profile = sourceProfile with { BodyLengthMm = targetNominalMm, Profile = aligned };
        }
        else if (current is not null)
        {
            profile = current.Profile;
            (profileTemplateId, profileTemplateVersion, profileDiverged) =
                (current.Plan.TemplateProfileId, current.Plan.TemplateProfileVersion, current.Plan.ProfileDiverged);
        }

        if (program is null && current is not null)
        {
            program = current.Program;
            (programTemplateId, programTemplateVersion, programDiverged) =
                (current.Plan.TemplateProgramId, current.Plan.TemplateProgramVersion, current.Plan.ProgramDiverged);
        }

        if (profile is null || program is null)
        {
            return Fail("PlanProblem_Missing", alignment);
        }

        if (program.ApplicableRollKind != RollKind.Unspecified && rollKind != RollKind.Unspecified && program.ApplicableRollKind != rollKind)
        {
            return Fail("PlanProblem_Kind", alignment);
        }

        LengthAlignment? download = measuredMm is double measured
            ? AlignForJob(profile, measured, endZone, this.machine, this.profileTypes, this.settings.ProfileSampleCount)
            : null;
        if (name.Length == 0)
        {
            name = PlanName(profile.Name, program.Name);
        }

        var preview = new CopyPreview(alignment, null, sourceText, profile.Name, program.Name, download);
        return new CopyResolution(
            preview, profile, program, name, purpose, kind, sourceText, endZone,
            new Lineage(profileTemplateId, profileTemplateVersion, profileDiverged, programTemplateId, programTemplateVersion, programDiverged));
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
            setCurrent, user, cancellationToken, lineage: Lineage.Of(content.Plan),
            ranges: await this.plans.GetRangesAsync(planId, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
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

    public Task<IReadOnlyList<PlanParamRange>> GetRangesAsync(string planId, CancellationToken cancellationToken) =>
        this.plans.GetRangesAsync(planId, cancellationToken);

    public async Task<PlanFromJobOutcome> CreateFromJobAsync(
        string jobId, IReadOnlyList<JobAdjustment> adopt, string name, bool setCurrent, string user, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(adopt);
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new PlanFromJobOutcome(null, "PlanProblem_NameEmpty");
        }

        (GrindingJob Job, JobState State)? stored = await this.jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (stored is not { } found)
        {
            return new PlanFromJobOutcome(null, "PlanProblem_Missing");
        }

        GrindingJob job = found.Job;
        PlanContent? content = job.PlanId is { } planId ? await GetContentAsync(planId, cancellationToken).ConfigureAwait(false) : null;
        RollRecord? roll = await this.rolls.GetAsync(job.RollId, cancellationToken).ConfigureAwait(false);
        if (content is null || roll is null)
        {
            return new PlanFromJobOutcome(null, "PlanProblem_NoPlan");
        }

        if (await NameTakenAsync(roll.RollId, trimmed, null, cancellationToken).ConfigureAwait(false))
        {
            return new PlanFromJobOutcome(null, "PlanProblem_NameTaken");
        }

        // 方案值换成本次值：同一参数改了几次取最后一次。
        GrindingProgram program = content.Program;
        GrindingJobStep[] steps = program.Steps.ToArray();
        foreach (JobAdjustment adjustment in adopt
            .GroupBy(item => (item.StepOrder, item.ParameterKey))
            .Select(group => group.Last()))
        {
            int index = Array.FindIndex(steps, step => step.Order == adjustment.StepOrder);
            if (index >= 0 && double.IsFinite(adjustment.JobValue))
            {
                steps[index] = steps[index] with
                {
                    Parameters = steps[index].Parameters.With(adjustment.ParameterKey, ParameterValue.FromNumber(adjustment.JobValue)),
                };
            }
        }

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GrindingPlanRecord plan = await CreateAsync(
                roll, content.Profile, program with { Steps = steps }, trimmed, content.Plan.Purpose, PlanSourceKind.Job, jobId,
                content.Plan.EndZoneMm, setCurrent, user, cancellationToken, uniqueName: false,
                lineage: Lineage.Of(content.Plan) with { ProgramDiverged = content.Plan.ProgramDiverged || adopt.Count > 0 },
                ranges: await this.plans.GetRangesAsync(content.Plan.PlanId, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            return new PlanFromJobOutcome(plan, null);
        }
        finally
        {
            this.gate.Release();
        }
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

                // 复制以后改过：与模板"已改动"（必须采用时按它分，默认改为普通提示）。
                GrindingPlanRecord kept = content.Plan with
                {
                    EndZoneMm = endZone,
                    ProfileDiverged = content.Plan.ProfileDiverged || profile is not null || edit.EndZoneChanged,
                    ProgramDiverged = content.Plan.ProgramDiverged || program is not null,
                };
                if (kept != content.Plan)
                {
                    await this.plans.SaveAsync(kept, cancellationToken).ConfigureAwait(false);
                }

                if (edit.Ranges is not null)
                {
                    await this.plans.SaveRangesAsync(planId, edit.Ranges, cancellationToken).ConfigureAwait(false);
                }

                string what = string.Join(
                    "+",
                    new[]
                    {
                        profile is not null ? "profile" : null, program is not null ? "program" : null, edit.EndZoneChanged ? "endZone" : null,
                        edit.Ranges is not null ? "ranges" : null,
                    }
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
                uniqueName: true,
                lineage: Lineage.Of(content.Plan) with
                {
                    ProfileDiverged = content.Plan.ProfileDiverged || profile is not null || edit.EndZoneChanged,
                    ProgramDiverged = content.Plan.ProgramDiverged || program is not null,
                },
                ranges: edit.Ranges ?? await this.plans.GetRangesAsync(planId, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
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
        bool uniqueName = true,
        Lineage? lineage = null,
        IReadOnlyList<PlanParamRange>? ranges = null)
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
        GrindingPlanRecord plan = (lineage ?? Lineage.None).ApplyTo(new GrindingPlanRecord(
            "PL-" + stem, roll.RollId, planNo, planName, purpose, ownProfile.ProfileId, ownProgram.ProgramId,
            sourceKind, sourceRef, endZoneMm, now, user ?? string.Empty));

        // 先写方案行：辊形 / 工艺一落库就被它认领，不会在模板列表里闪一下。
        await this.plans.SaveAsync(plan, cancellationToken).ConfigureAwait(false);
        await this.profiles.SaveAsync(ownProfile, cancellationToken).ConfigureAwait(false);
        await this.programs.SaveAsync(ownProgram, cancellationToken).ConfigureAwait(false);
        if (ranges is { Count: > 0 })
        {
            await this.plans.SaveRangesAsync(plan.PlanId, ranges, cancellationToken).ConfigureAwait(false);
        }

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

            // 登记、导入时选的模板按复制规则对到本辊名义长度（这一步在登记表里已经看过、确认过）；
            // 对不上（超出容差）原样存，留给作业核对去拦。
            CopyResolution resolved = await ResolveCopyAsync(
                roll, roll.Geometry.BodyLengthMm, roll.MeasuredBodyLengthMm, roll.Kind,
                CopySource.FromTemplates(profile.ProfileId, program.ProgramId), CopyScope.All, cancellationToken).ConfigureAwait(false);
            RollProfileDefinition own = resolved.Profile ?? profile;
            var lineage = new Lineage(profile.ProfileId, profile.Version, false, program.ProgramId, program.Version, false);

            // 推断来的（旧库升级）记"旧计划"；登记、导入时选的模板记"模板"。
            bool inferred = roll.PlanInferred;
            await CreateAsync(
                roll, own, program, PlanName(profile.Name, program.Name), roll.Purpose,
                inferred ? PlanSourceKind.Legacy : PlanSourceKind.Template,
                profile.Name + " · " + program.Name, null, setCurrent: true, "system", cancellationToken, lineage: lineage).ConfigureAwait(false);
            RollRecord migrated = (await this.rolls.GetAsync(rollId, cancellationToken).ConfigureAwait(false))!;
            if (inferred)
            {
                migrated = migrated with { PlanInferred = true };
                await this.rolls.UpsertAsync(migrated, cancellationToken).ConfigureAwait(false);
            }

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
