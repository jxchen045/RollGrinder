using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services.Jobs;

namespace RollGrinder.Services.Library;

/// <summary>复制关系里的一支辊：它的当前方案复制自这条模板（设计稿 v4 5.6.1 / 5.6.2）。</summary>
/// <param name="Roll">辊。</param>
/// <param name="Plan">当前方案。</param>
/// <param name="Diverged">复制以后改过（与模板"已改动"，通常是特批）。</param>
/// <param name="CopiedVersion">复制时模板是第几版。</param>
/// <param name="Preview">按模板现在这一版复制过来会怎样（复制对齐按名义、下发时按实测）。</param>
/// <param name="Decision">最近一次"必须采用"对它的决定；没有为 null。</param>
/// <param name="Applied">那次"必须采用"已经换上了。</param>
public sealed record TemplateCopyRow(
    RollRecord Roll,
    GrindingPlanRecord Plan,
    bool Diverged,
    int? CopiedVersion,
    CopyPreview Preview,
    MandateDecision? Decision,
    bool Applied);

/// <summary>模板库一行的复制关系摘要（设计稿 v4 5.6.1 P11）。</summary>
/// <param name="Consistent">当前方案复制自它、没改过的辊数。</param>
/// <param name="Diverged">复制自它、改过的辊数。</param>
/// <param name="Mandate">没撤回的最近一次"必须采用"；没有为 null。</param>
/// <param name="MandateApplied">那次已经换上的辊数。</param>
/// <param name="MandateTotal">那次要强制的辊数。</param>
public sealed record TemplateUsage(int Consistent, int Diverged, TemplateMandate? Mandate, int MandateApplied, int MandateTotal)
{
    public int Total => Consistent + Diverged;
}

/// <summary>这支辊来磨时，模板更新带来的事（作业核对页显示）。</summary>
/// <param name="Kind">辊形模板还是工艺模板。</param>
/// <param name="TemplateName">模板名。</param>
/// <param name="Forced">必须采用：已经换上了新方案。</param>
/// <param name="OldPlanNo">原当前方案号。</param>
/// <param name="NewPlanNo">换上的新方案号；只提示或换不了为 null。</param>
/// <param name="ProblemKey">要换但换不了的原因（如长度超出容差）；为 null 表示没问题。</param>
public sealed record TemplateUpdateNotice(TemplateKind Kind, string TemplateName, bool Forced, int OldPlanNo, int? NewPlanNo, string? ProblemKey)
{
    /// <summary>模板标识（"改用新的"按它复制）。</summary>
    public string? TemplateId { get; init; }

    /// <summary>模板现在是第几版。</summary>
    public int? TemplateVersion { get; init; }

    /// <summary>提示的是哪个方案（这支辊的当前方案）。</summary>
    public string? PlanId { get; init; }

    /// <summary>可以一键"改用新的"（只提示的那种；强制的已经换了）。</summary>
    public bool CanAdopt => !Forced && TemplateId is not null;
}

/// <summary>
/// 标准模板（设计说明 4.6）：模板只作复制起点，不被轧辊引用；改了不批量改任何辊。
/// 工艺员保存时可以"必须采用"：复制自它的辊下次来磨时，核对页直接换成从新模板复制的方案（原方案留作历史）。
/// "已改动"的辊由工艺员逐支决定：同样强制 / 改为普通提示 / 不提示。
/// </summary>
public interface ITemplateService
{
    /// <summary>当前方案复制自这条模板的辊（不含作废的）。</summary>
    Task<IReadOnlyList<TemplateCopyRow>> CopiedRollsAsync(TemplateKind kind, string templateId, CancellationToken cancellationToken);

    /// <summary>模板库一行的复制关系与采用进度。</summary>
    Task<TemplateUsage> UsageAsync(TemplateKind kind, string templateId, CancellationToken cancellationToken);

    /// <summary>
    /// 对模板现在这一版下"必须采用"。<paramref name="decisions"/> 没列到的辊按默认：与模板一致的强制、已改动的只提示。
    /// </summary>
    Task<TemplateMandate> CreateMandateAsync(
        TemplateKind kind, string templateId, IReadOnlyDictionary<string, MandateDecision> decisions, string user, CancellationToken cancellationToken);

    /// <summary>撤回：还没来磨的辊不再强制（已换上的不动）。</summary>
    Task RevokeMandateAsync(string mandateId, CancellationToken cancellationToken);

