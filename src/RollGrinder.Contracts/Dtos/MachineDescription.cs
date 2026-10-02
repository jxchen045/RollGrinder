using System;
using System.Collections.Generic;
using System.Linq;

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
/// <param name="UseSecurity">是否选择带签名加密的端点；调试期可关，投产必须开。</param>
/// <param name="AutoAcceptUntrustedCertificates">是否自动接受未信任的服务器证书；仅调试期可开。</param>
/// <param name="SessionTimeoutMs">会话超时（ms）。</param>
/// <param name="OperationTimeoutMs">单次读写超时（ms）。</param>
public sealed record ControllerDescription(
    string Kind,
    int ChannelNumber,
    string? EndpointUrl = null,
    bool UseSecurity = true,
    bool AutoAcceptUntrustedCertificates = false,
    int SessionTimeoutMs = 60000,
    int OperationTimeoutMs = 15000);

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
/// <param name="StepTypeCodes">工序类型键到 NC 侧数字代码的映射；新增一类工序只加一条配置。</param>
/// <param name="AuxiliaryActionCodes">
/// 辅助动作工序能挑的机构动作：动作键 → NC 程序认的动作号（问题 Q4）。少于两项时不提供辅助动作工序。
/// </param>
/// <param name="QuickBar">
/// 界面左栏的快捷入口（界面最终稿 D2），按顺序最多 7 个：machine、profile、steps、wheel、library、records、diagnostics、parameters。
/// 不写用默认。
/// </param>
/// <param name="PanelActions">
/// 装在按钮板上的动作（界面最终稿 Q18）：cycleStart、feedHold。登记了的，屏幕上就不放这个键——停止类不依赖上位机。
/// </param>
/// <param name="ManualStrokeMarginMm">手动往复的默认行程在辊身两端各留多少（mm，界面最终稿 M8），不写按 60。</param>
/// <param name="HeadstockRpmByWeight">
/// 按辊重限头架转速（关系设计 V1）：辊重 ≤ 某值时头架最高多少 r/min，按重量升序。为空时作业核对跳过这一项。
/// </param>
public sealed record MachineDescription(
    int SchemaVersion,
    string MachineId,
    string DisplayName,
    ControllerDescription Controller,
    IReadOnlyList<AxisDescription> Axes,
    IReadOnlyList<MeasurementChannelDescription> MeasurementChannels,
    IReadOnlyDictionary<string, bool> Options,
    IReadOnlyDictionary<string, double> Thresholds,
    WorkpieceLimits Workpiece,
    IReadOnlyDictionary<string, int> StepTypeCodes,
    IReadOnlyDictionary<string, int>? AuxiliaryActionCodes = null,
    IReadOnlyList<string>? QuickBar = null,
    IReadOnlyList<string>? PanelActions = null,
    double? ManualStrokeMarginMm = null,
    IReadOnlyList<HeadstockSpeedLimit>? HeadstockRpmByWeight = null)
{
    /// <summary>作业核对：辊形设计长度与辊身长度允许差多少（%），阈值键。不写按 2。</summary>
    public const string LengthTolerancePercentKey = "lengthTolerancePercent";

    /// <summary>作业核对：工件线速度下限（m/min），阈值键。不写不查。</summary>
    public const string MinWorkpieceSurfaceSpeedKey = "minWorkpieceSurfaceSpeedMPerMin";

    /// <summary>作业核对：工件线速度上限（m/min），阈值键。不写不查。</summary>
    public const string MaxWorkpieceSurfaceSpeedKey = "maxWorkpieceSurfaceSpeedMPerMin";

    /// <summary>某阈值；没写为 null。</summary>
    public double? Threshold(string key) => Thresholds.TryGetValue(key, out double value) ? value : null;

    /// <summary>手动往复默认行程两端留量的缺省值（mm）。</summary>
    public const double DefaultManualStrokeMarginMm = 60.0;

    /// <summary>按钮板上的循环启动。</summary>
    public const string PanelCycleStart = "cycleStart";

    /// <summary>按钮板上的暂停（进给保持）。</summary>
    public const string PanelFeedHold = "feedHold";

    /// <summary>这个动作装在按钮板上（屏幕上不放键）。</summary>
    public bool IsOnPanel(string action) =>
        PanelActions?.Any(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase)) == true;
}

/// <summary>按辊重限头架转速表的一行：辊重不超过 <paramref name="MaxWeightKg"/> 时头架最高 <paramref name="MaxRpm"/>。</summary>
/// <param name="MaxWeightKg">辊重上限（kg，含）。</param>
/// <param name="MaxRpm">头架最高转速（r/min）。</param>
public sealed record HeadstockSpeedLimit(double MaxWeightKg, double MaxRpm);

