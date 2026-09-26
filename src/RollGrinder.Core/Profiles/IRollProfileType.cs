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

    /// <summary>
    /// 曲线本身关于段中点对称（圆柱、凸度）。对称编辑时只有这种段能跨在辊身中点上当"中间段"。
    /// </summary>
    bool IsSelfSymmetric => false;

    /// <summary>
    /// 能不能参与对称编辑。CVC 这类本身就是左右不对称的辊形不能——含这种段时"对称"开关置灰。
    /// </summary>
    bool SupportsSymmetricEditing => true;
}
