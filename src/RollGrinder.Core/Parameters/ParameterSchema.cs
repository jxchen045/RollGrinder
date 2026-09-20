using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Core.Parameters;

/// <summary>
/// 一组参数定义。辊形类型与工序类型各自提供自己的 schema，
/// 界面、NC 生成器与数据库都只按 schema 工作，新增类型不改它们。
/// </summary>
public sealed record ParameterSchema
{
    private readonly Dictionary<string, ParameterDescriptor> byKey;

    public ParameterSchema(IEnumerable<ParameterDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ParameterDescriptor[] materialized = descriptors.ToArray();
        this.byKey = new Dictionary<string, ParameterDescriptor>(materialized.Length, StringComparer.Ordinal);
        foreach (ParameterDescriptor descriptor in materialized)
        {
            if (!this.byKey.TryAdd(descriptor.Key, descriptor))
            {
                throw new DomainException($"Duplicate parameter descriptor '{descriptor.Key}'.");
            }
        }

        Descriptors = materialized;
    }

    /// <summary>空 schema（例如圆柱辊形不需要参数）。</summary>
    public static ParameterSchema Empty { get; } = new(Array.Empty<ParameterDescriptor>());

    public IReadOnlyList<ParameterDescriptor> Descriptors { get; }

    public bool TryGet(string key, out ParameterDescriptor? descriptor) => this.byKey.TryGetValue(key, out descriptor);

    public ParameterDescriptor Get(string key) =>
        this.byKey.TryGetValue(key, out ParameterDescriptor? descriptor)
            ? descriptor
            : throw new DomainException($"Parameter '{key}' is not declared in this schema.");

    /// <summary>按默认值建一份参数集。</summary>
    public ParameterSet CreateDefaults() =>
        new(Descriptors.Select(descriptor =>
            new KeyValuePair<string, ParameterValue>(descriptor.Key, descriptor.DefaultValue)));

    /// <summary>校验一份参数集；只报事实，不产出界面文字。</summary>
    public ParameterValidationResult Validate(ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var violations = new List<ParameterViolation>();

        foreach (ParameterDescriptor descriptor in Descriptors)
        {
            if (!parameters.TryGet(descriptor.Key, out ParameterValue? value) || value is null)
            {
                if (descriptor.IsRequired)
                {
                    violations.Add(new ParameterViolation(descriptor.Key, ParameterViolationKind.Missing));
                }

                continue;
            }

            if (value.Kind != descriptor.Kind)
            {
                violations.Add(new ParameterViolation(descriptor.Key, ParameterViolationKind.KindMismatch));
                continue;
            }

            if (value.Kind == ParameterValueKind.Choice)
            {
                if (descriptor.AllowedValues is { Count: > 0 } allowed
                    && !allowed.Contains(value.Choice, StringComparer.Ordinal))
                {
                    violations.Add(new ParameterViolation(descriptor.Key, ParameterViolationKind.NotAllowed));
                }

                continue;
            }

            if (value.Kind != ParameterValueKind.Number)
            {
                continue;
            }

            if (descriptor.MinValue is double minimum && value.Number < minimum)
            {
                violations.Add(new ParameterViolation(descriptor.Key, ParameterViolationKind.BelowMinimum, minimum));
            }

            if (descriptor.MaxValue is double maximum && value.Number > maximum)
            {
                violations.Add(new ParameterViolation(descriptor.Key, ParameterViolationKind.AboveMaximum, maximum));
            }
        }

        foreach (string key in parameters.Keys)
        {
            if (!this.byKey.ContainsKey(key))
            {
                violations.Add(new ParameterViolation(key, ParameterViolationKind.Unknown));
            }
        }

        return new ParameterValidationResult(violations);
    }

    /// <summary>把缺失的可选参数补成默认值，便于下发与持久化。</summary>
    public ParameterSet ApplyDefaults(ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ParameterSet result = parameters;
        foreach (ParameterDescriptor descriptor in Descriptors)
        {
            if (!result.Contains(descriptor.Key))
            {
                result = result.With(descriptor.Key, descriptor.DefaultValue);
            }
        }

        return result;
    }
}
