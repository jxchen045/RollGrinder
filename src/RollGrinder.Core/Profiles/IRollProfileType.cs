using System.Collections.Generic;
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

    /// <summary>
    /// 端部减薄类（锥度）：在顺接辊形里，参数是"端面处比相邻段低多少"，朝哪一端由这段在辊身上的位置定——
    /// 段中点在头架半边（含正中）朝头架端面，在尾架半边朝尾架端面。段上的"镜像"标志对它无效。
    /// 叠加辊形（旧格式）不按这个含义算，保持原来的曲线。
    /// </summary>
    bool IsEndRelief => false;

    /// <summary>
    /// 端部减薄类在顺接辊形里的曲线（半径量 mm），段内坐标以段起点为端面：起点最低、终点为 0。
    /// 只对 <see cref="IsEndRelief"/> 为真的类型有意义。
    /// </summary>
    RollProfile CreateEndRelief(RollGeometry segmentGeometry, ParameterSet parameters, int sampleCount) =>
        CreateProfile(segmentGeometry, parameters, sampleCount);

    /// <summary>
    /// schema 管不到、要结合段长才能判断的检查（例如点表有没有从段起点排到段终点）。
    /// 大多数类型没有，默认什么都不报。
    /// </summary>
    IEnumerable<ParameterViolation> ValidateShape(ParameterSet parameters, double segmentLengthMm) =>
        System.Array.Empty<ParameterViolation>();
}
