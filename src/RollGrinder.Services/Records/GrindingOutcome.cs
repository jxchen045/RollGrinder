using System;
using System.Collections.Generic;
using System.Linq;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using RollGrinder.Data.Model;

namespace RollGrinder.Services.Records;

/// <summary>记录页上可切换的四条曲线，对应设计稿 B-Records 下半屏。</summary>
public enum RecordCurveKind
{
    /// <summary>磨前 / 磨后辊形：两条线叠着看这一趟磨掉了什么。</summary>
    BeforeAfterProfile = 0,

    /// <summary>误差曲线：磨后实测相对目标。</summary>
    Deviation = 1,

    /// <summary>圆度：各截面的圆度与偏心。</summary>
    Roundness = 2,

    /// <summary>补偿收敛过程：每一次迭代的最差偏差，看它有没有在往下走。</summary>
    CompensationConvergence = 3,
}

/// <summary>
/// 记录页上的一条曲线。
/// </summary>
/// <param name="LabelResourceKey">这条线叫什么。一张图上可能画两条（磨前/磨后）。</param>
/// <param name="Points">点列。横坐标的含义随曲线变，纵坐标由 <see cref="RecordCurve.ValueUnitResourceKey"/> 标。</param>
public sealed record RecordCurveSeries(
    string LabelResourceKey, IReadOnlyList<(double X, double Y)> Points);

/// <summary>
/// 一张记录曲线图：一条或几条线，外加两根轴的标题。
///
/// 与结果指标一样，是**算出来的**，不另存一份。没有数据时 <see cref="Series"/>
/// 为空，界面照实说"这支辊没有这项数据"——不画一条编出来的线。
/// </summary>
/// <param name="Kind">哪一条曲线。</param>
/// <param name="Series">线。空表示这支辊没有这项数据。</param>
/// <param name="AxisUnitResourceKey">横轴单位的文案键。</param>
/// <param name="ValueUnitResourceKey">纵轴单位的文案键。</param>
public sealed record RecordCurve(
    RecordCurveKind Kind,
    IReadOnlyList<RecordCurveSeries> Series,
    string AxisUnitResourceKey,
    string ValueUnitResourceKey)
{
    /// <summary>这支辊没有这项数据。</summary>
    public static RecordCurve Empty(RecordCurveKind kind) =>
        new(kind, Array.Empty<RecordCurveSeries>(), "Unit_Millimeter", "Unit_Micrometer");

    /// <summary>有没有画得出来的线。</summary>
    public bool HasData => Series.Count > 0 && Series.Any(series => series.Points.Count >= 2);
}

