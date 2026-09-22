using System;
using System.Collections.Generic;
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

    /// <summary>
    /// 界面上归到哪个工序槽（<see cref="StepSlotKeys"/>）。
    ///
    /// 只影响选工序时的分组呈现，不参与计算也不下发。默认是"不占槽"：
    /// 一道工序要占掉实机的某个槽，得有人想清楚它属于哪一档工艺。
    /// </summary>
    string SlotKey => StepSlotKeys.Independent;

    /// <summary>
    /// 本类工序要下发给 NC 的**工序专属参数**，按下发顺序列出参数键。
    ///
    /// 各类工序共有的量（道次、进给、转速……）由执行计划带走；只有某一类
    /// 工序才有的量（倒角几何、修整道次、探伤螺距、圆度采样格）走这一路：
    /// 上位机按这个顺序把值摆进工序专属参数块，NC 那一类工序的子程序照同样的
    /// 顺序读。**顺序就是协议**，改了要和 NC 侧一起改。
    ///
    /// 默认为空：一道工序要占用这个块，得先和 NC 侧约定好读法。
    /// </summary>
    IReadOnlyList<string> NcExtraParameterKeys => Array.Empty<string>();

    /// <summary>把参数展开成执行计划（半径量 mm）。</summary>
    GrindingStepPlan CreatePlan(RollGeometry geometry, ParameterSet parameters);
}
