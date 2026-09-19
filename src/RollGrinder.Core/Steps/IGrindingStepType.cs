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

    /// <summary>把参数展开成执行计划（半径量 mm）。</summary>
    GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters);
}
