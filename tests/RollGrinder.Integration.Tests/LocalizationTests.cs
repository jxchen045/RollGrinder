using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using RollGrinder.Data.Model;
using RollGrinder.Services.Alarms;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 界面文案守卫：XAML 与视图模型用到的资源键必须真实存在，
/// zh-CN 与 en-US 的键集必须一致——少一个键，现场就会看到 "!Key!"。
/// </summary>
public sealed class LocalizationTests
{
    private static string ResourceDirectory =>
        Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "Resources");

    private static IReadOnlySet<string> LoadKeys(string fileName)
    {
        XDocument document = XDocument.Load(Path.Combine(ResourceDirectory, fileName));
        return document.Root!
            .Elements("data")
            .Select(element => element.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> NeutralKeys => LoadKeys("Strings.resx");

    [Fact]
    public void Chinese_and_english_resources_declare_the_same_keys()
    {
        IReadOnlySet<string> english = LoadKeys("Strings.en-US.resx");

        NeutralKeys.Except(english, StringComparer.Ordinal).Should().BeEmpty("en-US 缺少这些键");
        english.Except(NeutralKeys, StringComparer.Ordinal).Should().BeEmpty("zh-CN 缺少这些键");
    }

    [Fact]
    public void Every_key_used_in_xaml_exists()
    {
        var pattern = new Regex(@"\{loc:Loc\s+([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
        string viewDirectory = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App");

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(viewDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        used.Should().NotBeEmpty();
        used.Except(NeutralKeys, StringComparer.Ordinal).Should().BeEmpty("XAML 引用了不存在的资源键");
    }

    [Fact]
    public void Every_registered_profile_and_step_type_has_a_label()
    {
        var profileTypes = new RollProfileTypeRegistry(new IRollProfileType[]
        {
            new CylindricalProfileType(), new TaperProfileType(), new CrownProfileType(), new CvcProfileType(),
        });
        var stepTypes = new GrindingStepTypeRegistry(new IGrindingStepType[]
        {
            new RoughGrindingStepType(), new FinishGrindingStepType(), new SparkOutStepType(), new MeasureStepType(),
        });

        foreach (IRollProfileType profileType in profileTypes.All)
        {
            NeutralKeys.Should().Contain("ProfileType_" + profileType.Key);
        }

        foreach (IGrindingStepType stepType in stepTypes.All)
        {
            NeutralKeys.Should().Contain("StepType_" + stepType.Key);
        }
    }

    [Fact]
    public void Every_built_in_parameter_has_a_label()
    {
        IEnumerable<ParameterDescriptor> descriptors = new IRollProfileType[]
            {
                new CylindricalProfileType(), new TaperProfileType(), new CrownProfileType(), new CvcProfileType(),
            }
            .SelectMany(type => type.Schema.Descriptors)
            .Concat(new IGrindingStepType[]
                {
                    new RoughGrindingStepType(), new FinishGrindingStepType(), new SparkOutStepType(), new MeasureStepType(),
                }
                .SelectMany(type => type.Schema.Descriptors));

        foreach (ParameterDescriptor descriptor in descriptors)
        {
            NeutralKeys.Should().Contain(descriptor.ResourceKey, $"参数 {descriptor.Key} 需要界面文案");
        }
    }

    [Fact]
    public void Every_enum_value_shown_on_screen_has_a_label()
    {
        foreach (GatewayConnectionState value in Enum.GetValues<GatewayConnectionState>())
        {
            NeutralKeys.Should().Contain("ConnectionState_" + value);
        }

        foreach (NcChannelState value in Enum.GetValues<NcChannelState>())
        {
            NeutralKeys.Should().Contain("ChannelState_" + value);
        }

        foreach (JobState value in Enum.GetValues<JobState>())
        {
            NeutralKeys.Should().Contain("JobState_" + value);
        }

        foreach (AlarmSeverity value in Enum.GetValues<AlarmSeverity>())
        {
            NeutralKeys.Should().Contain("Severity_" + value);
        }

        foreach (ParameterUnit value in Enum.GetValues<ParameterUnit>())
        {
            NeutralKeys.Should().Contain("Unit_" + value);
        }

        foreach (ParameterViolationKind value in Enum.GetValues<ParameterViolationKind>())
        {
            NeutralKeys.Should().Contain("Violation_" + value);
            NeutralKeys.Should().Contain("Violation_" + value + "_WithLimit");
        }
    }

    [Fact]
    public void Every_alarm_resource_key_raised_by_the_services_exists()
    {
        string[] raised =
        {
            AlarmLog.GatewayFailureResourceKey,
            AlarmLog.DomainFailureResourceKey,
            AlarmLog.UnexpectedFailureResourceKey,
            Services.Monitoring.MachineMonitor.ConnectionLostResourceKey,
            Services.Monitoring.MachineMonitor.ConnectionRestoredResourceKey,
            Services.Jobs.JobDownloadService.HandoverCompletedResourceKey,
            Services.Jobs.JobDownloadService.HandoverNotArchivedResourceKey,
        };

        foreach (string key in raised)
        {
            NeutralKeys.Should().Contain(key);
        }
    }
}
