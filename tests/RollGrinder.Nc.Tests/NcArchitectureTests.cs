using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using RollGrinder.Nc;
using Xunit;

namespace RollGrinder.Nc.Tests;

/// <summary>
/// RollGrinder.Nc 只允许依赖 Core 与 Contracts：NC 程序生成不得触碰设备实现或界面。
/// </summary>
public sealed class NcArchitectureTests
{
    private static readonly string[] AllowedRollGrinderAssemblies =
    {
        "RollGrinder.Core",
        "RollGrinder.Contracts",
    };

    private static readonly string[] ForbiddenAssemblyPrefixes =
    {
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml",
        "Opc.Ua",
        "OPCFoundation",
    };

    private static Assembly NcAssembly => typeof(NcAssemblyMarker).Assembly;

    [Fact]
    public void Nc_only_references_core_and_contracts_among_project_assemblies()
    {
        List<string> offenders = NcAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("RollGrinder", StringComparison.Ordinal))
            .Where(name => !AllowedRollGrinderAssemblies.Contains(name, StringComparer.Ordinal))
            .ToList();

        offenders.Should().BeEmpty("RollGrinder.Nc 只允许引用 Core 与 Contracts");
    }

    [Fact]
    public void Nc_does_not_reference_wpf_or_opcua()
    {
        List<string> offenders = NcAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        offenders.Should().BeEmpty("RollGrinder.Nc 不得引用 WPF 或 OPC UA 库");
    }
}
