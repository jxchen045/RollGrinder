using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// App 不得直接引用 Device 与 Sim：设备实现只经组合根装配。
/// 读程序集元数据而不是加载程序集，这样在非 Windows 的 CI 上也能检查 WPF 目标的 App。
/// </summary>
public sealed class AppAssemblyArchitectureTests
{
    private static readonly string[] ForbiddenReferences =
    {
        "RollGrinder.Device",
        "RollGrinder.Sim",
    };

    [Fact]
    public void App_assembly_does_not_reference_device_or_sim()
    {
        string appAssemblyPath = FindAppAssembly();
        IReadOnlyList<string> references = ReadAssemblyReferences(appAssemblyPath);

        references.Should().NotBeEmpty();
        references.Intersect(ForbiddenReferences, StringComparer.Ordinal).Should().BeEmpty(
            "App 只允许引用 Contracts/Core/Nc/Data/Composition，设备实现由组合根装配");
    }

    [Fact]
    public void App_assembly_references_the_composition_root()
    {
        ReadAssemblyReferences(FindAppAssembly())
            .Should().Contain("RollGrinder.Composition");
    }

    private static string FindAppAssembly()
    {
        string appOutputRoot = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "bin");
        Directory.Exists(appOutputRoot).Should().BeTrue(
            $"'{appOutputRoot}' 应存在；请先 dotnet build 整个解决方案");

        string? assembly = Directory
            .EnumerateFiles(appOutputRoot, "RollGrinder.App.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        assembly.Should().NotBeNull($"在 '{appOutputRoot}' 下未找到 RollGrinder.App.dll");
        return assembly!;
    }

    private static IReadOnlyList<string> ReadAssemblyReferences(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();

        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();
    }
}

/// <summary>
/// 分层守卫：界面以外的工程不得沾 WPF；应用服务不得直连设备实现。
/// </summary>
public sealed class LayeringArchitectureTests
{
    private static readonly string[] WpfAssemblies =
    {
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml",
    };

    public static TheoryData<string> NonUiAssemblies() => new()
    {
        "RollGrinder.Core",
        "RollGrinder.Contracts",
        "RollGrinder.Nc",
        "RollGrinder.Data",
        "RollGrinder.Services",
        "RollGrinder.Composition",
        "RollGrinder.Device",
        "RollGrinder.Sim",
    };

    [Theory]
    [MemberData(nameof(NonUiAssemblies))]
    public void Non_ui_assemblies_do_not_reference_wpf(string assemblyName)
    {
        System.Reflection.Assembly assembly = System.Reflection.Assembly.Load(assemblyName);

        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Intersect(WpfAssemblies, StringComparer.Ordinal)
            .Should().BeEmpty($"{assemblyName} 是非界面工程，不得引用 WPF");
    }

    [Fact]
    public void Application_services_do_not_reference_device_or_simulation_implementations()
    {
        System.Reflection.Assembly services = System.Reflection.Assembly.Load("RollGrinder.Services");

        services.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Intersect(new[] { "RollGrinder.Device", "RollGrinder.Sim" }, StringComparer.Ordinal)
            .Should().BeEmpty("服务层只经 IMachineGateway 访问机床");
    }

    [Fact]
    public void The_domain_stays_free_of_infrastructure()
    {
        System.Reflection.Assembly core = System.Reflection.Assembly.Load("RollGrinder.Core");

        core.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("RollGrinder", StringComparison.Ordinal)
                           || name.StartsWith("Microsoft.Data", StringComparison.Ordinal)
                           || name.StartsWith("Serilog", StringComparison.Ordinal)
                           || name.StartsWith("ScottPlot", StringComparison.Ordinal))
            .Should().BeEmpty("Core 不引用任何其他项目与第三方库");
    }
}
