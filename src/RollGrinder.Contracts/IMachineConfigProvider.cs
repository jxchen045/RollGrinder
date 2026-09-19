using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Contracts;

/// <summary>
/// 机床描述与变量映射的来源（machine.json / tagmap.json）。
/// 机床差异全部由配置描述，代码中不得硬编码轴名、行程与阈值。
/// </summary>
public interface IMachineConfigProvider
{
    /// <summary>读取机床描述。</summary>
    Task<MachineDescription> GetMachineAsync(CancellationToken cancellationToken);

    /// <summary>读取变量名映射。</summary>
    Task<ITagMap> GetTagMapAsync(CancellationToken cancellationToken);
}
