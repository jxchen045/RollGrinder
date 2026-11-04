using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RollGrinder.Data;

/// <summary>"必须采用"对一支辊怎么处理（设计说明 4.6、设计稿 v4 5.6.2）。</summary>
public enum MandateDecision
{
    /// <summary>同样强制：这支辊下次来磨，核对页直接换成从新模板复制的方案。</summary>
    Force = 0,

    /// <summary>改为普通提示：只提示"标准已更新"，沿用还是改用由现场决定。</summary>
    Notice = 1,

    /// <summary>不提示（特批方案，不受这次更新影响）。</summary>
    None = 2,
}

/// <summary>一次"必须采用"：哪条模板的哪一版。</summary>
public sealed record TemplateMandate(
    string MandateId,
    TemplateKind Kind,
    string TemplateId,
    int TemplateVersion,
    string TemplateName,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    bool Revoked);

/// <summary>"必须采用"里的一支辊：决定、换上了没有。</summary>
public sealed record TemplateMandateRoll(
    string MandateId,
    string RollId,
    MandateDecision Decision,
    string? AppliedPlanId,
    DateTimeOffset? AppliedAtUtc);

/// <summary>"必须采用"的存取。</summary>
public interface ITemplateMandateRepository
{
    Task AddAsync(TemplateMandate mandate, IReadOnlyList<TemplateMandateRoll> rolls, CancellationToken cancellationToken);

    /// <summary>这条模板的全部（新的在前）。</summary>
    Task<IReadOnlyList<TemplateMandate>> ListByTemplateAsync(TemplateKind kind, string templateId, CancellationToken cancellationToken);

    /// <summary>涉及这支辊、没撤回的（新的在前）。</summary>
    Task<IReadOnlyList<(TemplateMandate Mandate, TemplateMandateRoll Roll)>> ListActiveForRollAsync(string rollId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TemplateMandateRoll>> ListRollsAsync(string mandateId, CancellationToken cancellationToken);

    /// <summary>记下这支辊已经换上了哪个方案。</summary>
    Task MarkAppliedAsync(string mandateId, string rollId, string planId, DateTimeOffset atUtc, CancellationToken cancellationToken);

    /// <summary>撤回：还没换上的辊不再强制（已换上的不动）。</summary>
    Task RevokeAsync(string mandateId, CancellationToken cancellationToken);
}
