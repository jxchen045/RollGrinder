using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;

namespace RollGrinder.Services.Library;

/// <summary>对比的哪一项（资源键 "Diff_" + 名字）。</summary>
public enum DiffField
{
    Name = 0,
    NominalLength = 1,
    Tolerance = 2,
    EndZone = 3,
    SegmentCount = 4,
    SegmentType = 5,
    SegmentLength = 6,
    SegmentParameter = 7,
    ApplicableKind = 8,
    StandardStock = 9,
    StepCount = 10,
    StepType = 11,
    StepParameter = 12,
    Range = 13,
}

/// <summary>
/// 对比的一行：哪一项（第几段 / 第几道、哪个参数）、A 的值、B 的值（不变量文本，界面按需本地化类型名）。
/// 一边没有这一项时那边是空串。
/// </summary>
public sealed record DiffRow(DiffField Field, int? Index, string? TypeKey, string? ParameterKey, string A, string B)
{
    public bool Differs => !string.Equals(A, B, StringComparison.Ordinal);
}

/// <summary>对比的一边：一个方案（辊形 + 工艺 + 可调范围 + 端部非工作区）或一条模板（只有辊形或只有工艺）。</summary>
public sealed record CompareSide(
    string Title,
    RollProfileDefinition? Profile,
    GrindingProgram? Program,
    IReadOnlyList<PlanParamRange>? Ranges = null,
    double? EndZoneMm = null);

/// <summary>
/// 方案 / 模板对比（设计稿 v4 5.5.1、5.6.1"对比"）：逐项列出两边的辊形（名义长度、公差、各段类型 / 长度 / 参数）、
/// 工艺（适用、标准余量、各道工序类型与参数，序号按"开始 … 结束"整理后）、可调范围、端部非工作区。
/// 只比结构化的值，不比名字以外的说明文字。
/// </summary>
public static class PlanDiff
{
    public static IReadOnlyList<DiffRow> Compare(CompareSide a, CompareSide b, GrindingStepTypeRegistry stepTypes)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(stepTypes);
        var rows = new List<DiffRow>();

        if (a.Profile is not null || b.Profile is not null)
        {
            ProfileRows(a.Profile, b.Profile, rows);
            if (a.EndZoneMm is not null || b.EndZoneMm is not null)
            {
                rows.Add(new DiffRow(DiffField.EndZone, null, null, null, Number(a.EndZoneMm), Number(b.EndZoneMm)));
            }
        }

        if (a.Program is not null || b.Program is not null)
        {
            ProgramRows(a.Program, b.Program, stepTypes, rows);
        }

        if (a.Ranges is not null || b.Ranges is not null)
        {
            Dictionary<(int, string), string> left = (a.Ranges ?? Array.Empty<PlanParamRange>()).ToDictionary(r => (r.StepOrder, r.ParameterKey), r => r.Rule);
            Dictionary<(int, string), string> right = (b.Ranges ?? Array.Empty<PlanParamRange>()).ToDictionary(r => (r.StepOrder, r.ParameterKey), r => r.Rule);
            foreach ((int step, string key) in left.Keys.Concat(right.Keys).Distinct().OrderBy(k => k.Item1).ThenBy(k => k.Item2, StringComparer.Ordinal))
            {
                rows.Add(new DiffRow(
                    DiffField.Range, step, null, key,
                    left.TryGetValue((step, key), out string? l) ? l : string.Empty,
                    right.TryGetValue((step, key), out string? r) ? r : string.Empty));
            }
        }

