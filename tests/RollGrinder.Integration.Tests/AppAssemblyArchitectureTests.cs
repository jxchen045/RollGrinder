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
