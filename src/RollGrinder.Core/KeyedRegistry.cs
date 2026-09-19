using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Core;

/// <summary>按 Key 索引的只读注册表。新增一类只需注册一个实现，使用方不改。</summary>
/// <typeparam name="T">被注册的类型。</typeparam>
public abstract class KeyedRegistry<T>
    where T : class
{
    private readonly Dictionary<string, T> byKey;

    protected KeyedRegistry(IEnumerable<T> items, Func<T, string> keySelector)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keySelector);

        T[] materialized = items.ToArray();
        this.byKey = new Dictionary<string, T>(materialized.Length, StringComparer.Ordinal);
        foreach (T item in materialized)
        {
            string key = keySelector(item);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new DomainException($"A {typeof(T).Name} was registered without a key.");
            }

            if (!this.byKey.TryAdd(key, item))
            {
                throw new DomainException($"Duplicate {typeof(T).Name} key '{key}'.");
            }
        }

        All = materialized.OrderBy(keySelector, StringComparer.Ordinal).ToArray();
    }

    /// <summary>按键排序的全部注册项。</summary>
    public IReadOnlyList<T> All { get; }

    public bool TryGet(string key, out T? item) => this.byKey.TryGetValue(key, out item);

    public T Get(string key) =>
        this.byKey.TryGetValue(key, out T? item)
            ? item
            : throw new DomainException($"No {typeof(T).Name} is registered for key '{key}'.");
}
