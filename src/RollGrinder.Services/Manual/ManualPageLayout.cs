using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Manual;

/// <summary>
/// 手动页的一页（修改稿 4、5.6）：一组机构动作，一个动作一个竖向软键；旁边显示这组机构的到位状态。
/// </summary>
/// <param name="Key">页名，界面文案取 "ManualGroup_" + Key。</param>
/// <param name="ActionKeys">这页的动作（手动目录里的键），按竖键从上到下的顺序。</param>
/// <param name="Indicators">这页要看的到位状态。</param>
public sealed record ManualPage(string Key, IReadOnlyList<string> ActionKeys, IReadOnlyList<StatusIndicator> Indicators);

/// <summary>
/// 手动页怎么分页。运动类（点动、手轮）只在机床面板上；屏幕上只留机构动作，
/// 按功能分 6 页，每页不超过 8 个——正好对应右侧 8 个竖向软键。
/// 会让整台机床跑起来的循环（手动磨削、回参考点、各轴归位……）收进"辅助循环 ▸"。
/// </summary>
public static class ManualPageLayout
{
    /// <summary>一页最多几个动作：右侧竖键 8 格。</summary>
    public const int MaxActionsPerPage = 8;

    /// <summary>测量与对中这一页：动作是上位机自己的（采点、归档、记两端），不在手动目录里。</summary>
    public const string MeasureAndCentringKey = "measureCentring";

    public static IReadOnlyList<ManualPage> Pages { get; } = new[]
    {
        new ManualPage(
            "measuringArm",
            new[]
            {
                "outerArm.lower", "outerArm.raise", "innerArm.lower", "innerArm.raise",
                "arms.toRoll", "arms.home", "arms.calibrate", "measurement.sample",
            },
            new[] { MachineStatusCatalog.OuterArm, MachineStatusCatalog.InnerArm }),
        new ManualPage(
            "tailstock",
            new[] { "quill.extend", "quill.retract", "tailstock.forward", "tailstock.backward" },
            new[] { MachineStatusCatalog.Quill, MachineStatusCatalog.Tailstock }),
        new ManualPage(
            "headstock",
            new[]
            {
                "headstock.forward", "headstock.reverse", "headstock.speedUp", "headstock.speedDown",
                "driver.extend", "driver.retract",
            },
            new[] { MachineStatusCatalog.HeadstockForward, MachineStatusCatalog.HeadstockReverse, MachineStatusCatalog.Driver }),
        new ManualPage(
            "softLanding",
            new[]
            {
                "softLanding.headstock.up", "softLanding.headstock.down",
                "softLanding.tailstock.up", "softLanding.tailstock.down", "u1Axis.zero",
            },
            new[] { MachineStatusCatalog.SoftLandingHeadstock, MachineStatusCatalog.SoftLandingTailstock, MachineStatusCatalog.SteadyRest }),
        new ManualPage(
            "wheelCoolant",
            new[] { "wheel.run", "coolant", "wheel.measureDiameter", "cycle.wheelDress" },
            new[] { MachineStatusCatalog.WheelRunning, MachineStatusCatalog.Coolant }),
        new ManualPage(MeasureAndCentringKey, Array.Empty<string>(), Array.Empty<StatusIndicator>()),
    };

    /// <summary>"辅助循环 ▸"里的动作：跑一段 NC 程序的循环，外加"各轴归位"。</summary>
    public static IReadOnlyList<string> AuxiliaryKeys { get; } =
        ManualCommandCatalog.Cycles.Select(command => command.Key).Append("axes.home").ToArray();
}
