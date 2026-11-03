using System.Collections.Generic;

namespace RollGrinder.Core.Profiles;

/// <summary>辊形类型注册表。</summary>
public sealed class RollProfileTypeRegistry : KeyedRegistry<IRollProfileType>
{
    public RollProfileTypeRegistry(IEnumerable<IRollProfileType> profileTypes)
        : base(profileTypes, profileType => profileType.Key)
    {
    }

    /// <summary>全部五种曲线类型（与装配时注册的一样），给不经依赖注入的纯计算用。</summary>
    public static RollProfileTypeRegistry CreateDefault() => new(new IRollProfileType[]
    {
        new CylindricalProfileType(), new TaperProfileType(), new CrownProfileType(), new CvcProfileType(), new PointTableProfileType(),
    });
}