    /// <summary>
    /// 这支辊来磨（打开作业核对）时：要强制的从新模板复制出新方案并设为当前（原方案留作历史），要提示的列出来。
    /// </summary>
    Task<IReadOnlyList<TemplateUpdateNotice>> ApplyForJobAsync(string rollId, string user, CancellationToken cancellationToken);

    /// <summary>
    /// 核对页第 6 格"改用新模板…"（设计稿 v4 I7）：从提示里的新模板复制出新方案设为当前（原方案留作历史，记改动）。
    /// 辊形、工艺各有提示的各换各的。
    /// </summary>
    Task<PlanCreateOutcome> AdoptAsync(string rollId, IReadOnlyList<TemplateUpdateNotice> notices, string user, CancellationToken cancellationToken);

    /// <summary>这支辊这次没改用：同一版不再提示（设计说明 4.6"提示一次"）。</summary>
    Task DeclineAsync(IReadOnlyList<TemplateUpdateNotice> notices, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITemplateService"/>
public sealed class TemplateService : ITemplateService
{
    private readonly IGrindingPlanRepository planRepository;
    private readonly IGrindingPlanService plans;
    private readonly IRollRepository rolls;
    private readonly IRollProfileRepository profiles;
    private readonly IProgramRepository programs;
    private readonly ITemplateMandateRepository mandates;
    private readonly IChangeLogRepository changeLog;
    private readonly TimeProvider timeProvider;

    public TemplateService(
        IGrindingPlanRepository planRepository,
        IGrindingPlanService plans,
        IRollRepository rolls,
        IRollProfileRepository profiles,
        IProgramRepository programs,
        ITemplateMandateRepository mandates,
        IChangeLogRepository changeLog,
        TimeProvider timeProvider)
    {
        this.planRepository = planRepository ?? throw new ArgumentNullException(nameof(planRepository));
        this.plans = plans ?? throw new ArgumentNullException(nameof(plans));
        this.rolls = rolls ?? throw new ArgumentNullException(nameof(rolls));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.programs = programs ?? throw new ArgumentNullException(nameof(programs));
        this.mandates = mandates ?? throw new ArgumentNullException(nameof(mandates));
        this.changeLog = changeLog ?? throw new ArgumentNullException(nameof(changeLog));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<TemplateCopyRow>> CopiedRollsAsync(TemplateKind kind, string templateId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        TemplateMandate? mandate = (await this.mandates.ListByTemplateAsync(kind, templateId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(m => !m.Revoked);
        Dictionary<string, TemplateMandateRoll> decided = mandate is null
            ? new Dictionary<string, TemplateMandateRoll>(StringComparer.Ordinal)
            : (await this.mandates.ListRollsAsync(mandate.MandateId, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.RollId, StringComparer.Ordinal);

        var rows = new List<TemplateCopyRow>();
        foreach (GrindingPlanRecord plan in await this.planRepository.ListByTemplateAsync(kind, templateId, cancellationToken).ConfigureAwait(false))
        {
            RollRecord? roll = await this.rolls.GetAsync(plan.RollId, cancellationToken).ConfigureAwait(false);
            if (roll is null || roll.Retired || roll.CurrentPlanId != plan.PlanId)
            {
                continue;
            }

            CopySource source = kind == TemplateKind.Profile
                ? CopySource.FromTemplates(templateId, null)
                : CopySource.FromTemplates(null, templateId);
            CopyPreview preview = await this.plans.PreviewCopyAsync(roll.RollId, source, CopyScope.All, cancellationToken).ConfigureAwait(false);
            decided.TryGetValue(roll.RollId, out TemplateMandateRoll? decision);
            rows.Add(new TemplateCopyRow(
                roll,
                plan,
                kind == TemplateKind.Profile ? plan.ProfileDiverged : plan.ProgramDiverged,
                kind == TemplateKind.Profile ? plan.TemplateProfileVersion : plan.TemplateProgramVersion,
                preview,
                decision?.Decision,
                decision?.AppliedPlanId is not null));
        }

        return rows;
    }

    public async Task<TemplateUsage> UsageAsync(TemplateKind kind, string templateId, CancellationToken cancellationToken)
    {
        int consistent = 0;
        int diverged = 0;
        foreach (GrindingPlanRecord plan in await this.planRepository.ListByTemplateAsync(kind, templateId, cancellationToken).ConfigureAwait(false))
        {
            RollRecord? roll = await this.rolls.GetAsync(plan.RollId, cancellationToken).ConfigureAwait(false);
            if (roll is null || roll.Retired || roll.CurrentPlanId != plan.PlanId)
            {
                continue;
            }

            if (kind == TemplateKind.Profile ? plan.ProfileDiverged : plan.ProgramDiverged)
            {
                diverged++;
            }
            else
            {
                consistent++;
            }
        }

        TemplateMandate? mandate = (await this.mandates.ListByTemplateAsync(kind, templateId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(m => !m.Revoked);
        int applied = 0;
        int total = 0;
        if (mandate is not null)
        {
            IReadOnlyList<TemplateMandateRoll> list = await this.mandates.ListRollsAsync(mandate.MandateId, cancellationToken).ConfigureAwait(false);
            total = list.Count(r => r.Decision == MandateDecision.Force);
            applied = list.Count(r => r.Decision == MandateDecision.Force && r.AppliedPlanId is not null);
        }

        return new TemplateUsage(consistent, diverged, mandate, applied, total);
    }

    public async Task<TemplateMandate> CreateMandateAsync(
        TemplateKind kind, string templateId, IReadOnlyDictionary<string, MandateDecision> decisions, string user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        (string name, int version) = kind == TemplateKind.Profile
            ? await this.profiles.GetAsync(templateId, cancellationToken).ConfigureAwait(false) is { } profile
                ? (profile.Name, profile.Version)
                : throw new Core.DomainException($"Profile template '{templateId}' does not exist.")
            : await this.programs.GetAsync(templateId, cancellationToken).ConfigureAwait(false) is { } program
                ? (program.Name, program.Version)
                : throw new Core.DomainException($"Program template '{templateId}' does not exist.");

        // 新的一次取代旧的：旧的撤回（已换上的不动）。
        foreach (TemplateMandate old in (await this.mandates.ListByTemplateAsync(kind, templateId, cancellationToken).ConfigureAwait(false))
                     .Where(m => !m.Revoked))
        {
            await this.mandates.RevokeAsync(old.MandateId, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        var mandate = new TemplateMandate(Guid.NewGuid().ToString("N"), kind, templateId, version, name, now, user ?? string.Empty, false);
        var rollsInMandate = new List<TemplateMandateRoll>();
        foreach (TemplateCopyRow row in await CopiedRollsAsync(kind, templateId, cancellationToken).ConfigureAwait(false))
        {
            if (row.CopiedVersion >= version)
            {
                continue; // 已经是这一版复制的，不用换。
            }

            MandateDecision decision = decisions.TryGetValue(row.Roll.RollId, out MandateDecision chosen)
                ? chosen
                : row.Diverged ? MandateDecision.Notice : MandateDecision.Force;
            rollsInMandate.Add(new TemplateMandateRoll(mandate.MandateId, row.Roll.RollId, decision, null, null));
        }

        await this.mandates.AddAsync(mandate, rollsInMandate, cancellationToken).ConfigureAwait(false);
        await this.changeLog.AddAsync(
            new[]
            {
                new ChangeLogEntry(
                    Guid.NewGuid().ToString("N"), now, user ?? string.Empty,
                    kind == TemplateKind.Profile ? ChangeLogAreas.ProfileLibrary : ChangeLogAreas.ProgramLibrary,
                    name + " · mandate", null,
                    "v" + version + " · " + rollsInMandate.Count(r => r.Decision == MandateDecision.Force) + "/" + rollsInMandate.Count),
            },
            cancellationToken).ConfigureAwait(false);
        return mandate;
    }

    public Task RevokeMandateAsync(string mandateId, CancellationToken cancellationToken) =>
        this.mandates.RevokeAsync(mandateId, cancellationToken);

    public async Task<IReadOnlyList<TemplateUpdateNotice>> ApplyForJobAsync(string rollId, string user, CancellationToken cancellationToken)
    {
        var notices = new List<TemplateUpdateNotice>();
        var silenced = new HashSet<(TemplateKind, string)>();
        foreach ((TemplateMandate mandate, TemplateMandateRoll entry) in
                 await this.mandates.ListActiveForRollAsync(rollId, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Decision == MandateDecision.None)
            {
                silenced.Add((mandate.Kind, mandate.TemplateId)); // 工艺员对这支辊选了"不提示"：普通提示也不出。
            }

            if (entry.AppliedPlanId is not null || entry.Decision == MandateDecision.None)
            {
                continue;
            }

            GrindingPlanRecord? current = await this.plans.CurrentAsync(rollId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                continue;
            }

            (string? copiedFrom, int? copiedVersion) = mandate.Kind == TemplateKind.Profile
                ? (current.TemplateProfileId, current.TemplateProfileVersion)
                : (current.TemplateProgramId, current.TemplateProgramVersion);
            if (copiedFrom != mandate.TemplateId || copiedVersion >= mandate.TemplateVersion)
            {
                continue; // 当前方案已经不是从这条模板来的旧版（换过方案、或已经是新版）。
            }

            if (entry.Decision == MandateDecision.Notice)
            {
                notices.Add(new TemplateUpdateNotice(mandate.Kind, mandate.TemplateName, false, current.PlanNo, null, null)
                {
                    TemplateId = mandate.TemplateId,
                    TemplateVersion = mandate.TemplateVersion,
                    PlanId = current.PlanId,
                });
                continue;
            }

            CopySource source = mandate.Kind == TemplateKind.Profile
                ? CopySource.FromTemplates(mandate.TemplateId, null)
                : CopySource.FromTemplates(null, mandate.TemplateId);
            PlanCreateOutcome created = await this.plans
                .CopyToRollAsync(rollId, source, CopyScope.All, confirmed: true, setCurrent: true, user, cancellationToken).ConfigureAwait(false);
            if (created.Plan is { } plan)
            {
                await this.mandates.MarkAppliedAsync(mandate.MandateId, rollId, plan.PlanId, this.timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }

            notices.Add(new TemplateUpdateNotice(mandate.Kind, mandate.TemplateName, true, current.PlanNo, created.Plan?.PlanNo, created.ProblemKey));
        }

        // 普通"标准已更新"（设计说明 4.6 第一段）：当前方案复制自的模板出了新版，没下"必须采用"，也提示一次。
        GrindingPlanRecord? now = await this.plans.CurrentAsync(rollId, cancellationToken).ConfigureAwait(false);
        if (now is not null)
        {
            foreach ((TemplateKind kind, string? templateId, int? copied) in new[]
            {
                (TemplateKind.Profile, now.TemplateProfileId, now.TemplateProfileVersion),
                (TemplateKind.Program, now.TemplateProgramId, now.TemplateProgramVersion),
            })
            {
                if (templateId is null || notices.Any(notice => notice.Kind == kind) || silenced.Contains((kind, templateId)))
                {
                    continue;
                }

                (string? name, int? version, bool disabled) = await TemplateStateAsync(kind, templateId, cancellationToken).ConfigureAwait(false);
                if (disabled || name is null || version is not int latest || latest <= (copied ?? latest)
                    || await this.mandates.IsDeclinedAsync(now.PlanId, kind, templateId, latest, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                notices.Add(new TemplateUpdateNotice(kind, name, false, now.PlanNo, null, null)
                {
                    TemplateId = templateId,
                    TemplateVersion = latest,
                    PlanId = now.PlanId,
                });
            }
        }

        return notices;
    }

    private async Task<(string? Name, int? Version, bool Disabled)> TemplateStateAsync(
        TemplateKind kind, string templateId, CancellationToken cancellationToken)
    {
        if (kind == TemplateKind.Profile)
        {
            return await this.profiles.GetAsync(templateId, cancellationToken).ConfigureAwait(false) is { } profile
                ? (profile.Name, profile.Version, profile.Disabled)
                : (null, null, true);
        }

        return await this.programs.GetAsync(templateId, cancellationToken).ConfigureAwait(false) is { } program
            ? (program.Name, program.Version, program.Disabled)
            : (null, null, true);
    }

    public async Task<PlanCreateOutcome> AdoptAsync(
        string rollId, IReadOnlyList<TemplateUpdateNotice> notices, string user, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollId);
        ArgumentNullException.ThrowIfNull(notices);
        string? profileTemplate = notices.FirstOrDefault(n => n.CanAdopt && n.Kind == TemplateKind.Profile)?.TemplateId;
        string? programTemplate = notices.FirstOrDefault(n => n.CanAdopt && n.Kind == TemplateKind.Program)?.TemplateId;
        if (profileTemplate is null && programTemplate is null)
        {
            return new PlanCreateOutcome(null, "PlanProblem_Missing", null);
        }

        // 只换有新版的那一样；另一样照当前方案（复制范围按给了哪样模板定）。
        return await this.plans.CopyToRollAsync(
            rollId, CopySource.FromTemplates(profileTemplate, programTemplate), CopyScope.All, confirmed: true, setCurrent: true, user, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeclineAsync(IReadOnlyList<TemplateUpdateNotice> notices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notices);
        foreach (TemplateUpdateNotice notice in notices)
        {
            if (notice is { CanAdopt: true, PlanId: { } planId, TemplateId: { } templateId, TemplateVersion: int version })
            {
                await this.mandates.DeclineAsync(planId, notice.Kind, templateId, version, this.timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
