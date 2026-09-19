using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// 一类辊形。新增一类辊形只写一个实现类并注册，界面、NC 生成器与数据库都不改。
/// 参数用界面量（直径量、微米）声明，实现内部换算成半径量 mm。
/// </summary>
public interface IRollProfileType
{
    /// <summary>类型键，持久化与下发用。</summary>
    string Key { get; }

    /// <summary>本类型的参数定义。</summary>
    ParameterSchema Schema { get; }

    /// <summary>按参数生成目标辊形（半径量 mm）。</summary>
    RollProfile CreateProfile(RollGeometry geometry, ParameterSet parameters, int sampleCount);
}
