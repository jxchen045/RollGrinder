using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 字体嵌入的守卫。
///
/// 现场最怕的不是"字难看"，而是**换一台机器字就变了**——
/// 字宽一变，54 px 的实测直径和下面的数据行就对不齐，操作员会以为读数跳了。
/// 所以字体必须编译进程序集，而不是指望工控机装了什么。
/// </summary>
public sealed class EmbeddedFontTests
{
    private static string FontDirectory =>
        Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "Fonts");

    private static readonly string[] RequiredFonts =
    {
        "NotoSansSC-Regular.otf",
        "IBMPlexMono-Regular.ttf",
        "IBMPlexMono-Medium.ttf",
        "IBMPlexMono-SemiBold.ttf",
    };

    [Fact]
    public void Every_font_the_design_calls_for_is_in_the_repository()
    {
        foreach (string fileName in RequiredFonts)
        {
            string path = Path.Combine(FontDirectory, fileName);
            File.Exists(path).Should().BeTrue($"缺字体文件 {fileName}");
            new FileInfo(path).Length.Should().BeGreaterThan(1024, $"{fileName} 看着像个占位文件");
        }
    }

    [Fact]
    public void The_open_font_licences_travel_with_the_fonts()
    {
        // OFL 要求授权文本随字体一同分发，少一份就是分发违规。
        foreach (string licence in new[] { "OFL-IBMPlexMono.txt", "OFL-NotoSansSC.txt" })
        {
            string path = Path.Combine(FontDirectory, licence);
            File.Exists(path).Should().BeTrue($"缺授权文本 {licence}");
            File.ReadAllText(path).Should().Contain("Open Font License");
        }
    }

    [Fact]
    public void The_project_compiles_the_fonts_into_the_assembly()
    {
        string csproj = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "RollGrinder.App.csproj"));

        csproj.Should().Contain(@"<Resource Include=""Fonts\*.ttf"" />");
        csproj.Should().Contain(@"<Resource Include=""Fonts\*.otf"" />");
        csproj.Should().Contain("OFL-*.txt", "授权文本要随程序发布");
    }

    [Fact]
    public void Typography_uses_the_embedded_families_and_keeps_a_fallback()
    {
        string typography = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "Themes", "Typography.xaml"));

        typography.Should().Contain("pack://application:,,,/Fonts/#Noto Sans SC");
        typography.Should().Contain("pack://application:,,,/Fonts/#IBM Plex Mono");

        // 兜底不能删：资源万一加载不出来，界面得还能读，不能变成方块。
        typography.Should().Contain("Microsoft YaHei UI");
        typography.Should().Contain("Consolas");
    }

    [Fact]
    public void The_built_assembly_actually_carries_the_font_data()
    {
        // 只看 csproj 不够——WPF 资源打包是另一回事，得确认字节真的进了程序集。
        byte[] assembly = File.ReadAllBytes(FindAppAssembly());

        IndexOf(assembly, Encoding.ASCII.GetBytes("OTTO")).Should().BeGreaterThan(0,
            "Noto Sans SC 是 CFF 轮廓的 OpenType，签名 OTTO 应当出现在程序集里");
        IndexOf(assembly, Encoding.ASCII.GetBytes("IBM Plex Mono")).Should().BeGreaterThan(0);
        IndexOf(assembly, Encoding.Unicode.GetBytes("fonts/")).Should().BeGreaterThan(0,
            "WPF 把资源路径小写后存成 UTF-16");
    }

    private static string FindAppAssembly()
    {
        string root = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "bin");
        Directory.Exists(root).Should().BeTrue($"'{root}' 应存在；请先 dotnet build");

        string? assembly = Directory
            .EnumerateFiles(root, "RollGrinder.App.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        assembly.Should().NotBeNull();
        return assembly!;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
