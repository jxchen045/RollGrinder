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
    public void Layout_extensions_are_not_used_where_the_target_type_is_unknown()
    {
        // 模板（DataTemplate、ItemsPanelTemplate……）里的标记扩展拿不到目标属性类型：ByLayout 会把字符串塞给 int 属性，
        // Px 会把 double 塞给 GridLength / DataGridLength——都在建窗口时才抛异常（主窗口建不出来那一次就是这样）。
        // 模板里改用 x:Static（例如 LayoutProfile.MetricColumns）。
        var offenders = Directory.EnumerateFiles(App, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(path => XDocument.Load(path).Descendants()
                .Where(e => e.Ancestors().Any(a => a.Name.LocalName.EndsWith("Template", System.StringComparison.Ordinal)))
                .SelectMany(e => e.Attributes().Select(attribute => (path, element: e.Name.LocalName, value: attribute.Value))))
            .Where(x => x.value.Contains("{ctl:ByLayout", System.StringComparison.Ordinal)
                        || (x.value.Contains("{ctl:Px", System.StringComparison.Ordinal)
                            && (x.element is "ColumnDefinition" or "RowDefinition" || x.element.StartsWith("DataGrid", System.StringComparison.Ordinal))))
            .Select(x => Path.GetFileName(x.path) + ": " + x.element + " = " + x.value)
            .ToList();

        offenders.Should().BeEmpty();
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
