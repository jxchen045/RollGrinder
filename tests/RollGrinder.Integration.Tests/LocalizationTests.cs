using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Calibration;
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

    /// <summary>
    /// 界面上能选到的全部工序类型。
    ///
    /// 用反射从 Core 里捞，而不是手写一份名单：手写的名单会忘记更新，
    /// 新加一种工序时文案守卫就悄悄漏掉它——这正是架构约束 ④ 要挡的事。
    /// </summary>
    private static IGrindingStepType[] AllStepTypes() =>
        typeof(IGrindingStepType).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsPublic: true }
                && typeof(IGrindingStepType).IsAssignableFrom(type))
            .Select(type => (IGrindingStepType)Activator.CreateInstance(type)!)
            .OrderBy(stepType => stepType.Key, StringComparer.Ordinal)
            .ToArray();

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

    /// <summary>
    /// 代码里按"前缀 + 键"拼出来的那些资源键。整串在源码里搜不到，
    /// 所以死文案检查要放过它们——各自另有守卫盯着它们齐不齐。
    /// </summary>
    private static readonly string[] ComposedPrefixes =
    {
        "StepType_", "ProfileType_", "Parameter_", "Choice_", "Unit_", "AxisRole_",
        "JobState_", "StepSlot_", "Option_", "Role_", "Severity_", "Violation_",
        "ChannelState_", "ConnectionState_", "WheelChange_Hint_", "Curve_", "Action_",
    };

    [Fact]
    public void No_resource_key_is_left_behind_by_code_that_no_longer_exists()
    {
        // 死文案不会让界面出错，但两份 resx 要一直同步，翻译的时候也要一条条看过去。
        // 攒着攒着就没人分得清哪条还在用——所以让测试来数。
        // 扫整个 src：文案键不只在界面层出现——服务层把自己那些
        // "…ResourceKey" 常量也定在身边，报警条上显示的就是它们。
        string sourceRoot = Path.Combine(RepositoryLayout.Root, "src");

        var sources = new System.Text.StringBuilder();
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".xaml", StringComparison.Ordinal)))
            {
                continue;
            }

            sources.Append(File.ReadAllText(file));
        }

        string all = sources.ToString();
        string[] dead = NeutralKeys
            .Where(key => !ComposedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(key => !all.Contains(key, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        dead.Should().BeEmpty("这些文案已经没有代码在用了");
    }

    [Fact]
    public void Every_step_slot_has_a_label()
    {
        // 槽名是选工序下拉里的分组头。缺一条，下拉里就是一个 "!StepSlot_xxx!" 的组。
        foreach (string slotKey in StepSlotKeys.Ordered)
        {
            NeutralKeys.Should().Contain(StepSlotKeys.ResourceKeyOf(slotKey));
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
        // 磨削类工序必须给全"两路进给分量 + 变速三件套"，
        // 少一个，界面上就会出现一个没法解释的空格。
        string[] grindingKeys =
        {
            StepTypeKeys.ShortStroke, StepTypeKeys.Rough, StepTypeKeys.SemiFinish,
            StepTypeKeys.Finish, StepTypeKeys.Polish,
        };

        foreach (IGrindingStepType stepType in AllStepTypes().Where(type => grindingKeys.Contains(type.Key)))
        {
            string[] declared = stepType.Schema.Descriptors.Select(descriptor => descriptor.Key).ToArray();

            declared.Should().Contain(StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin, stepType.Key);
            declared.Should().Contain(StepParameterKeys.InfeedPerPassDiameterMicrometer, stepType.Key);
            declared.Should().Contain(StepParameterKeys.SpeedVariationTarget, stepType.Key);
            declared.Should().Contain(StepParameterKeys.SpeedVariationPercent, stepType.Key);
            declared.Should().Contain(StepParameterKeys.SpeedVariationPeriodRevolutions, stepType.Key);
            declared.Should().Contain(StepParameterKeys.WheelSurfaceSpeedMPerSec, stepType.Key);
            declared.Should().Contain(StepParameterKeys.ReversalDwellSeconds, stepType.Key);
        }
    }

    [Fact]
    public void Every_calibration_value_has_a_label_and_every_option_has_one_too()
    {
        // 设置页的参数格完全由 schema 生成，缺一条文案现场就看到 wheelDiameterMm 这种原始键。
        foreach (ParameterDescriptor descriptor in MachineCalibration.Schema.Descriptors)
        {
            NeutralKeys.Should().Contain(descriptor.ResourceKey, $"标定值 {descriptor.Key} 需要界面文案");

            foreach (string option in descriptor.AllowedValues ?? Array.Empty<string>())
            {
                NeutralKeys.Should().Contain(descriptor.ChoiceResourceKey(option));
            }
        }
    }

    [Fact]
    public void Every_unit_that_a_built_in_parameter_uses_has_a_label()
    {
        IEnumerable<ParameterDescriptor> descriptors = MachineCalibration.Schema.Descriptors
            .Concat(AllStepTypes().SelectMany(type => type.Schema.Descriptors));

        foreach (ParameterUnit unit in descriptors.Select(descriptor => descriptor.Unit).Distinct())
        {
            NeutralKeys.Should().Contain("Unit_" + unit, $"单位 {unit} 需要界面文案");
        }
    }

    /// <summary>
    /// XAML 里的 {StaticResource X} 是**运行期**才解析的：键写错了编译照过，
    /// 到现场才崩。这里把所有 XAML 扫一遍，逐个对着 Themes 里声明的键核。
    /// </summary>
    [Fact]
    public void Every_static_resource_key_used_in_xaml_is_declared()
    {
        string appDirectory = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App");
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var used = new Dictionary<string, string>(StringComparer.Ordinal);

        var declarationPattern = new Regex(@"x:Key=""([^""]+)""", RegexOptions.Compiled);
        var usagePattern = new Regex(@"\{StaticResource\s+([^}\s,]+)", RegexOptions.Compiled);

        foreach (string file in Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in declarationPattern.Matches(text))
            {
                declared.Add(match.Groups[1].Value);
            }

            foreach (Match match in usagePattern.Matches(text))
            {
                used[match.Groups[1].Value] = Path.GetFileName(file);
            }
        }

        used.Should().NotBeEmpty();

        // WPF 自带的系统键不在我们的主题里声明，排掉。
        string[] missing = used.Keys
            .Where(key => !declared.Contains(key) && !key.StartsWith("{x:Static", StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        missing.Should().BeEmpty(
            "这些键在 XAML 里被引用但没有声明，运行期才会炸：" +
            string.Join(", ", missing.Select(key => $"{key}（{used[key]}）")));
    }

    [Fact]
    public void Every_device_a_step_requires_has_a_label()
    {
        // 缺这个键，诊断页的"机床能力"就会显示 hasEddyCurrentTester 这种原始键，
        // 而校验报出来的"本台机床未配置"也说不清缺的是什么。
        foreach (IGrindingStepType stepType in AllStepTypes())
        {
            if (stepType.RequiredOptionKey is not string option)
            {
                continue;
            }

            NeutralKeys.Should().Contain(
                "Option_" + option, $"工序 {stepType.Key} 要用的装置 {option} 需要界面文案");
        }
    }

    [Fact]
    public void Every_step_type_the_machine_file_can_gate_is_declared_in_the_sample()
    {
        // 工序声明了要用某个装置，machine.sample.json 里就得有这一项（true 或 false 都行），
        // 否则现场拿样例改配置时会漏掉，而漏掉等于"没装"。
        string path = Path.Combine(RepositoryLayout.Root, "config", "machine.sample.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement options = document.RootElement.GetProperty("options");

        foreach (IGrindingStepType stepType in AllStepTypes())
        {
            if (stepType.RequiredOptionKey is not string option)
            {
                continue;
            }

            options.TryGetProperty(option, out _).Should().BeTrue(
                $"machine.sample.json 的 options 缺少 {option}");
        }
    }

    [Fact]
    public void Every_step_type_has_an_nc_code_in_the_sample_machine_file()
    {
        // 少一个代码，下发时就抛 GatewayException——宁可现在红，别到现场才红。
        string path = Path.Combine(RepositoryLayout.Root, "config", "machine.sample.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement codes = document.RootElement.GetProperty("stepTypeCodes");

        foreach (IGrindingStepType stepType in AllStepTypes())
        {
            codes.TryGetProperty(stepType.Key, out _).Should().BeTrue(
                $"machine.sample.json 的 stepTypeCodes 缺少 {stepType.Key}");
        }
    }

    [Fact]
    public void Every_manual_action_button_has_a_label()
    {
        // 手动页的 26 个按钮，字都在 resx 里——少一个，现场按钮上就是 "!Action_xxx!"。
        foreach (Services.Manual.ManualCommandDescriptor command in Services.Manual.ManualCommandCatalog.All)
        {
            NeutralKeys.Should().Contain(command.ResourceKey, $"动作 {command.Key} 需要界面文案");
        }
    }

    [Fact]
    public void Every_reason_a_manual_action_can_be_refused_has_a_label()
    {
        string[] reasons =
        {
            Services.Manual.ManualCommandService.NotMappedResourceKey,
            Services.Manual.ManualCommandService.ChannelBusyResourceKey,
            Services.Manual.ManualCommandService.DisconnectedResourceKey,
            Services.Manual.ManualCommandService.WriteFailedResourceKey,
        };

        foreach (string key in reasons)
        {
            NeutralKeys.Should().Contain(key);
        }
    }

    [Fact]
    public void Every_program_step_switch_has_a_label()
    {
        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All)
        {
            NeutralKeys.Should().Contain(option.ResourceKey, $"程序步骤 {option.Key} 需要界面文案");
        }
    }

    [Fact]
    public void Every_prerequisite_a_program_step_needs_is_declared_in_the_sample()
    {
        // 开关声明了要用某个装置或某根轴，machine.sample.json 里就得有——
        // 漏掉等于"没装"，现场拿样例改配置时会莫名其妙少几个开关。
        string path = Path.Combine(RepositoryLayout.Root, "config", "machine.sample.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;

        JsonElement options = root.GetProperty("options");
        string[] axisRoles = root.GetProperty("axes")
            .EnumerateArray()
            .Select(axis => axis.GetProperty("role").GetString()!)
            .ToArray();
        string[] quantities = root.GetProperty("measurementChannels")
            .EnumerateArray()
            .Select(channel => channel.GetProperty("quantity").GetString()!)
            .ToArray();

        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All)
        {
            if (option.RequiredMachineOption is string machineOption)
            {
                options.TryGetProperty(machineOption, out _).Should().BeTrue(
                    $"machine.sample.json 的 options 缺少 {machineOption}");
                NeutralKeys.Should().Contain("Option_" + machineOption);
            }

            if (option.RequiredAxisRole is string role)
            {
                axisRoles.Should().Contain(role, $"machine.sample.json 缺少角色为 {role} 的轴");
                NeutralKeys.Should().Contain("AxisRole_" + role);
            }

            if (option.RequiresDiameterMeasurement)
            {
                quantities.Should().Contain(MeasurementQuantities.Diameter);
            }
        }
    }

    [Fact]
    public void Every_program_step_switch_is_mapped_in_the_sample_tag_map()
    {
        string path = Path.Combine(RepositoryLayout.Root, "config", "tagmap.sample.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        HashSet<string> keys = document.RootElement.GetProperty("tags")
            .EnumerateArray()
            .Select(tag => tag.GetProperty("key").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All)
        {
            if (option.IsHmiSide)
            {
                // 打印这两项由上位机做（打印机挂在工控机上），NC 不必知道。
                // 机床真要这一位的话在自己的 tagmap 里加就是，下发时会带上。
                continue;
            }

            keys.Should().Contain(MachineTagKeys.JobOption(option.Key), $"开关 {option.Key} 需要一个下发变量");
        }
    }

    [Fact]
    public void The_domain_and_the_contracts_agree_on_axis_role_names()
    {
        // 领域层不引用 Contracts，所以 RollProfile 这个角色名在两边各写了一次。
        MachineAxisRoleNames.RollProfile.Should().Be(MachineAxisRoles.RollProfile);
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
            Services.Session.UserDirectory.AlreadyExistsResourceKey,
            Services.Session.UserDirectory.PasswordEmptyResourceKey,
            Services.Session.UserDirectory.LastManufacturerResourceKey,
            Services.Session.UserDirectory.NotFoundResourceKey,
        };

        foreach (string key in raised)
        {
            NeutralKeys.Should().Contain(key);
        }
    }
}
