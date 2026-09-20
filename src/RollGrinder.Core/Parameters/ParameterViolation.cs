using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Core.Parameters;

/// <summary>参数校验失败的原因。界面按原因 + 参数键去取本地化文案，领域层不产出界面文字。</summary>
public enum ParameterViolationKind
{
    /// <summary>必填参数缺失。</summary>
    Missing = 0,

    /// <summary>取值种类不符（例如给数值参数传了文本）。</summary>
    KindMismatch = 1,

    /// <summary>小于下限。</summary>
    BelowMinimum = 2,

    /// <summary>大于上限。</summary>
    AboveMaximum = 3,

    /// <summary>不在 schema 中声明的多余参数。</summary>
    Unknown = 4,

    /// <summary>超出本台机床的能力（行程、阈值等，由机床描述给出）。</summary>
    ExceedsMachineLimit = 5,

    /// <summary>选项参数取了一个未声明的选项。</summary>
    NotAllowed = 6,

    /// <summary>参数之间互相矛盾（例如周期进给 × 道次 与 磨削量 对不上）。</summary>
    Inconsistent = 7,
}

/// <summary>一条参数校验失败记录。</summary>
/// <param name="ParameterKey">出问题的参数键。</param>
/// <param name="Kind">失败原因。</param>
/// <param name="Limit">越界时的限值，其他情况为空。</param>
public sealed record ParameterViolation(string ParameterKey, ParameterViolationKind Kind, double? Limit = null);

/// <summary>校验结果。</summary>
public sealed record ParameterValidationResult(IReadOnlyList<ParameterViolation> Violations)
{
    /// <summary>通过校验的结果。</summary>
    public static ParameterValidationResult Valid { get; } = new(System.Array.Empty<ParameterViolation>());

    public bool IsValid => Violations.Count == 0;

    /// <summary>合并多个校验结果。</summary>
    public static ParameterValidationResult Combine(params ParameterValidationResult[] results) =>
        new(results.SelectMany(result => result.Violations).ToArray());
}
