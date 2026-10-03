using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.App.SelfTest;

/// <summary>自检范围。</summary>
public enum SelfTestScope
{
    /// <summary>逐模块全功能 + 全流程。</summary>
    Full = 0,

    /// <summary>只把每一页、每一层子视图渲染一遍并截图（用于换语言、换分辨率时快速过一遍）。</summary>
    Render = 1,
}

/// <summary>
/// 界面自检的命令行参数：
///   --selftest                  以自检模式启动：自动登录、逐页逐键操作、记录结果后退出
///   --selftest-out &lt;dir&gt;     结果目录（日志、截图、导出文件），默认 &lt;数据目录&gt;/selftest
///   --selftest-scope full|render
///   --selftest-label &lt;name&gt;  这一轮的名字，写进日志，便于区分 sim / offline / en-US 几轮
///   --selftest-shots fail|key|all  截图策略，默认 key（失败 + 关键画面；画面没变的不重复存）
///   --selftest-max-shots &lt;n&gt;   一轮最多存几张，默认 120
///   --selftest-jpeg-quality &lt;q&gt; JPEG 质量 30–95，默认 70
///
/// 自检会点"启动""复位"、建用户、改口令、写记录——所以有两道闸，见 <see cref="Refuse"/>。
/// </summary>
/// <param name="Enabled">是否自检模式。</param>
/// <param name="OutputDirectory">结果目录；为 null 时由调用方按数据目录给默认值。</param>
/// <param name="Scope">范围。</param>
/// <param name="Label">这一轮的名字。</param>
/// <summary>
/// 截图策略。截图是测试包体积的大头（一张 1920×1080 约 150–300 KB），默认只截关键画面。
/// </summary>
public enum ScreenshotPolicy
{
    /// <summary>只截失败的步骤。</summary>
    Failures = 0,

    /// <summary>失败 + 标了"截图"的步骤与每页 / 子视图 / 菜单态各一张（默认）。</summary>
    Key = 1,

    /// <summary>另外把巡检里每按一个键都截一张（排查具体某个键时用）。</summary>
    All = 2,
}

public sealed record SelfTestOptions(bool Enabled, string? OutputDirectory, SelfTestScope Scope, string Label)
{
    /// <summary>自检专用数据目录里的标记文件：有它才允许往一个非空目录里写。</summary>
    public const string DataMarkerFileName = ".selftest-data";

    /// <summary>默认 JPEG 质量（与 WindowCapture 的默认一致；这个文件也编进不引用 WPF 的测试工程）。</summary>
    public const int DefaultJpegQuality = 70;

    /// <summary>截图策略（--selftest-shots fail|key|all）。</summary>
    public ScreenshotPolicy Shots { get; init; } = ScreenshotPolicy.Key;

    /// <summary>一轮最多存几张截图（--selftest-max-shots）；到顶后只再截失败的步骤。</summary>
    public int MaxShots { get; init; } = 120;

    /// <summary>JPEG 质量（--selftest-jpeg-quality，30–95）。</summary>
    public int JpegQuality { get; init; } = DefaultJpegQuality;

    /// <summary>未开启自检。</summary>
    public static SelfTestOptions Disabled { get; } = new(false, null, SelfTestScope.Full, string.Empty);

    /// <summary>解析命令行；不认识的参数留给别人。</summary>
    /// <exception cref="ArgumentException">参数缺值或取值不认识。</exception>
    public static SelfTestOptions Parse(IReadOnlyList<string> args, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool enabled = false;
        string? output = null;
        SelfTestScope scope = SelfTestScope.Full;
        string label = "selftest";
        ScreenshotPolicy shots = ScreenshotPolicy.Key;
        int maxShots = 120;
        int quality = DefaultJpegQuality;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--selftest":
                    enabled = true;
                    break;

                case "--selftest-out":
                    output = Path.GetFullPath(Value(args, ref i), baseDirectory);
                    break;

                case "--selftest-scope":
                    scope = Value(args, ref i).ToLowerInvariant() switch
                    {
                        "full" => SelfTestScope.Full,
                        "render" => SelfTestScope.Render,
                        string other => throw new ArgumentException(
                            $"--selftest-scope expects full or render, got '{other}'.", nameof(args)),
                    };
                    break;

                case "--selftest-label":
                    label = Value(args, ref i);
                    break;

                case "--selftest-shots":
                    shots = Value(args, ref i).ToLowerInvariant() switch
                    {
                        "fail" or "failures" => ScreenshotPolicy.Failures,
                        "key" => ScreenshotPolicy.Key,
                        "all" => ScreenshotPolicy.All,
                        string other => throw new ArgumentException(
                            $"--selftest-shots expects fail, key or all, got '{other}'.", nameof(args)),
                    };
                    break;

                case "--selftest-max-shots":
                    maxShots = Number(args, ref i, 0, 5000);
                    break;

                case "--selftest-jpeg-quality":
                    quality = Number(args, ref i, 30, 95);
                    break;

                default:
                    break;
            }
        }

        return enabled
            ? new SelfTestOptions(true, output, scope, label) { Shots = shots, MaxShots = maxShots, JpegQuality = quality }
            : Disabled;
    }

    private static int Number(IReadOnlyList<string> args, ref int index, int min, int max)
    {
        string option = args[index];
        string text = Value(args, ref index);
        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value >= min && value <= max
            ? value
            : throw new ArgumentException($"{option} expects a whole number {min}–{max}, got '{text}'.", nameof(args));
    }

    private static string Value(IReadOnlyList<string> args, ref int index)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"Command line option '{args[index]}' requires a value.", nameof(args));
        }

        index++;
        return args[index];
    }

    /// <summary>
    /// 能不能在这个环境里跑自检。返回拒绝理由；null 表示可以。
    ///
    /// 1. 只许对着假机床跑：sim / offline / stub。自检会按"启动"、按"复位"——对着真机床这是事故。
    /// 2. 数据目录必须是自检专用的：不存在、空的、或者带 <see cref="DataMarkerFileName"/>。
    ///    自检会建用户、给 admin 设口令、写磨削记录——绝不能落进现场的库里。
    /// </summary>
    public static string? Refuse(GatewayKind gateway, string dataDirectory)
    {
        if (gateway is not (GatewayKind.Sim or GatewayKind.Offline or GatewayKind.Stub))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Self-test refuses to run against gateway '{gateway}': it presses cycle start and reset. Use --gateway sim, --offline or --stub.");
        }

        if (Directory.Exists(dataDirectory)
            && Directory.EnumerateFileSystemEntries(dataDirectory).Any()
            && !File.Exists(Path.Combine(dataDirectory, DataMarkerFileName)))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Self-test refuses to use data directory '{dataDirectory}': it is not empty and has no {DataMarkerFileName} marker. Point --data at a fresh directory.");
        }

        return null;
    }
}
