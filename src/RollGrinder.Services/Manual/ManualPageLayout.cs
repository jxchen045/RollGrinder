using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Manual;

/// <summary>
/// 手动动作页的一页（界面最终稿 5.4）：一组机构动作，一个动作一个竖向软键；下面显示这组机构的到位状态。
/// </summary>
/// <param name="Key">页名，界面文案取 "ManualPage_" + Key。</param>
/// <param name="ActionKeys">这页的动作（手动目录里的键），按竖键从上到下的顺序。</param>
/// <param name="Indicators">这页要看的到位状态。</param>
public sealed record ManualPage(string Key, IReadOnlyList<string> ActionKeys, IReadOnlyList<StatusIndicator> Indicators);

/// <summary>
/// 手动动作放在哪（界面最终稿 F2、5.1、5.4）。运动类（点动、手轮）只在手持盒与按钮板上；
/// 屏幕上的机构动作分 4 页，每页不超过 8 个——正好对应右侧 8 个竖向软键：
/// 测量臂 8 个、尾架 4 个、头架拨盘 2 个、托瓦 5 个；另有测量对中一页（上位机自己的动作）。
/// 砂轮启停、冷却液、测砂轮直径、各轴归位在手动磨削页；会跑一段 NC 程序的循环收进"辅助循环 ▸"；
/// 头架正转 / 反转、回参考点在按钮板上，屏幕只显示状态（改到硬件）。
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
            "driver",
            new[] { "driver.extend", "driver.retract" },
            new[] { MachineStatusCatalog.Driver, MachineStatusCatalog.HeadstockForward, MachineStatusCatalog.HeadstockReverse }),
        new ManualPage(
            "steadyRest",
            new[]
            {
                "softLanding.headstock.up", "softLanding.headstock.down",
                "softLanding.tailstock.up", "softLanding.tailstock.down", "u1Axis.zero",
            },
            new[] { MachineStatusCatalog.SoftLandingHeadstock, MachineStatusCatalog.SoftLandingTailstock, MachineStatusCatalog.SteadyRest }),
        new ManualPage(MeasureAndCentringKey, Array.Empty<string>(), Array.Empty<StatusIndicator>()),
    };

    /// <summary>手动磨削页上的机构动作（竖键第 1、3、6 格与第二页）。</summary>
    public static IReadOnlyList<string> GrindingPageKeys { get; } = new[]
    {
        "wheel.run", "coolant", "axes.home", "wheel.measureDiameter", "arms.calibrate",
    };

    /// <summary>"辅助循环 ▸"里的循环：手动磨削、基准标定、砂轮修整、辊对中。</summary>
    public static IReadOnlyList<string> AuxiliaryKeys { get; } = new[]
    {
        "cycle.manualGrinding", "cycle.calibrateDatum", "cycle.wheelDress", "cycle.rollAlign",
    };

    /// <summary>改到硬件的动作：按钮板上有，屏幕上只显示状态（头架正转 / 反转、回参考点）。</summary>
    public static IReadOnlyList<string> PanelOnlyKeys { get; } = new[]
    {
        "headstock.forward", "headstock.reverse", "cycle.referencePoint",
    };
}
