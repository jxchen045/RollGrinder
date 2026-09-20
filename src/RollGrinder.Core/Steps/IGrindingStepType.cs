using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Steps;

/// <summary>
/// 一类工序。新增一类工序只写一个实现类并注册。
/// </summary>
public interface IGrindingStepType
{
    /// <summary>类型键。</summary>
    string Key { get; }

    /// <summary>本类型的参数定义（界面量）。</summary>
    ParameterSchema Schema { get; }

    /// <summary>
    /// 本工序需要机床装有的选装装置（<see cref="MachineOptionKeys"/> 里的逻辑名）；
    /// null 表示任何机床都能做。没有这项装置时，这道工序连编都不让编进程序，
    /// 更不会被下发下去。
    /// </summary>
    string? RequiredOptionKey => null;

    /// <summary>把参数展开成执行计划（半径量 mm）。</summary>
    GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters);
}
