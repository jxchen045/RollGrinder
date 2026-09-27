using System;
using System.Collections.Generic;
using RollGrinder.Core.Steps;

namespace RollGrinder.App.Controls;

/// <summary>工序简图的种类（修改稿 5.3：每种工序一张简图）。</summary>
public enum StepDiagramKind
{
    /// <summary>开始 / 结束：只画一个标记。</summary>
    Marker = 0,

    /// <summary>往复磨削：砂轮沿辊身往复，换向时进给（粗 / 半精 / 精 / 短行程 / 抛光 / 光磨）。</summary>
    Traverse = 1,

    /// <summary>测量：测量臂沿辊身取点。</summary>
    Measure = 2,

    /// <summary>圆度：若干截面，每转取若干点。</summary>
    Roundness = 3,

    /// <summary>倒角：辊身端部的斜坡或圆弧。</summary>
    Chamfer = 4,

    /// <summary>砂轮修整：金刚笔沿砂轮宽度走。</summary>
    WheelDress = 5,

    /// <summary>涡流探伤：探头螺旋扫查。</summary>
    EddyCurrent = 6,

    /// <summary>暂停。</summary>
    Pause = 7,

    /// <summary>辅助动作：开 / 关一个机构。</summary>
    Auxiliary = 8,
}

/// <summary>简图上能被高亮的一个量。</summary>
public enum DiagramElement
{
    Stock,
    InfeedPerPass,
    ContinuousInfeed,
    Feed,
    WorkpieceSpeed,
    WheelSurfaceSpeed,
    ReversalDwell,
    Passes,
    SparkOutPasses,
    InProcessMeasurement,
    SpeedVariation,
    MeasurePoints,
    RoundnessSections,
    RoundnessPoints,
    ChamferLength1,
    ChamferHeight1,
    ChamferLength2,
    ChamferHeight2,
    ChamferKind,
    DressInfeed,
    DressPasses,
    DressFeed,
    ScanPitch,
    AuxAction,
    AuxState,
    PauseReason,
}

/// <summary>
/// 工序类型 → 简图种类、参数 → 简图上的量、量 → 图上标的符号。纯映射，不引用 WPF，可以单测：
/// 保证每道工序的每个参数在它那张图上都有东西可亮——光标落到哪一格，图上就亮哪一处。
/// 图上只标工程符号（ae、f、n、vc……），不写中文；说明、单位、范围在图下面那一行。
/// </summary>
public static class StepDiagramMap
{
    private static readonly Dictionary<string, DiagramElement> ByParameter = new(StringComparer.Ordinal)
    {
        [StepParameterKeys.StockDiameterMicrometer] = DiagramElement.Stock,
        [StepParameterKeys.InfeedPerPassDiameterMicrometer] = DiagramElement.InfeedPerPass,
        [StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin] = DiagramElement.ContinuousInfeed,
        [StepParameterKeys.FeedMmPerMin] = DiagramElement.Feed,
        [StepParameterKeys.WorkpieceSpeedRpm] = DiagramElement.WorkpieceSpeed,
        [StepParameterKeys.WheelSurfaceSpeedMPerSec] = DiagramElement.WheelSurfaceSpeed,
        [StepParameterKeys.ReversalDwellSeconds] = DiagramElement.ReversalDwell,
        [StepParameterKeys.PassCount] = DiagramElement.Passes,
        [StepParameterKeys.SparkOutPassCount] = DiagramElement.SparkOutPasses,
        [StepParameterKeys.InProcessMeasurement] = DiagramElement.InProcessMeasurement,
        [StepParameterKeys.SpeedVariationTarget] = DiagramElement.SpeedVariation,
        [StepParameterKeys.SpeedVariationPercent] = DiagramElement.SpeedVariation,
        [StepParameterKeys.SpeedVariationPeriodRevolutions] = DiagramElement.SpeedVariation,
        [StepParameterKeys.MeasurePointCount] = DiagramElement.MeasurePoints,
        [StepParameterKeys.RoundnessSectionCount] = DiagramElement.RoundnessSections,
        [StepParameterKeys.RoundnessPointsPerRevolution] = DiagramElement.RoundnessPoints,
        [StepParameterKeys.ChamferLength1Mm] = DiagramElement.ChamferLength1,
        [StepParameterKeys.ChamferHeight1Mm] = DiagramElement.ChamferHeight1,
        [StepParameterKeys.ChamferLength2Mm] = DiagramElement.ChamferLength2,
        [StepParameterKeys.ChamferHeight2Mm] = DiagramElement.ChamferHeight2,
        [StepParameterKeys.ChamferKind] = DiagramElement.ChamferKind,
        [StepParameterKeys.DressInfeedRadiusMicrometer] = DiagramElement.DressInfeed,
        [StepParameterKeys.DressPassCount] = DiagramElement.DressPasses,
        [StepParameterKeys.DressFeedMmPerMin] = DiagramElement.DressFeed,
        [StepParameterKeys.ScanPitchMm] = DiagramElement.ScanPitch,
        [StepParameterKeys.AuxAction] = DiagramElement.AuxAction,
        [StepParameterKeys.AuxState] = DiagramElement.AuxState,
        [StepParameterKeys.PauseReason] = DiagramElement.PauseReason,
    };

