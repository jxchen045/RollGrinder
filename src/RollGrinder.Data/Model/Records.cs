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
    DateTimeOffset CreatedAtUtc);

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
    string? Note);

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
    MeasuredProfile Profile);

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
public sealed record AlarmRecord(
    long AlarmId,
    DateTimeOffset RaisedAtUtc,
    int Severity,
    string MessageResourceKey,
    string? Detail);
