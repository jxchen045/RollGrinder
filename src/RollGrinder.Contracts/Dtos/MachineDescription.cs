using System.Collections.Generic;

namespace RollGrinder.Contracts.Dtos;

/// <summary>轴的闭环方式。</summary>
public enum AxisClosedLoopKind
{
    /// <summary>半闭环：电机编码器反馈。</summary>
    SemiClosed = 0,

    /// <summary>全闭环：光栅尺反馈。</summary>
    FullClosed = 1,

    /// <summary>无位置反馈（例如仅调速的主轴）。</summary>
    OpenLoop = 2,
}

/// <summary>
/// 一根轴的描述。轴的有无、行程、闭环方式全部来自 machine.json。
/// </summary>
/// <param name="Name">NC 中的轴名，例如 X、Z、C。</param>
/// <param name="Role">在本机床上的用途标识，例如 InfeedRadius、Carriage。</param>
/// <param name="IsPresent">本台机床是否装有该轴。</param>
/// <param name="ClosedLoop">闭环方式。</param>
/// <param name="MinPositionMm">行程下限（mm，直线轴有效）。</param>
/// <param name="MaxPositionMm">行程上限（mm，直线轴有效）。</param>
/// <param name="MaxFeedMmPerMin">最大进给（mm/min，直线轴有效）。</param>
/// <param name="MaxSpeedRpm">最大转速（r/min，旋转轴有效）。</param>
public sealed record AxisDescription(
    string Name,
    string Role,
    bool IsPresent,
    AxisClosedLoopKind ClosedLoop,
    double? MinPositionMm = null,
    double? MaxPositionMm = null,
    double? MaxFeedMmPerMin = null,
    double? MaxSpeedRpm = null);

/// <summary>
/// 测量通道描述（测径仪、圆度仪等）。
/// </summary>
/// <param name="Name">通道标识。</param>
/// <param name="IsPresent">本台机床是否装有该通道。</param>
/// <param name="Quantity">测量量，例如 Diameter、Roundness。</param>
/// <param name="ResolutionMicrometer">分辨力（µm）。</param>
public sealed record MeasurementChannelDescription(
    string Name,
    bool IsPresent,
    string Quantity,
    double ResolutionMicrometer);

/// <summary>
/// 数控系统描述。
/// </summary>
/// <param name="Kind">系统型号标识，例如 SinumerikOne。</param>
/// <param name="ChannelNumber">通道号。</param>
/// <param name="EndpointUrl">通信端点（OPC UA 时为服务器地址），可为空。</param>
public sealed record ControllerDescription(
    string Kind,
    int ChannelNumber,
    string? EndpointUrl = null);

/// <summary>
/// 可加工辊件的界限。
/// </summary>
/// <param name="MinBodyLengthMm">最小辊身长度（mm）。</param>
/// <param name="MaxBodyLengthMm">最大辊身长度（mm）。</param>
/// <param name="MinDiameterMm">最小直径（mm，界面量）。</param>
/// <param name="MaxDiameterMm">最大直径（mm，界面量）。</param>
/// <param name="MaxWeightKg">最大重量（kg）。</param>
public sealed record WorkpieceLimits(
    double MinBodyLengthMm,
    double MaxBodyLengthMm,
    double MinDiameterMm,
    double MaxDiameterMm,
    double MaxWeightKg);

/// <summary>
/// 一台机床的完整描述，来源为 machine.json。
/// 选件与阈值用字典承载，新增一项只改配置不改代码。
/// </summary>
/// <param name="SchemaVersion">配置结构版本。</param>
/// <param name="MachineId">机床编号。</param>
/// <param name="DisplayName">显示名称。</param>
/// <param name="Controller">数控系统描述。</param>
/// <param name="Axes">轴列表。</param>
/// <param name="MeasurementChannels">测量通道列表。</param>
/// <param name="Options">选件开关，键由现场约定。</param>
/// <param name="Thresholds">阈值，键由现场约定，单位写在键名里。</param>
/// <param name="Workpiece">辊件界限。</param>
public sealed record MachineDescription(
    int SchemaVersion,
    string MachineId,
    string DisplayName,
    ControllerDescription Controller,
    IReadOnlyList<AxisDescription> Axes,
    IReadOnlyList<MeasurementChannelDescription> MeasurementChannels,
    IReadOnlyDictionary<string, bool> Options,
    IReadOnlyDictionary<string, double> Thresholds,
    WorkpieceLimits Workpiece);
