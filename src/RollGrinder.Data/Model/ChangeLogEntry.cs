using System;

namespace RollGrinder.Data.Model;

/// <summary>
/// 一条改动记录（修改稿 5.8"每次改动写审计"）：谁、何时、改了哪一块的哪一项、从多少改成多少。
/// </summary>
/// <param name="EntryId">记录标识。</param>
/// <param name="ChangedAtUtc">改动时刻（UTC）。</param>
/// <param name="ChangedBy">改动人的用户名。</param>
/// <param name="Area">哪一块：补偿设置、机床配置、标签映射（见 <see cref="ChangeLogAreas"/>）。</param>
/// <param name="Item">哪一项（参数键、配置路径或变量逻辑名，现场数据，不翻译）。</param>
/// <param name="OldValue">原来的值；新增的项为空。</param>
/// <param name="NewValue">新的值；删掉的项为空。</param>
public sealed record ChangeLogEntry(
    string EntryId,
    DateTimeOffset ChangedAtUtc,
    string ChangedBy,
    string Area,
    string Item,
    string? OldValue,
    string? NewValue);

/// <summary>改动记录的"哪一块"。界面按 "ChangeArea_" + 值取文案。</summary>
public static class ChangeLogAreas
{
    public const string Compensation = "compensation";

    public const string MachineConfig = "machineConfig";

    public const string TagMap = "tagMap";

    /// <summary>轧辊计划（目标辊形、磨削程序）：作业里"变更"、台账改计划、多选改计划。</summary>
    public const string RollPlan = "rollPlan";

    /// <summary>台账（登记、作废、导入）。</summary>
    public const string RollLedger = "rollLedger";

    /// <summary>辊形库（保存版本、停用、删除）。</summary>
    public const string ProfileLibrary = "profileLibrary";

    /// <summary>程序库（保存版本、停用、删除）。</summary>
    public const string ProgramLibrary = "programLibrary";
}
