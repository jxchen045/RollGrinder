using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Composition;

/// <summary>
/// <see cref="ITagMap"/> 的默认实现：tagmap.json 载入后的只读查找表。
/// </summary>
public sealed class TagMap : ITagMap
{
    private readonly Dictionary<string, TagDescriptor> byKey;

    public TagMap(IEnumerable<TagDescriptor> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        TagDescriptor[] materialized = tags.ToArray();
        this.byKey = new Dictionary<string, TagDescriptor>(materialized.Length, StringComparer.Ordinal);
        foreach (TagDescriptor tag in materialized)
        {
            if (!this.byKey.TryAdd(tag.Key, tag))
            {
                throw new GatewayException($"Duplicate tag key '{tag.Key}' in tag map.");
            }
        }

        Tags = materialized;
    }

    public IReadOnlyList<TagDescriptor> Tags { get; }

    public bool TryResolve(string logicalName, out TagDescriptor? descriptor) =>
        this.byKey.TryGetValue(logicalName, out descriptor);

    public TagDescriptor Resolve(string logicalName)
    {
        if (!this.byKey.TryGetValue(logicalName, out TagDescriptor? descriptor))
        {
            throw new GatewayException($"Tag '{logicalName}' is not present in the tag map.");
        }

        return descriptor;
    }
}
