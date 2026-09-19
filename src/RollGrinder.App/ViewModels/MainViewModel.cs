using System;
using CommunityToolkit.Mvvm.ComponentModel;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.App.ViewModels;

/// <summary>
/// 主界面视图模型。T-01 只展示装配结果，不访问机床。
/// ViewModel 不直接调用网关，后续任务经服务层取快照。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(IAppOptions options, MachineDescription machine, ITagMap tagMap)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(tagMap);

        MachineDisplayName = machine.DisplayName;
        MachineId = machine.MachineId;
        GatewayName = options.Gateway.ToString();
        ConfigDirectory = options.ConfigDirectory;
        DataDirectory = options.DataDirectory;
        LogDirectory = options.LogDirectory;
        TagCount = tagMap.Tags.Count;
    }

    public string MachineDisplayName { get; }

    public string MachineId { get; }

    public string GatewayName { get; }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    public string LogDirectory { get; }

    public int TagCount { get; }
}