        return rows;
    }

    private static void ProfileRows(RollProfileDefinition? a, RollProfileDefinition? b, List<DiffRow> rows)
    {
        rows.Add(new DiffRow(DiffField.Name, null, null, null, a?.Name ?? string.Empty, b?.Name ?? string.Empty));
        rows.Add(new DiffRow(DiffField.NominalLength, null, null, null, Number(a?.BodyLengthMm), Number(b?.BodyLengthMm)));
        rows.Add(new DiffRow(DiffField.Tolerance, null, null, null, Number(a?.ToleranceMicrometer), Number(b?.ToleranceMicrometer)));
        IReadOnlyList<RollProfileSegment> left = a?.Profile.Segments ?? Array.Empty<RollProfileSegment>();
        IReadOnlyList<RollProfileSegment> right = b?.Profile.Segments ?? Array.Empty<RollProfileSegment>();
        rows.Add(new DiffRow(DiffField.SegmentCount, null, null, null, Count(a is null ? null : left.Count), Count(b is null ? null : right.Count)));
        for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            RollProfileSegment? l = i < left.Count ? left[i] : null;
            RollProfileSegment? r = i < right.Count ? right[i] : null;
            rows.Add(new DiffRow(DiffField.SegmentType, i + 1, l?.ProfileTypeKey ?? r?.ProfileTypeKey, null, l?.ProfileTypeKey ?? string.Empty, r?.ProfileTypeKey ?? string.Empty));
            rows.Add(new DiffRow(DiffField.SegmentLength, i + 1, null, null, Number(l?.LengthMm), Number(r?.LengthMm)));
            ParameterRows(DiffField.SegmentParameter, i + 1, l?.ProfileTypeKey ?? r?.ProfileTypeKey, l?.Parameters, r?.Parameters, rows);
        }
    }

    private static void ProgramRows(GrindingProgram? a, GrindingProgram? b, GrindingStepTypeRegistry stepTypes, List<DiffRow> rows)
    {
        rows.Add(new DiffRow(DiffField.Name, null, "program", null, a?.Name ?? string.Empty, b?.Name ?? string.Empty));
        rows.Add(new DiffRow(DiffField.ApplicableKind, null, null, null, a?.ApplicableRollKind.ToString() ?? string.Empty, b?.ApplicableRollKind.ToString() ?? string.Empty));
        rows.Add(new DiffRow(DiffField.StandardStock, null, null, null, Number(a?.StandardStockMicrometer), Number(b?.StandardStockMicrometer)));

        // 序号按作业里的（整理成"开始 … 结束"以后），与可调范围、记录说的"第 n 道"一致。
        IReadOnlyList<GrindingJobStep> left = a is null ? Array.Empty<GrindingJobStep>() : ProgramFrame.Normalize(a.Steps, stepTypes);
        IReadOnlyList<GrindingJobStep> right = b is null ? Array.Empty<GrindingJobStep>() : ProgramFrame.Normalize(b.Steps, stepTypes);
        rows.Add(new DiffRow(DiffField.StepCount, null, null, null, Count(a is null ? null : left.Count), Count(b is null ? null : right.Count)));
        for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            GrindingJobStep? l = i < left.Count ? left[i] : null;
            GrindingJobStep? r = i < right.Count ? right[i] : null;
            string? type = l?.StepTypeKey ?? r?.StepTypeKey;
            rows.Add(new DiffRow(DiffField.StepType, i + 1, type, null, l?.StepTypeKey ?? string.Empty, r?.StepTypeKey ?? string.Empty));

            // 两边同一道是同一种工序才逐个参数比；类型都不一样，比参数没有意义。
            if (l is not null && r is not null && l.StepTypeKey != r.StepTypeKey)
            {
                continue;
            }

            ParameterSet? defaults = type is not null && stepTypes.TryGet(type, out IGrindingStepType? stepType) && stepType is not null
                ? stepType.Schema.CreateDefaults()
                : null;
            ParameterRows(
                DiffField.StepParameter, i + 1, type,
                l is null ? null : defaults?.Merge(l.Parameters) ?? l.Parameters,
                r is null ? null : defaults?.Merge(r.Parameters) ?? r.Parameters,
                rows);
        }
    }

    private static void ParameterRows(DiffField field, int index, string? type, ParameterSet? a, ParameterSet? b, List<DiffRow> rows)
    {
        IEnumerable<string> keys = (a?.Keys ?? Array.Empty<string>()).Concat(b?.Keys ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            rows.Add(new DiffRow(field, index, type, key, Value(a, key), Value(b, key)));
        }
    }

    private static string Value(ParameterSet? set, string key) =>
        set is not null && set.TryGet(key, out ParameterValue? value) && value is not null ? value.ToInvariantString() : string.Empty;

    private static string Number(double? value) =>
        value is double v ? v.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

    private static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
