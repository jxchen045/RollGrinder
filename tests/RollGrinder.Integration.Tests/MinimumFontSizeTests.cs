using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 界面上最小的字不小于 15 DIP。
///
/// 触摸屏装在机床操作台上，人站着隔一臂看；13、14 号字在 4K@200% 上只有两毫米多高，
/// 第二轮现场截图里工序矩阵的序号、状态角标都得凑近才认得出。
/// 字号要么写字面量（≥15），要么引用 Typography.xaml 里的 Size.*——那边的最小档同样受这条约束。
/// </summary>
public sealed class MinimumFontSizeTests
{
    private const double Minimum = 15.0;

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void No_literal_font_size_below_the_minimum()
    {
        string appRoot = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App");
        var offenders = Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(path => File.ReadLines(path).Select((line, i) => (path, line, number: i + 1)))
            .SelectMany(l => Regex.Matches(l.line, "FontSize=\"(?<v>[0-9.]+)\"")
                .Select(m => (l.path, l.number, value: double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture))))
            .Where(m => m.value < Minimum)
            .Select(m => Path.GetFileName(m.path) + ":" + m.number + " = " + m.value)
            .ToList();

        offenders.Should().BeEmpty("字号不得小于 {0} DIP，请改用 Size.Caption 或更大的档", Minimum);
    }

    [Fact]
    public void Every_size_token_meets_the_minimum()
    {
        string path = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "Themes", "Typography.xaml");
        var sizes = XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "Double" && ((string?)e.Attribute(X + "Key"))?.StartsWith("Size.") == true)
            .Select(e => ((string)e.Attribute(X + "Key")!, double.Parse(e.Value, CultureInfo.InvariantCulture)))
            .ToList();

        sizes.Should().NotBeEmpty();
        sizes.Where(s => s.Item2 < Minimum).Should().BeEmpty();
    }
}