/// <summary>
/// 一支辊磨完之后的结果指标，对应设计稿 B-Records 的"磨削结果"那 12 项。
///
/// 全部是**算出来的**，不另存一份：每一项都能从测量、记录与作业推出来，
/// 存一份等于给自己留一个会和原始数据对不上的副本。
///
/// 每一项都是 <c>double?</c>：算不出来就是 null，界面显示 "--"。
/// **"没量过"与"量出来是 0"是两回事**，填一个 0 会让人以为这支辊量过了。
/// </summary>
/// <param name="PreGrindDiameterHeadMm">磨前直径 头架侧（mm）。</param>
/// <param name="PreGrindDiameterTailMm">磨前直径 尾座侧（mm）。</param>
/// <param name="PostGrindDiameterHeadMm">磨后直径 头架侧（mm）。</param>
/// <param name="PostGrindDiameterTailMm">磨后直径 尾座侧（mm）。</param>
/// <param name="TaperMm">锥度（mm）：磨后两端直径之差。</param>
/// <param name="ProfileRmsMicrometer">辊形误差均方根（直径量 µm）。</param>
/// <param name="RoundnessMicrometer">圆度误差（µm）：各截面里最差的那个。</param>
/// <param name="ConcentricityMicrometer">同轴度（µm）：各截面里最大的偏心。</param>
/// <param name="ActualCrownMm">实际凸度（mm，直径量）：中间比两端粗多少。</param>
/// <param name="WheelDiameterMm">砂轮磨后直径（mm）。</param>
/// <param name="Duration">磨削时长。</param>
/// <param name="CompensationIterations">补偿迭代次数。</param>
public sealed record GrindingOutcome(
    double? PreGrindDiameterHeadMm,
    double? PreGrindDiameterTailMm,
    double? PostGrindDiameterHeadMm,
    double? PostGrindDiameterTailMm,
    double? TaperMm,
    double? ProfileRmsMicrometer,
    double? RoundnessMicrometer,
    double? ConcentricityMicrometer,
    double? ActualCrownMm,
    double? WheelDiameterMm,
    TimeSpan? Duration,
    int CompensationIterations)
{
    /// <summary>一项都算不出来的空结果。</summary>
    public static GrindingOutcome Empty { get; } =
        new(null, null, null, null, null, null, null, null, null, null, null, 0);

    /// <summary>
    /// 把手上有的东西算成 12 项。
    /// </summary>
    /// <param name="record">磨削记录。</param>
    /// <param name="job">这支辊按什么磨的；null 表示追溯不到，辊形相关的几项就算不出来。</param>
    /// <param name="preGrind">磨前测量。</param>
    /// <param name="postGrind">磨后测量。</param>
    /// <param name="roundness">圆度测量。</param>
    /// <param name="targetProfile">目标辊形（已按作业合成）；null 时辊形误差算不出来。</param>
    /// <param name="compensationIterations">这支辊迭代了几次补偿。</param>
    public static GrindingOutcome Create(
        GrindingRecord record,
        RollGeometry? job,
        MeasurementRecord? preGrind,
        MeasurementRecord? postGrind,
        RoundnessMeasurement? roundness,
        RollProfile? targetProfile,
        int compensationIterations)
    {
        ArgumentNullException.ThrowIfNull(record);

        (double? preHead, double? preTail) = EndDiameters(preGrind);
        (double? postHead, double? postTail) = EndDiameters(postGrind);

        return new GrindingOutcome(
            preHead,
            preTail,
            postHead,
            postTail,

            // 锥度是磨后两端之差。磨前的锥度是来料的事，不是这一次磨出来的结果。
            postHead is double head && postTail is double tail ? head - tail : null,
            ProfileRms(postGrind, targetProfile, job),
            Worst(roundness, point => point.RoundnessMicrometer),
            Worst(roundness, point => point.EccentricityMicrometer),
            Crown(postGrind),
            record.WheelDiameterMm,
            record.FinishedAtUtc is null ? null : record.FinishedAtUtc.Value - record.StartedAtUtc,
            compensationIterations);
    }

    /// <summary>
    /// 两端的直径。辊身坐标 0 在操作侧（头架侧），向传动侧（尾座侧）增大，
    /// 所以第一个点是头架侧、最后一个点是尾座侧。
    /// </summary>
    private static (double? Head, double? Tail) EndDiameters(MeasurementRecord? measurement)
    {
        if (measurement is null || measurement.Profile.Points.Count == 0)
        {
            return (null, null);
        }

        IReadOnlyList<MeasurementPoint> points = measurement.Profile.Points;
        return (
            UnitConversion.RadiusMmToDiameterMm(points[0].MeasuredRadiusMm),
            UnitConversion.RadiusMmToDiameterMm(points[^1].MeasuredRadiusMm));
    }

    /// <summary>实测相对目标辊形的均方根偏差（直径量 µm）。</summary>
    private static double? ProfileRms(
        MeasurementRecord? measurement, RollProfile? target, RollGeometry? geometry)
    {
        if (measurement is null || target is null || geometry is null
            || measurement.Profile.Points.Count < 2)
        {
            return null;
        }

        RollProfile deviation = CompensationCalculator.ComputeDeviation(
            measurement.Profile, target, geometry);
        if (deviation.Points.Count == 0)
        {
            return null;
        }

        double sumOfSquares = deviation.Points.Sum(point =>
        {
            double micrometer = UnitConversion.RadiusMmToDiameterMicrometer(point.RadiusOffsetMm);
            return micrometer * micrometer;
        });

        return Math.Sqrt(sumOfSquares / deviation.Points.Count);
    }

    /// <summary>各截面里最差的那个。圆度与同轴度都按"最差处"报，不报平均。</summary>
    private static double? Worst(RoundnessMeasurement? roundness, Func<RoundnessPoint, double> pick)
    {
        if (roundness is null || roundness.Points.Count == 0)
        {
            return null;
        }

        return roundness.Points.Max(pick);
    }

    /// <summary>
    /// 实际凸度（mm，直径量）：辊身中间比两端粗多少。
    ///
    /// 两端取实测点列的首尾，中间取最靠近辊身中点的那个点——
    /// 测点不一定正好落在中点上，硬插值反而把测量噪声放大。
    /// </summary>
    private static double? Crown(MeasurementRecord? measurement)
    {
        if (measurement is null || measurement.Profile.Points.Count < 3)
        {
            return null;
        }

        IReadOnlyList<MeasurementPoint> points = measurement.Profile.Points;
        double centreMm = (points[0].BodyPositionMm + points[^1].BodyPositionMm) / 2.0;

        MeasurementPoint middle = points[0];
        double best = double.MaxValue;
        foreach (MeasurementPoint point in points)
        {
            double distance = Math.Abs(point.BodyPositionMm - centreMm);
            if (distance < best)
            {
                best = distance;
                middle = point;
            }
        }

        double endsRadiusMm = (points[0].MeasuredRadiusMm + points[^1].MeasuredRadiusMm) / 2.0;
        return UnitConversion.RadiusMmToDiameterMm(middle.MeasuredRadiusMm - endsRadiusMm);
    }
}
