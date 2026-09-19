using System;
using System.Collections.Generic;
using System.IO;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Composition;

/// <summary>
/// <see cref="IAppOptions"/> 的默认实现：默认使用程序目录下的 config/ 与 data/，
/// 允许命令行覆盖。
/// 支持的参数：
///   --stub                 以打桩网关启动（等价于 --gateway stub）
///   --gateway &lt;kind&gt;  opcua | stub | sim | file
///   --config &lt;dir&gt;    配置目录
///   --data &lt;dir&gt;      数据目录
///   --replay &lt;file&gt;   回放指定的 .jsonl 录制文件（隐含 --gateway file）
/// </summary>
public sealed class AppOptions : IAppOptions
{
    public const string MachineConfigFileName = "machine.json";
    public const string TagMapFileName = "tagmap.json";

    private AppOptions(string configDirectory, string dataDirectory, GatewayKind gateway, string? replayFilePath)
    {
        ConfigDirectory = configDirectory;
        DataDirectory = dataDirectory;
        Gateway = gateway;
        ReplayFilePath = replayFilePath;
    }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string MachineConfigFilePath => Path.Combine(ConfigDirectory, MachineConfigFileName);

    public string TagMapFilePath => Path.Combine(ConfigDirectory, TagMapFileName);

    public GatewayKind Gateway { get; }

    public bool UseStub => Gateway == GatewayKind.Stub;

    /// <summary>回放文件路径（--replay）；为空时取 data/replay 下最新的 .jsonl。</summary>
    public string? ReplayFilePath { get; }

    /// <summary>
    /// 解析命令行。<paramref name="baseDirectory"/> 一般传程序目录（AppContext.BaseDirectory）。
    /// </summary>
    /// <exception cref="ArgumentException">参数缺少取值或网关名无法识别。</exception>
    public static AppOptions Parse(IReadOnlyList<string> args, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        string configDirectory = Path.Combine(baseDirectory, "config");
        string dataDirectory = Path.Combine(baseDirectory, "data");
        GatewayKind gateway = GatewayKind.OpcUa;
        string? replayFilePath = null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--stub":
                    gateway = GatewayKind.Stub;
                    break;

                case "--gateway":
                    gateway = ParseGateway(RequireValue(args, ref i, arg));
                    break;

                case "--config":
                    configDirectory = Path.GetFullPath(RequireValue(args, ref i, arg), baseDirectory);
                    break;

                case "--data":
                    dataDirectory = Path.GetFullPath(RequireValue(args, ref i, arg), baseDirectory);
                    break;

                case "--replay":
                    replayFilePath = Path.GetFullPath(RequireValue(args, ref i, arg), baseDirectory);
                    gateway = GatewayKind.File;
                    break;

                default:
                    // 未知参数留给宿主处理（例如 WPF 自身的参数），此处不报错。
                    break;
            }
        }

        return new AppOptions(configDirectory, dataDirectory, gateway, replayFilePath);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"Command line option '{optionName}' requires a value.", nameof(args));
        }

        index++;
        return args[index];
    }

    private static GatewayKind ParseGateway(string value) => value.ToLowerInvariant() switch
    {
        "opcua" => GatewayKind.OpcUa,
        "stub" => GatewayKind.Stub,
        "file" => GatewayKind.File,
        "sim" => GatewayKind.Sim,
        _ => throw new ArgumentException($"Unknown gateway kind '{value}'. Expected opcua, stub, sim or file.", nameof(value)),
    };
}
