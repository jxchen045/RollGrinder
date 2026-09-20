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

    /// <summary>界面上能选到的全部工序类型，与组合根的注册保持一致。</summary>
    private static IGrindingStepType[] AllStepTypes() => new IGrindingStepType[]
    {
        new StartStepType(), new ShortStrokeStepType(), new RoughGrindingStepType(), new WheelDressStepType(),
        new SemiFinishGrindingStepType(), new FinishGrindingStepType(), new SparkOutStepType(),
        new MeasureStepType(), new PolishStepType(), new ChamferStepType(), new EddyCurrentStepType(),
        new EndStepType(),
    };

    [Fact]
    public void Every_registered_profile_and_step_type_has_a_label()
    {
        var profileTypes = new RollProfileTypeRegistry(new IRollProfileType[]
        {
            new CylindricalProfileType(), new TaperProfileType(), new CrownProfileType(), new CvcProfileType(),
        });
        var stepTypes = new GrindingStepTypeRegistry(AllStepTypes());

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
            .Concat(AllStepTypes().SelectMany(type => type.Schema.Descriptors));

        foreach (ParameterDescriptor descriptor in descriptors)
        {
            NeutralKeys.Should().Contain(descriptor.ResourceKey, $"参数 {descriptor.Key} 需要界面文案");
        }
    }

    [Fact]
    public void Every_choice_option_has_a_label()
    {
        // 选项参数渲染成分段按钮，按钮上的字就是这些键——缺一个，现场按钮上就是 "!Choice_xxx!"。
        foreach (IGrindingStepType stepType in AllStepTypes())
        {
            foreach (ParameterDescriptor descriptor in stepType.Schema.Descriptors)
            {
                if (descriptor.AllowedValues is not { Count: > 0 } options)
                {
                    continue;
                }

                foreach (string option in options)
                {
                    NeutralKeys.Should().Contain(
                        descriptor.ChoiceResourceKey(option),
                        $"参数 {descriptor.Key} 的选项 {option} 需要界面文案");
                }
            }
        }
    }

    [Fact]
    public void Every_step_type_declares_the_parameters_its_kind_of_work_needs()
    {
        // 磨削类工序必须给全"进给方式 + 两个互斥进给量 + 变速三件套"，
        // 少一个，界面上就会出现一个没法解释的空格。
        string[] grindingKeys =
        {
            StepTypeKeys.ShortStroke, StepTypeKeys.Rough, StepTypeKeys.SemiFinish,
            StepTypeKeys.Finish, StepTypeKeys.Polish,
        };

        foreach (IGrindingStepType stepType in AllStepTypes().Where(type => grindingKeys.Contains(type.Key)))
        {
            string[] declared = stepType.Schema.Descriptors.Select(descriptor => descriptor.Key).ToArray();

            declared.Should().Contain(StepParameterKeys.FeedMode, stepType.Key);
            declared.Should().Contain(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, stepType.Key);
            declared.Should().Contain(StepParameterKeys.InfeedPerPassDiameterMicrometer, stepType.Key);
            declared.Should().Contain(StepParameterKeys.SpeedVariationTarget, stepType.Key);
            declared.Should().Contain(StepParameterKeys.SpeedVariationPercent, stepType.Key);
            declared.Should().Contain(StepParameterKeys.SpeedVariationPeriodSeconds, stepType.Key);
            declared.Should().Contain(StepParameterKeys.WheelSurfaceSpeedMPerSec, stepType.Key);
            declared.Should().Contain(StepParameterKeys.ReversalDwellSeconds, stepType.Key);
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
