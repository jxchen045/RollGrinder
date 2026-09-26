using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Profiles;

/// <summary>
/// 点表辊形（阶段 1，问题 Q8 的决定）：表格录入 Z 与直径偏差，按选定的方式插值。
///
/// 点的 Z 是**段内坐标**（从本段起点量起，mm），段挪到哪儿点跟着走；
/// 第一个点在 0、最后一个点在段长上。直径偏差是界面量（直径、µm），内部换成半径量 mm。
/// 可以从 CSV 导入（见 <see cref="PointTableCsv"/>），格式和"生成点列"导出的一样。
/// </summary>
public sealed class PointTableProfileType : IRollProfileType
{
    public const string PointsKey = "points";
    public const string InterpolationKey = "interpolation";
    public const string SmoothingKey = "smoothing";

    /// <summary>至少几个点。两个点就是一条直线。</summary>
    public const int MinimumPoints = 2;

    /// <summary>段两端与第一个 / 最后一个点差多少 mm 以内算对上。</summary>
    public const double CoverToleranceMm = 0.01;

    public string Key => ProfileTypeKeys.PointTable;

    public ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Points(PointsKey, ParameterUnit.Micrometer),
        ParameterDescriptor.Choice(
            InterpolationKey,
            Enum.GetNames<InterpolationMethod>(),
            nameof(InterpolationMethod.ShapePreserving)),
        ParameterDescriptor.Number(SmoothingKey, ParameterUnit.None, 0.5, 0.0, 1.0),
    });

    /// <summary>新插一段点表时的默认点：两端各一个 0，就是一段直线，再按需要加点。</summary>
    public static ParameterSet DefaultsFor(double segmentLengthMm) =>
        new PointTableProfileType().Schema.CreateDefaults()
            .With(PointsKey, ParameterValue.FromPoints(new[] { new TablePoint(0.0, 0.0), new TablePoint(segmentLengthMm, 0.0) }));

    public RollProfile CreateProfile(RollGeometry geometry, ParameterSet parameters, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        IReadOnlyList<TablePoint> points = parameters.Get(PointsKey).Points;
        if (points.Count < MinimumPoints)
        {
            // 点不够就是一段平的；校验会把它报成错误，这里只保证预览不崩。
            return RollProfile.Sample(geometry.BodyLengthMm, sampleCount, _ => 0.0);
        }

        var method = Enum.Parse<InterpolationMethod>(parameters.GetChoice(InterpolationKey));
        Func<double, double> curve = Interpolation.Build(
            method,
            points.Select(point => point.X).ToArray(),
            points.Select(point => UnitConversion.DiameterMicrometerToRadiusMm(point.Y)).ToArray(),
            parameters.GetNumberOrDefault(SmoothingKey, 0.0));

        return RollProfile.Sample(geometry.BodyLengthMm, sampleCount, curve);
    }

    public IEnumerable<ParameterViolation> ValidateShape(ParameterSet parameters, double segmentLengthMm)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.TryGet(PointsKey, out ParameterValue? value) || value is not { Kind: ParameterValueKind.Points })
        {
            yield break;
        }

        IReadOnlyList<TablePoint> points = value.Points;
        if (points.Count < MinimumPoints)
        {
            yield return new ParameterViolation(PointsKey, ParameterViolationKind.TooFewPoints, MinimumPoints);
            yield break;
        }

        for (int i = 1; i < points.Count; i++)
        {
            if (!(points[i].X > points[i - 1].X))
            {
                yield return new ParameterViolation(PointsKey, ParameterViolationKind.PointsNotIncreasing);
                yield break;
            }
        }

        if (Math.Abs(points[0].X) > CoverToleranceMm || Math.Abs(points[^1].X - segmentLengthMm) > CoverToleranceMm)
        {
            yield return new ParameterViolation(PointsKey, ParameterViolationKind.PointsDoNotCoverSegment, segmentLengthMm);
        }
    }
}

/// <summary>
/// 点表 CSV：每行 "Z mm, 直径偏差 µm"，逗号、分号或制表符分隔。
/// 表头、单位行、注释行、空行都跳过——现场的表常带着这些，为这个整份不读不划算。
/// 与辊形页"生成点列"导出的格式相同。
/// </summary>
public static class PointTableCsv
{
    public static IReadOnlyList<TablePoint> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var points = new List<TablePoint>();
        foreach (string line in lines)
        {
            if (TryParse(line, out TablePoint point))
            {
                points.Add(point);
            }
        }

        return points;
    }

    /// <summary>把点挪到从 Z = 0 起（导进来的表常是整根辊身坐标），按 Z 排好。</summary>
    public static IReadOnlyList<TablePoint> StartAtZero(IReadOnlyList<TablePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return points;
        }

        double first = points.Min(point => point.X);
        return points.OrderBy(point => point.X).Select(point => point with { X = point.X - first }).ToArray();
    }

    private static bool TryParse(string line, out TablePoint point)
    {
        point = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] parts = line.Split(',', ';', '\t');
        if (parts.Length < 2
            || !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double z)
            || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double micrometer)
            || !double.IsFinite(z) || !double.IsFinite(micrometer))
        {
            return false;
        }

        point = new TablePoint(z, micrometer);
        return true;
    }
}
