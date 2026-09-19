using System.Collections.Generic;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Steps;

namespace RollGrinder.Nc;

/// <summary>
/// 一次下发的完整内容。写入顺序即下发顺序，"参数有效"标志固定在最后一条：
/// NC 只在整组参数写完后才认这份数据，上位机此后被杀也不影响这支辊磨完。
/// </summary>
/// <param name="Writes">按顺序执行的写入。</param>
/// <param name="TargetProfile">下发的目标辊形（已含补偿）。</param>
/// <param name="Plans">各工序展开后的执行计划。</param>
public sealed record NcDownload(
    IReadOnlyList<TagWrite> Writes,
    RollProfile TargetProfile,
    IReadOnlyList<GrindingStepPlan> Plans);
