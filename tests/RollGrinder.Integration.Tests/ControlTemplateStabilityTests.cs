using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 按钮模板的两条硬规矩，都来自"鼠标停在按钮上闪烁"这次现场问题：
///
/// 1. 模板触发器里不放 Binding。上一版在 IsMouseOver 触发器里用 Binding 取悬停色，
///    取值一落空底色就成了 null，null 底色的区域接不住鼠标 → 悬停撤销 → 底色恢复 → 又接住……
///    每帧来回一次。颜色要随样式变，用 TemplateBinding 绑在叠层上，触发器只切可见性。
/// 2. 可点的模板（Button / ToggleButton）根元素必须有底色（哪怕是 Transparent），
///    保证整块区域无论处于哪种状态都接得住鼠标。
/// </summary>
public sealed class ControlTemplateStabilityTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument[] AllXaml() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Select(path => XDocument.Load(path, LoadOptions.SetBaseUri))
            .ToArray();

    [Fact]
    public void Template_triggers_never_use_bindings()
    {
        var offenders = AllXaml()
            .SelectMany(d => d.Descendants(Wpf + "ControlTemplate.Triggers"))
            .SelectMany(t => t.Descendants(Wpf + "Setter"))
            .Where(s => ((string?)s.Attribute("Value"))?.Contains("{Binding", System.StringComparison.Ordinal) == true
                        || s.Elements().Any(e => e.Name.LocalName.EndsWith("Setter.Value", System.StringComparison.Ordinal)
                                                 && e.Descendants().Any(x => x.Name.LocalName == "Binding")))
            .Select(s => Path.GetFileName(s.Document!.BaseUri) + ": " + (string?)s.Attribute("TargetName") + "." + (string?)s.Attribute("Property"))
            .ToList();

        offenders.Should().BeEmpty("触发器里的 Binding 取值落空会让底色变 null，鼠标在按钮上来回进出而闪烁");
    }

    [Fact]
    public void Clickable_templates_are_hit_testable_everywhere()
    {
        var templates = AllXaml()
            .SelectMany(d => d.Descendants(Wpf + "ControlTemplate"))
            .Where(t => (string?)t.Attribute("TargetType") is "Button" or "ToggleButton")
            .ToList();

        templates.Should().NotBeEmpty();
        foreach (XElement template in templates)
        {
            XElement root = template.Elements().First(e => !e.Name.LocalName.StartsWith("ControlTemplate.", System.StringComparison.Ordinal));
            string? background = (string?)root.Attribute("Background");
            background.Should().NotBeNullOrEmpty(
                "{0} 模板的根 {1} 要有底色（至少 Transparent），否则空白处接不住鼠标", Path.GetFileName(template.Document!.BaseUri), root.Name.LocalName);
            background.Should().NotBe("{x:Null}");
        }
    }
}
