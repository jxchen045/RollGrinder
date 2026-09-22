using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 一个程序步骤开关：自动磨削之前要不要做这件辅助工作。
///
/// 与"工序"的区别：工序是排在序列里、有先后、有时长的一道活；
/// 程序步骤是整支程序的取舍，NC 程序按它决定要不要走那段子程序。
/// </summary>
/// <param name="Key">开关键，NC 侧按 <c>job.option.&lt;Key&gt;</c> 取。</param>
/// <param name="DefaultEnabled">新建程序时的默认值。</param>
/// <param name="RequiredMachineOption">需要的选装装置（machine.json 的 options），null 表示不需要。</param>
/// <param name="RequiredAxisRole">需要的轴角色（machine.json 的 axes[].role），null 表示不需要。</param>
/// <param name="RequiresDiameterMeasurement">是否需要直径测量通道。</param>
/// <param name="IsHmiSide">
/// 这件事由上位机做，不是 NC 走的子程序（打印就是：打印机挂在工控机上）。
/// 开关照样跟着作业走、照样下发（机床映射了就能看见），但真正做事的是上位机。
/// </param>
public sealed record ProgramOptionDescriptor(
    string Key,
    bool DefaultEnabled,
    string? RequiredMachineOption = null,
    string? RequiredAxisRole = null,
    bool RequiresDiameterMeasurement = false,
    bool IsHmiSide = false)
{
    /// <summary>界面文案的资源键，与参数共用 "Parameter_" 前缀。</summary>
    public string ResourceKey => "Parameter_" + Key;

    /// <summary>这个开关是不是有前置条件。</summary>
    public bool HasRequirement =>
        RequiredMachineOption is not null || RequiredAxisRole is not null || RequiresDiameterMeasurement;
}

/// <summary>程序步骤开关的键。</summary>
public static class ProgramOptionKeys
{
    /// <summary>轧辊磨前测量。</summary>
    public const string PreGrindMeasure = "preGrindMeasure";

    /// <summary>测量安装误差（A/B 两个测头比对）。</summary>
    public const string MountingErrorMeasure = "mountingErrorMeasure";

    /// <summary>轴线前馈补偿。</summary>
    public const string AxisFeedForward = "axisFeedForward";

    /// <summary>U1 轴自动调平。</summary>
    public const string U1AutoLevel = "u1AutoLevel";

    /// <summary>砂轮自动趋近。</summary>
    public const string WheelAutoApproach = "wheelAutoApproach";

    /// <summary>允许在线测量。</summary>
    public const string InProcessMeasure = "inProcessMeasure";

    /// <summary>轧辊磨后测量。</summary>
    public const string PostGrindMeasure = "postGrindMeasure";

    /// <summary>涡流探伤。</summary>
    public const string EddyCurrentTest = "eddyCurrentTest";

    /// <summary>下发之后打一张磨前工艺单。</summary>
    public const string PrintPreGrindData = "printPreGrindData";

    /// <summary>收尾之后打一张磨削报告。</summary>
    public const string PrintPostGrindData = "printPostGrindData";
}

/// <summary>
/// 程序步骤开关的目录，对应 docs/design/B-Steps-工序编程.html 的"程序步骤（自动磨削前取舍）"。
/// 顺序就是设计稿上的顺序。
/// </summary>
public static class ProgramOptionCatalog
{
    /// <summary>十个开关，与实机的程序步骤一一对应。</summary>
    public static IReadOnlyList<ProgramOptionDescriptor> All { get; } = new[]
    {
        new ProgramOptionDescriptor(
            ProgramOptionKeys.PreGrindMeasure, DefaultEnabled: true, RequiresDiameterMeasurement: true),

        new ProgramOptionDescriptor(
            ProgramOptionKeys.MountingErrorMeasure,
            DefaultEnabled: true,
            RequiredMachineOption: MachineOptionKeys.DualProbeMeasurement),

        new ProgramOptionDescriptor(
            ProgramOptionKeys.AxisFeedForward,
            DefaultEnabled: true,
            RequiredAxisRole: MachineAxisRoleNames.CrownAdjust),

        new ProgramOptionDescriptor(
            ProgramOptionKeys.U1AutoLevel,
            DefaultEnabled: false,
            RequiredMachineOption: MachineOptionKeys.U1Leveling),

        new ProgramOptionDescriptor(
            ProgramOptionKeys.WheelAutoApproach,
            DefaultEnabled: true,
            RequiredMachineOption: MachineOptionKeys.ContactDetection),

        new ProgramOptionDescriptor(
            ProgramOptionKeys.InProcessMeasure, DefaultEnabled: true, RequiresDiameterMeasurement: true),

        new ProgramOptionDescriptor(
            ProgramOptionKeys.PostGrindMeasure, DefaultEnabled: true, RequiresDiameterMeasurement: true),

        new ProgramOptionDescriptor(
            ProgramOptionKeys.EddyCurrentTest,
            DefaultEnabled: false,
            RequiredMachineOption: MachineOptionKeys.EddyCurrentTester),

        // 打印默认关着：要有纸有墨、要有人去拿。现场要打，勾上就是。
        // 这两件事由上位机做（打印机挂在工控机上），不是 NC 走的子程序。
        new ProgramOptionDescriptor(
            ProgramOptionKeys.PrintPreGrindData, DefaultEnabled: false, IsHmiSide: true),

        new ProgramOptionDescriptor(
            ProgramOptionKeys.PrintPostGrindData, DefaultEnabled: false, IsHmiSide: true),
    };

    /// <summary>十个开关的参数定义。持久化、校验与默认值都走它，和工艺参数同一套机制。</summary>
    public static ParameterSchema Schema { get; } = new(
        All.Select(option => ParameterDescriptor.Boolean(option.Key, option.DefaultEnabled)));

    /// <summary>默认的一份开关取值。</summary>
    public static ParameterSet Defaults => Schema.CreateDefaults();

    /// <summary>按键取定义。</summary>
    public static ProgramOptionDescriptor Get(string key) =>
        All.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.Ordinal))
        ?? throw new DomainException($"Unknown program option '{key}'.");
}
