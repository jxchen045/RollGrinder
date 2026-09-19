using System.Collections.Generic;

namespace RollGrinder.Core.Profiles;

/// <summary>辊形类型注册表。</summary>
public sealed class RollProfileTypeRegistry : KeyedRegistry<IRollProfileType>
{
    public RollProfileTypeRegistry(IEnumerable<IRollProfileType> profileTypes)
        : base(profileTypes, profileType => profileType.Key)
    {
    }
}
