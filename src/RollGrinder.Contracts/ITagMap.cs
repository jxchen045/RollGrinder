using System.Collections.Generic;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Contracts;

/// <summary>
/// 逻辑变量名到机床物理地址的映射，来源为 tagmap.json。
/// 业务代码只使用逻辑名，不得出现物理变量名字面量。
/// </summary>
public interface ITagMap
{
    /// <summary>映射表中的全部变量描述。</summary>
    IReadOnlyList<TagDescriptor> Tags { get; }

    /// <summary>按逻辑名查找，未找到返回 false。</summary>
    bool TryResolve(string logicalName, out TagDescriptor? descriptor);

    /// <summary>按逻辑名查找，未找到抛出 <see cref="GatewayException"/>。</summary>
    TagDescriptor Resolve(string logicalName);
}
