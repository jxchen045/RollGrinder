using System;
using System.Collections.Generic;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;

namespace RollGrinder.Data.Model;

/// <summary>一支辊件。</summary>
/// <param name="RollId">辊件标识（现场编号）。</param>
/// <param name="Code">辊号/图号。</param>
/// <param name="Geometry">几何（半径量）。</param>
/// <param name="Material">材质，可为空。</param>
/// <param name="CreatedAtUtc">建档时刻。</param>
public sealed record RollRecord(
    string RollId,
    string Code,
    RollGeometry Geometry,
    string? Material,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>
    /// 实机"轧辊数据"屏上那几项里，属于**这支辊本身**的部分。
    ///
    /// 全部可空：现场不一定每支辊都登记得齐，逼着填只会让人乱填一个数，
    /// 而一个乱填的重量会让中心架托瓦按错的压力顶上去。
    /// </summary>
    public RollDataSheet Data { get; init; } = RollDataSheet.Empty;
}

/// <summary>
/// 一支辊的登记数据。与几何（长度、直径）分开：几何是算辊形要用的，
/// 这些是吊装、找正、验收要用的。
/// </summary>
/// <param name="GrindStartPositionMm">启磨点坐标（mm，辊身坐标）。</param>
/// <param name="CurveLengthMm">曲线长度（mm）：辊形作用在辊身的哪一段上。</param>
/// <param name="CurveToleranceMicrometer">曲线允许误差（直径量 µm）。</param>
/// <param name="NetWeightKg">轧辊净重（kg）。</param>
/// <param name="HeadBoxWeightKg">头架端轴承箱重（kg）。</param>
/// <param name="TailBoxWeightKg">尾架端轴承箱重（kg）。</param>
public sealed record RollDataSheet(
    double? GrindStartPositionMm,
    double? CurveLengthMm,
    double? CurveToleranceMicrometer,
    double? NetWeightKg,
    double? HeadBoxWeightKg,
    double? TailBoxWeightKg)
{
    /// <summary>一项都没登记。</summary>
    public static RollDataSheet Empty { get; } = new(null, null, null, null, null, null);

    /// <summary>
    /// 吊装总重：净重加两个轴承箱。缺一项就算不出来——
    /// 少算一个轴承箱会让人按偏轻的重量挂吊具。
    /// </summary>
    public double? TotalWeightKg =>
        NetWeightKg is double net && HeadBoxWeightKg is double head && TailBoxWeightKg is double tail
            ? net + head + tail
            : null;
}

/// <summary>作业状态。</summary>
public enum JobState
{
    /// <summary>已编辑，未下发。</summary>
    Draft = 0,

    /// <summary>已下发给 NC。</summary>
    Handed = 1,

    /// <summary>已完成。</summary>
    Completed = 2,

    /// <summary>已放弃。</summary>
    Abandoned = 3,
}

/// <summary>一次磨削记录。</summary>
/// <param name="RecordId">记录标识。</param>
/// <param name="JobId">作业标识。</param>
/// <param name="StartedAtUtc">开始时刻。</param>
/// <param name="FinishedAtUtc">结束时刻，未结束为空。</param>
/// <param name="State">结束时的作业状态。</param>
/// <param name="Note">备注。</param>
public sealed record GrindingRecord(
    string RecordId,
    string JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    JobState State,
    string? Note)
{
    /// <summary>
    /// 收尾时砂轮有多大（mm）。null 表示那一次没记下来。
    ///
    /// 砂轮天天在磨小，标定值里的"当前砂轮直径"只是此刻的值；
    /// 事后回头查这支辊是用多大的砂轮磨的，只能靠当时记下来。
    /// </summary>
    public double? WheelDiameterMm { get; init; }
}

/// <summary>一次测量。</summary>
/// <param name="MeasurementId">测量标识。</param>
/// <param name="JobId">作业标识。</param>
/// <param name="RecordedAtUtc">测量时刻。</param>
/// <param name="Source">来源，例如测量通道名或 "manual"。</param>
/// <param name="Profile">测量结果。</param>
public sealed record MeasurementRecord(
    string MeasurementId,
    string JobId,
    DateTimeOffset RecordedAtUtc,
    string Source,
    MeasuredProfile Profile)
{
    /// <summary>
    /// 这一次是磨前、磨中还是磨后量的。
    ///
    /// 光有 <see cref="Source"/> 分不出来："gauge" 磨前磨后都是同一个测头。
    /// 而磨前直径、锥度这些指标全靠这一项才算得出来。
    /// </summary>
    public MeasurementStage Stage { get; init; } = MeasurementStage.PostGrind;
}

/// <summary>一次测量是在什么时候量的。</summary>
public enum MeasurementStage
{
    /// <summary>磨前：来料什么样。</summary>
    PreGrind = 0,

    /// <summary>磨中：某一道工序之后的中间测量，用来算行程间补偿。</summary>
    InProcess = 1,

    /// <summary>磨后：磨成了什么样。默认是它——补偿与报表看的都是这一份。</summary>
    PostGrind = 2,
}

/// <summary>圆度测量上的一个点：一个辊身位置上的圆度与偏心。</summary>
/// <param name="BodyPositionMm">辊身坐标（mm）。</param>
/// <param name="RoundnessMicrometer">圆度（µm，峰谷值）。</param>
/// <param name="EccentricityMicrometer">偏心量（µm，全跳动）。</param>
public sealed record RoundnessPoint(
    double BodyPositionMm, double RoundnessMicrometer, double EccentricityMicrometer);

/// <summary>
/// 一次圆度测量。
///
/// 与辊形测量分开存：辊形测量一个位置上是一个半径，圆度测量一个位置上是
/// 圆度与偏心两个数，量的也不是同一件事（一个沿轴线扫，一个绕圆周扫）。
/// 塞进同一张表会让"这一行到底是什么"变成要靠 source 猜。
/// </summary>
/// <param name="RoundnessId">测量标识。</param>
/// <param name="JobId">作业标识。</param>
/// <param name="RecordedAtUtc">测量时刻。</param>
/// <param name="Source">来源，例如测量通道名。</param>
/// <param name="Points">各个截面上的读数，按辊身坐标从小到大。</param>
public sealed record RoundnessMeasurement(
    string RoundnessId,
    string JobId,
    DateTimeOffset RecordedAtUtc,
    string Source,
    IReadOnlyList<RoundnessPoint> Points);

/// <summary>一次补偿。</summary>
/// <param name="CompensationId">补偿标识。</param>
/// <param name="JobId">作业标识。</param>
/// <param name="CreatedAtUtc">生成时刻。</param>
/// <param name="BasedOnMeasurementId">依据的测量标识。</param>
/// <param name="Points">补偿曲线（半径量 mm）。</param>
public sealed record CompensationRecord(
    string CompensationId,
    string JobId,
    DateTimeOffset CreatedAtUtc,
    string? BasedOnMeasurementId,
    IReadOnlyList<ProfilePoint> Points);

/// <summary>一条归档报警。</summary>
/// <param name="AlarmId">序号。</param>
/// <param name="RaisedAtUtc">发生时刻。</param>
/// <param name="Severity">级别（与服务层 AlarmSeverity 数值一致）。</param>
/// <param name="MessageResourceKey">资源键。</param>
/// <param name="Detail">技术细节。</param>
/// <param name="Code">报警号。</param>
public sealed record AlarmRecord(
    long AlarmId,
    DateTimeOffset RaisedAtUtc,
    int Severity,
    string MessageResourceKey,
    string? Detail,
    int Code);
