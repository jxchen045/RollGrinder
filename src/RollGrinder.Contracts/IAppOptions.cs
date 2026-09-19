using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Contracts;

/// <summary>
/// 进程级运行选项：目录布局与网关选择。由组合根在启动时解析命令行后装配。
/// </summary>
public interface IAppOptions
{
    /// <summary>配置目录，默认 &lt;程序目录&gt;/config，升级时保留。</summary>
    string ConfigDirectory { get; }

    /// <summary>数据目录，默认 &lt;程序目录&gt;/data，升级时保留。</summary>
    string DataDirectory { get; }

    /// <summary>日志目录，默认 &lt;数据目录&gt;/logs。</summary>
    string LogDirectory { get; }

    /// <summary>机床描述文件路径（machine.json）。</summary>
    string MachineConfigFilePath { get; }

    /// <summary>变量名映射文件路径（tagmap.json）。</summary>
    string TagMapFilePath { get; }

    /// <summary>网关实现选择，由组合根按此值装配，业务代码不得读取此值做分支。</summary>
    GatewayKind Gateway { get; }

    /// <summary>是否以打桩模式运行（命令行 --stub）。</summary>
    bool UseStub { get; }
}
