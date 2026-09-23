using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 时间处理守卫。`new DateTimeOffset(someDateTime, TimeSpan.Zero)` 在 UTC 的开发机上永远不出事，
/// 到了东八区的工控机上，只要传进来的是本地时间（DateTime.Today、DateTime.Now）就直接抛异常——
/// 磨削记录页曾因此整页不可用。本地日历日一律经 Core.Time.LocalDays 换算。
/// </summary>
public sealed partial class TimeHandlingGuardTests
{
    [Fact]
    public void No_DateTimeOffset_is_built_with_a_hard_coded_zero_offset()
    {
        string src = Path.Combine(RepositoryLayout.Root, "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (File: Path.GetRelativePath(src, f), Line: i + 1, Text: line))
                .Where(l => ZeroOffset().IsMatch(l.Text)))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        offenders.Should().BeEmpty("本地日期换 UTC 区间请用 LocalDays.Range");
    }

    [GeneratedRegex(@"new\s+DateTimeOffset\s*\([^;]*TimeSpan\.Zero\s*\)")]
    private static partial Regex ZeroOffset();
}
