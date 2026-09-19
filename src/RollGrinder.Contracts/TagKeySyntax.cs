using System;
using System.Globalization;

namespace RollGrinder.Contracts;

/// <summary>
/// 逻辑名的下标写法：base[index]。数组变量在 tagmap.json 里用 {index} 占位，
/// 业务代码只用逻辑名，不碰物理地址。
/// </summary>
public static class TagKeySyntax
{
    /// <summary>拼出带下标的逻辑名。</summary>
    public static string Indexed(string baseKey, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseKey);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return string.Create(CultureInfo.InvariantCulture, $"{baseKey}[{index}]");
    }

    /// <summary>拆出基名与下标；不是下标写法时返回 false。</summary>
    public static bool TrySplit(string logicalName, out string baseKey, out int index)
    {
        baseKey = logicalName;
        index = 0;

        if (string.IsNullOrEmpty(logicalName) || logicalName[^1] != ']')
        {
            return false;
        }

        int open = logicalName.LastIndexOf('[');
        if (open <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> indexText = logicalName.AsSpan(open + 1, logicalName.Length - open - 2);
        if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        baseKey = logicalName[..open];
        return true;
    }
}
