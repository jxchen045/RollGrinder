using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 架构约束 1 的守卫：RollGrinder.Core 不引用任何其他项目，也不引用任何第三方库。
/// 这些用例必须在 CI 中执行，破坏依赖方向时立即失败。
/// </summary>
public sealed class CoreArchitectureTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    {
        // WPF / 界面
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml",
        "CommunityToolkit",
        // OPC UA 客户端库
        "Opc.Ua",
        "OPCFoundation",
        // 其余第三方
        "Serilog",
        "ScottPlot",
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw",
        "Microsoft.Extensions",
    };

    private static Assembly CoreAssembly => typeof(DomainException).Assembly;

    [Fact]
    public void Core_does_not_reference_other_RollGrinder_assemblies()
    {
        IEnumerable<string> referenced = CoreAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("RollGrinder", StringComparison.Ordinal));

        referenced.Should().BeEmpty(
            "RollGrinder.Core 必须独立于其他项目（架构约束 1）");
    }

    [Fact]
    public void Core_does_not_reference_wpf_opcua_or_other_third_party_assemblies()
    {
        List<string> offenders = CoreAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        offenders.Should().BeEmpty(
            "RollGrinder.Core 不得引用 WPF、OPC UA 或任何第三方库（架构约束 1）");
    }

    [Fact]
    public void Core_project_file_declares_no_package_or_project_references()
    {
        string projectPath = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.Core", "RollGrinder.Core.csproj");
        File.Exists(projectPath).Should().BeTrue($"'{projectPath}' 应该存在");

        XDocument project = XDocument.Load(projectPath);
        IEnumerable<string> declared = project
            .Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? element.Name.LocalName);

        declared.Should().BeEmpty(
            "RollGrinder.Core.csproj 不得声明任何 PackageReference / ProjectReference（架构约束 1）");
    }

    [Fact]
    public void Core_only_references_the_base_class_library()
    {
        List<string> referenced = CoreAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                           && !string.Equals(name, "netstandard", StringComparison.Ordinal)
                           && !string.Equals(name, "mscorlib", StringComparison.Ordinal))
            .ToList();

        referenced.Should().BeEmpty("RollGrinder.Core 只允许依赖 BCL");
    }
}
