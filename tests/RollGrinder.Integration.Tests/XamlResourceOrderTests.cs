using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// XAML 的 StaticResource 必须"先定义、后引用"，而且只能看见应用级字典与自己文件里的键。
/// 这类错误编译不报，要到那个控件第一次渲染才抛——曾经就因为 ValueTextBox 样式写在
/// 参数格模板后面，一点"补偿设置"跳到工序编程页就崩。
/// 开发环境跑不起 WPF 界面，所以用静态扫描把这条规则钉住。
/// </summary>
public sealed partial class XamlResourceOrderTests
{
    private static readonly string AppDirectory =
        Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App");

    [Fact]
    public void Every_static_resource_is_defined_before_it_is_used()
    {
        var problems = new List<string>();
        var applicationKeys = new HashSet<string>(StringComparer.Ordinal);

        // 应用级字典：按 App.xaml 的合并顺序，每个字典只能看见它自己前面的键与更早合并的字典。
        string appXaml = File.ReadAllText(Path.Combine(AppDirectory, "App.xaml"));
        foreach (Match source in MergedSource().Matches(appXaml))
        {
            string relative = source.Groups[1].Value.Split(";component/").Last().TrimStart('/');
            string path = Path.Combine(AppDirectory, relative);
            Check(path, applicationKeys, problems);
            applicationKeys.UnionWith(Definitions(File.ReadAllText(path)).Select(d => d.Key));
        }

        applicationKeys.UnionWith(Definitions(appXaml).Select(d => d.Key));

        foreach (string view in Directory.GetFiles(Path.Combine(AppDirectory, "Views"), "*.xaml"))
        {
            Check(view, applicationKeys, problems);
        }

        problems.Should().BeEmpty("引用的资源必须在它前面定义（或在应用级字典里）");
    }

    private static void Check(string path, IReadOnlySet<string> visible, List<string> problems)
    {
        string text = File.ReadAllText(path);
        IReadOnlyList<(int Position, string Key)> definitions = Definitions(text);

        foreach (Match use in StaticResourceUse().Matches(text))
        {
            string key = use.Groups[1].Value;
            if (visible.Contains(key) || definitions.Any(d => d.Key == key && d.Position < use.Index))
            {
                continue;
            }

            int line = text.AsSpan(0, use.Index).Count('\n') + 1;
            string where = definitions.Any(d => d.Key == key) ? "defined later in the same file" : "not defined";
            problems.Add($"{Path.GetFileName(path)}:{line} {key} ({where})");
        }
    }

    private static IReadOnlyList<(int Position, string Key)> Definitions(string text) =>
        KeyDefinition().Matches(text).Select(m => (m.Index, m.Groups[1].Value)).ToList();

    [GeneratedRegex("x:Key=\"([^\"]+)\"")]
    private static partial Regex KeyDefinition();

    [GeneratedRegex(@"\{StaticResource\s+([^}\s]+)\}")]
    private static partial Regex StaticResourceUse();

    [GeneratedRegex("<ResourceDictionary\\s+Source=\"([^\"]+)\"")]
    private static partial Regex MergedSource();
}
