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

    public bool TryResolve(string logicalName, out TagDescriptor? descriptor)
    {
        if (this.byKey.TryGetValue(logicalName, out descriptor))
        {
            return true;
        }

        if (TagKeySyntax.TrySplit(logicalName, out string baseKey, out int index)
            && this.byKey.TryGetValue(baseKey, out TagDescriptor? arrayDescriptor)
            && arrayDescriptor.IsArray
            && index < arrayDescriptor.ArrayLength)
        {
            descriptor = arrayDescriptor.AtIndex(index);
            return true;
        }

        descriptor = null;
        return false;
    }

    public TagDescriptor Resolve(string logicalName)
    {
        if (!TryResolve(logicalName, out TagDescriptor? descriptor) || descriptor is null)
        {
            throw new GatewayException($"Tag '{logicalName}' is not present in the tag map.");
        }

        return descriptor;
    }
}
