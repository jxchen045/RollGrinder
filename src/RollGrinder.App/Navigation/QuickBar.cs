using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.App.Navigation;

/// <summary>
/// 左栏的一个快捷入口（最终稿 D2）。
/// </summary>
/// <param name="Id">machine.json quickBar 里写的名字。</param>
/// <param name="Area">目的区域。</param>
/// <param name="GroupKey">
/// 进区域后直接打开的功能组（对应那个区域的一个横键），null 表示区域的默认画面。
/// 例："砂轮"= 参数区的"砂轮"组。
/// </param>
/// <param name="LabelResourceKey">左栏上的两个字。</param>
/// <param name="Glyph">左栏上的图形符号。</param>
public sealed record QuickBarEntry(string Id, AreaKey Area, string? GroupKey, string LabelResourceKey, string Glyph);

/// <summary>
/// 左栏：最多 7 个快捷入口（Ctrl+1…7），由 machine.json 的 quickBar 决定，
/// 不写就用最终稿 D2 的默认（机床、辊形、工艺、砂轮、库、记录、诊断）。
/// 纯逻辑，写错的名字不让它进左栏，由调用方记日志。
/// </summary>
public static class QuickBarCatalog
{
    /// <summary>左栏入口数上限：Ctrl+1…7。</summary>
    public const int MaxEntries = 7;

    /// <summary>参数区"砂轮"组的键。</summary>
    public const string WheelGroup = "wheel";

    /// <summary>能放进左栏的全部入口。</summary>
    public static IReadOnlyList<QuickBarEntry> Known { get; } = new[]
    {
        new QuickBarEntry("machine", AreaKey.Machine, null, "Quick_Machine", AreaCatalog.Glyph(AreaKey.Machine)),
        new QuickBarEntry("profile", AreaKey.Profile, null, "Quick_Profile", AreaCatalog.Glyph(AreaKey.Profile)),
        new QuickBarEntry("steps", AreaKey.Steps, null, "Quick_Steps", AreaCatalog.Glyph(AreaKey.Steps)),
        new QuickBarEntry("wheel", AreaKey.Parameters, WheelGroup, "Quick_Wheel", "◎"),
        new QuickBarEntry("library", AreaKey.Library, null, "Quick_Library", AreaCatalog.Glyph(AreaKey.Library)),
        new QuickBarEntry("records", AreaKey.Records, null, "Quick_Records", AreaCatalog.Glyph(AreaKey.Records)),
        new QuickBarEntry("diagnostics", AreaKey.Diagnostics, null, "Quick_Diagnostics", AreaCatalog.Glyph(AreaKey.Diagnostics)),
        new QuickBarEntry("parameters", AreaKey.Parameters, null, "Quick_Parameters", AreaCatalog.Glyph(AreaKey.Parameters)),
    };

    /// <summary>最终稿 D2 的默认左栏。</summary>
    public static IReadOnlyList<string> DefaultIds { get; } = new[]
    {
        "machine", "profile", "steps", "wheel", "library", "records", "diagnostics",
    };

    /// <summary>
    /// 按配置排出左栏。未知的名字、重复的名字、超出 7 个的都不要，放进 <paramref name="rejected"/>。
    /// 配置为空（或全写错）时用默认。
    /// </summary>
    public static IReadOnlyList<QuickBarEntry> Resolve(IReadOnlyList<string>? ids, out IReadOnlyList<string> rejected)
    {
        var refused = new List<string>();
        var entries = new List<QuickBarEntry>();

        foreach (string id in ids ?? Array.Empty<string>())
        {
            QuickBarEntry? entry = Known.FirstOrDefault(e => string.Equals(e.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (entry is null || entries.Contains(entry) || entries.Count == MaxEntries)
            {
                refused.Add(id ?? string.Empty);
                continue;
            }

            entries.Add(entry);
        }

        rejected = refused;
        if (entries.Count == 0)
        {
            return DefaultIds.Select(id => Known.First(e => e.Id == id)).ToArray();
        }

        return entries;
    }
}
