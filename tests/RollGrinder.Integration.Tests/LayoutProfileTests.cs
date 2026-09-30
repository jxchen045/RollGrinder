using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using RollGrinder.App.Controls;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>两档版面（分辨率适配方案第 3 节）：选档规则、两份尺寸字典对得上、页面里不留写死的大尺寸。</summary>
public sealed class LayoutProfileTests
{
    private static readonly string App = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("auto", 1920, 1080, LayoutKind.Standard)]
    [InlineData("auto", 1366, 768, LayoutKind.Compact)]
    [InlineData("auto", 1536, 864, LayoutKind.Compact)]
    [InlineData("standard", 1366, 768, LayoutKind.Standard)]
    [InlineData("compact", 1920, 1080, LayoutKind.Compact)]
    public void Chooses_the_layout_from_setting_and_screen(string setting, double width, double height, LayoutKind expected) =>
        LayoutChooser.Choose(setting, width, height).Should().Be(expected);

    [Fact]
    public void Both_layout_dictionaries_define_the_same_keys()
    {
        string[] Keys(string file) => XDocument.Load(Path.Combine(App, "Themes", file)).Root!.Elements()
            .Select(e => (string)e.Attribute(X + "Key")!).OrderBy(k => k).ToArray();

        Keys("Layout.Compact.xaml").Should().Equal(Keys("Layout.Standard.xaml"));
    }

    [Fact]
    public void Views_have_no_literal_size_of_100_or_more()
    {
        // 大尺寸必须写成 {ctl:Px n}（紧凑档乘 0.7）或走 Layout.* 令牌，否则 1366 下几栏加起来会超出工作区。
        var offenders = Directory.EnumerateFiles(Path.Combine(App, "Views"), "*.xaml")
            .SelectMany(path => File.ReadLines(path).Select((line, i) => (path, line, number: i + 1)))
            .Where(l => !l.line.TrimStart().StartsWith("Width=\"1920\"", System.StringComparison.Ordinal)
                        && !l.line.TrimStart().StartsWith("Height=\"1080\"", System.StringComparison.Ordinal))
            .Where(l => Regex.IsMatch(l.line, "\\b(Min|Max)?(Width|Height)=\"[1-9][0-9]{2,}\""))
            .Select(l => Path.GetFileName(l.path) + ":" + l.number)
            .ToList();

        offenders.Should().BeEmpty();
    }
}
