using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Core.Parameters;

/// <summary>
/// 一组参数取值。不可变：修改一律返回新实例。
/// </summary>
public sealed record ParameterSet
{
    private readonly Dictionary<string, ParameterValue> values;

    public ParameterSet(IEnumerable<KeyValuePair<string, ParameterValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        this.values = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ParameterValue> pair in values)
        {
            if (!this.values.TryAdd(pair.Key, pair.Value))
            {
                throw new DomainException($"Duplicate parameter key '{pair.Key}'.");
            }
        }

        Keys = this.values.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>空参数集。</summary>
    public static ParameterSet Empty { get; } = new(Array.Empty<KeyValuePair<string, ParameterValue>>());

    /// <summary>按序排列的键。</summary>
    public IReadOnlyList<string> Keys { get; }

    public int Count => this.values.Count;

    public bool Contains(string key) => this.values.ContainsKey(key);

    public bool TryGet(string key, out ParameterValue? value) => this.values.TryGetValue(key, out value);

    /// <summary>取值；键不存在抛 <see cref="DomainException"/>。</summary>
    public ParameterValue Get(string key) =>
        this.values.TryGetValue(key, out ParameterValue? value)
            ? value
            : throw new DomainException($"Parameter '{key}' is missing.");

    public double GetNumber(string key) => Get(key).Number;

    public bool GetBoolean(string key) => Get(key).Boolean;

    public string GetText(string key) => Get(key).Text;

    /// <summary>取数值，键不存在时返回给定的兜底值。</summary>
    public double GetNumberOrDefault(string key, double fallback) =>
        this.values.TryGetValue(key, out ParameterValue? value) && value.Kind == ParameterValueKind.Number
            ? value.Number
            : fallback;

    /// <summary>返回增加或替换了一个键的新参数集。</summary>
    public ParameterSet With(string key, ParameterValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var copy = new Dictionary<string, ParameterValue>(this.values, StringComparer.Ordinal)
        {
            [key] = value,
        };
        return new ParameterSet(copy);
    }

    /// <summary>以本集合为底，用 <paramref name="overrides"/> 覆盖后返回新参数集。</summary>
    public ParameterSet Merge(ParameterSet overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var copy = new Dictionary<string, ParameterValue>(this.values, StringComparer.Ordinal);
        foreach (string key in overrides.Keys)
        {
            copy[key] = overrides.Get(key);
        }

        return new ParameterSet(copy);
    }

    /// <summary>用于持久化与比较的键值对。</summary>
    public IReadOnlyList<KeyValuePair<string, ParameterValue>> ToOrderedPairs() =>
        Keys.Select(key => new KeyValuePair<string, ParameterValue>(key, this.values[key])).ToArray();
}