    private static readonly Dictionary<StepDiagramKind, DiagramElement[]> Drawn = new()
    {
        [StepDiagramKind.Marker] = Array.Empty<DiagramElement>(),
        [StepDiagramKind.Traverse] = new[]
        {
            DiagramElement.Stock, DiagramElement.InfeedPerPass, DiagramElement.ContinuousInfeed, DiagramElement.Feed,
            DiagramElement.WorkpieceSpeed, DiagramElement.WheelSurfaceSpeed, DiagramElement.ReversalDwell,
            DiagramElement.Passes, DiagramElement.SparkOutPasses, DiagramElement.InProcessMeasurement,
            DiagramElement.SpeedVariation,
        },
        [StepDiagramKind.Measure] = new[] { DiagramElement.MeasurePoints, DiagramElement.Feed, DiagramElement.WorkpieceSpeed },
        [StepDiagramKind.Roundness] = new[]
        {
            DiagramElement.RoundnessSections, DiagramElement.RoundnessPoints, DiagramElement.WorkpieceSpeed,
        },
        [StepDiagramKind.Chamfer] = new[]
        {
            DiagramElement.ChamferLength1, DiagramElement.ChamferHeight1, DiagramElement.ChamferLength2,
            DiagramElement.ChamferHeight2, DiagramElement.ChamferKind, DiagramElement.Feed, DiagramElement.Passes,
            DiagramElement.WheelSurfaceSpeed, DiagramElement.WorkpieceSpeed,
        },
        [StepDiagramKind.WheelDress] = new[]
        {
            DiagramElement.DressInfeed, DiagramElement.DressPasses, DiagramElement.DressFeed, DiagramElement.WheelSurfaceSpeed,
        },
        [StepDiagramKind.EddyCurrent] = new[] { DiagramElement.ScanPitch, DiagramElement.WorkpieceSpeed },
        [StepDiagramKind.Pause] = new[] { DiagramElement.PauseReason },
        [StepDiagramKind.Auxiliary] = new[] { DiagramElement.AuxAction, DiagramElement.AuxState },
    };

    /// <summary>这类工序用哪张图。没列出的新工序类型画成标记，不会画错图。</summary>
    public static StepDiagramKind KindOf(string stepTypeKey) => stepTypeKey switch
    {
        StepTypeKeys.Rough or StepTypeKeys.SemiFinish or StepTypeKeys.Finish or StepTypeKeys.ShortStroke
            or StepTypeKeys.Polish or StepTypeKeys.SparkOut => StepDiagramKind.Traverse,
        StepTypeKeys.Measure => StepDiagramKind.Measure,
        StepTypeKeys.Roundness => StepDiagramKind.Roundness,
        StepTypeKeys.Chamfer => StepDiagramKind.Chamfer,
        StepTypeKeys.WheelDress => StepDiagramKind.WheelDress,
        StepTypeKeys.EddyCurrent => StepDiagramKind.EddyCurrent,
        StepTypeKeys.Pause => StepDiagramKind.Pause,
        StepTypeKeys.Auxiliary => StepDiagramKind.Auxiliary,
        _ => StepDiagramKind.Marker,
    };

    /// <summary>参数对应图上的哪个量；没有对应的返回 null。</summary>
    public static DiagramElement? ElementOf(string parameterKey) =>
        ByParameter.TryGetValue(parameterKey, out DiagramElement element) ? element : null;

    /// <summary>这张图上画了哪些量。</summary>
    public static IReadOnlyCollection<DiagramElement> ElementsOf(StepDiagramKind kind) => Drawn[kind];

    /// <summary>图上标的符号：工程上通用的记号，不带语言。</summary>
    public static string SymbolOf(DiagramElement element) => element switch
    {
        DiagramElement.Stock => "Δd",
        DiagramElement.InfeedPerPass => "ae",
        DiagramElement.ContinuousInfeed => "ae'",
        DiagramElement.Feed => "f",
        DiagramElement.WorkpieceSpeed => "n",
        DiagramElement.WheelSurfaceSpeed => "vc",
        DiagramElement.ReversalDwell => "t",
        DiagramElement.Passes => "i",
        DiagramElement.SparkOutPasses => "i0",
        DiagramElement.InProcessMeasurement => "M",
        DiagramElement.SpeedVariation => "Δn",
        DiagramElement.MeasurePoints => "k",
        DiagramElement.RoundnessSections => "s",
        DiagramElement.RoundnessPoints => "p",
        DiagramElement.ChamferLength1 => "L1",
        DiagramElement.ChamferHeight1 => "H1",
        DiagramElement.ChamferLength2 => "L2",
        DiagramElement.ChamferHeight2 => "H2",
        DiagramElement.ChamferKind => "R",
        DiagramElement.DressInfeed => "ad",
        DiagramElement.DressPasses => "id",
        DiagramElement.DressFeed => "fd",
        DiagramElement.ScanPitch => "P",
        DiagramElement.AuxAction => "A",
        DiagramElement.AuxState => "I/O",
        DiagramElement.PauseReason => "II",
        _ => string.Empty,
    };
}
