using System;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Calibration;

/// <summary>现场标定值的参数键。</summary>
public static class CalibrationKeys
{
    /// <summary>当前砂轮直径（mm）。换一次砂轮改一次，磨损也在变。</summary>
    public const string WheelDiameterMm = "wheelDiameterMm";

    /// <summary>新砂轮直径（mm）：换上一片新砂轮时直径回到这个值。</summary>
    public const string NewWheelDiameterMm = "newWheelDiameterMm";

    /// <summary>砂轮宽度（mm）。</summary>
    public const string WheelWidthMm = "wheelWidthMm";

    /// <summary>基准盘直径（mm）：测量臂标定用的那个基准，靠精密量具量出来。</summary>
    public const string ReferenceDiscDiameterMm = "referenceDiscDiameterMm";

    /// <summary>基准盘相对尾座检测位置的偏移（mm）。</summary>
    public const string ReferenceDiscOffsetMm = "referenceDiscOffsetMm";

    /// <summary>金刚笔相对尾座位置的偏移（mm）。</summary>
    public const string DresserOffsetMm = "dresserOffsetMm";

    /// <summary>金刚笔基准块相对尾座位置的偏移（mm）。</summary>
    public const string DresserReferenceOffsetMm = "dresserReferenceOffsetMm";

    /// <summary>B 探头距砂轮中心的距离（mm）。</summary>
    public const string ProbeBToWheelCentreMm = "probeBToWheelCentreMm";

    /// <summary>B 探头距砂轮表面的距离（mm）。</summary>
    public const string ProbeBToWheelSurfaceMm = "probeBToWheelSurfaceMm";

    /// <summary>对刀偏移（mm）：自动对刀时砂轮与辊面的偏差值，可以是负的。</summary>
    public const string TouchOffsetMm = "touchOffsetMm";

    /// <summary>对刀方式：手动 / 自动。</summary>
    public const string TouchMode = "touchMode";

    /// <summary>短行程电流（A）：热轧辊磨削量很大时，短行程工艺按这个电流控制行程范围。</summary>
    public const string ShortStrokeCurrentA = "shortStrokeCurrentA";

    /// <summary>辊形验收公差（直径量 µm）。</summary>
    public const string ProfileToleranceMicrometer = "profileToleranceMicrometer";

    /// <summary>圆度验收公差（直径量 µm）。</summary>
    public const string RoundnessToleranceMicrometer = "roundnessToleranceMicrometer";

    /// <summary>对中（安装误差）验收公差（直径量 µm）。</summary>
    public const string CentringToleranceMicrometer = "centringToleranceMicrometer";
}

/// <summary>对刀方式的选项键。</summary>
public static class TouchModeChoices
{
    /// <summary>手动对刀：操作工自己把砂轮摇到接触。</summary>
    public const string Manual = "manual";

    /// <summary>自动对刀：靠接触检测（声发射或功率突变）趋近。</summary>
    public const string Automatic = "automatic";
}

/// <summary>
/// 现场标定值。
///
/// 与 <c>machine.json</c> 描述的**机床固有能力**（有哪几根轴、行程多长、装了什么选件）
/// 是两回事：那些装机时定下就基本不动，而这里的值**换一次砂轮就变**，
/// 现场要能在界面上改、要记谁在什么时候改的。所以它们存数据库，不躺在配置文件里等人去编辑。
///
/// 取值全部由 <see cref="Schema"/> 描述，新增一项只写一行 + 一条 resx 文案，
/// 设置页与持久化都不用动（架构约束 ④）。
/// </summary>
public sealed record MachineCalibration
{
    /// <summary>标定值的参数模式：单位、默认值、上下限都在这里。</summary>
    public static ParameterSchema Schema { get; } = new(new[]
    {
        ParameterDescriptor.Number(CalibrationKeys.WheelDiameterMm, ParameterUnit.Millimeter, 900.0, 1.0, 2000.0),
        ParameterDescriptor.Number(CalibrationKeys.NewWheelDiameterMm, ParameterUnit.Millimeter, 1000.0, 1.0, 2000.0),
        ParameterDescriptor.Number(CalibrationKeys.WheelWidthMm, ParameterUnit.Millimeter, 100.0, 1.0, 500.0),
        ParameterDescriptor.Number(CalibrationKeys.ReferenceDiscDiameterMm, ParameterUnit.Millimeter, 0.0, 0.0, 1000.0),
        ParameterDescriptor.Number(CalibrationKeys.ReferenceDiscOffsetMm, ParameterUnit.Millimeter, 0.0, 0.0, 10000.0),
        ParameterDescriptor.Number(CalibrationKeys.DresserOffsetMm, ParameterUnit.Millimeter, 0.0, 0.0, 10000.0),
        ParameterDescriptor.Number(CalibrationKeys.DresserReferenceOffsetMm, ParameterUnit.Millimeter, 0.0, 0.0, 10000.0),
        ParameterDescriptor.Number(CalibrationKeys.ProbeBToWheelCentreMm, ParameterUnit.Millimeter, 0.0, 0.0, 500.0),
        ParameterDescriptor.Number(CalibrationKeys.ProbeBToWheelSurfaceMm, ParameterUnit.Millimeter, 0.0, 0.0, 500.0),

        // 对刀偏移可以是负的：宁可少切一点也不要扎刀。
        ParameterDescriptor.Number(CalibrationKeys.TouchOffsetMm, ParameterUnit.Millimeter, 0.0, -2.0, 2.0),
        ParameterDescriptor.Choice(
            CalibrationKeys.TouchMode,
            new[] { TouchModeChoices.Manual, TouchModeChoices.Automatic },
            TouchModeChoices.Automatic),
        ParameterDescriptor.Number(CalibrationKeys.ShortStrokeCurrentA, ParameterUnit.Ampere, 0.0, 0.0, 100.0),
        ParameterDescriptor.Number(
            CalibrationKeys.ProfileToleranceMicrometer, ParameterUnit.Micrometer, 10.0, 0.1, 1000.0),
        ParameterDescriptor.Number(
            CalibrationKeys.RoundnessToleranceMicrometer, ParameterUnit.Micrometer, 10.0, 0.1, 1000.0),
        ParameterDescriptor.Number(
            CalibrationKeys.CentringToleranceMicrometer, ParameterUnit.Micrometer, 20.0, 0.1, 1000.0),
    });

    /// <summary>全套默认值。数据库里一条都没有时用它——但默认值不是机床数字，装机时必须现场标定。</summary>
    public static MachineCalibration Defaults { get; } = new(Schema.CreateDefaults());

    public MachineCalibration(ParameterSet values)
    {
        ArgumentNullException.ThrowIfNull(values);

        // 缺的键补默认值：少一项不该让"这台机床标定过没有"变成未定义。
        Values = Schema.ApplyDefaults(values);
    }

    /// <summary>全部标定值。</summary>
    public ParameterSet Values { get; }

    /// <summary>当前砂轮直径（mm）。</summary>
    public double WheelDiameterMm => Values.GetNumber(CalibrationKeys.WheelDiameterMm);

    /// <summary>当前砂轮半径（mm）。内部计算一律用半径量（架构约束 ⑨）。</summary>
    public double WheelRadiusMm => UnitConversion.DiameterMmToRadiusMm(WheelDiameterMm);

    /// <summary>新砂轮直径（mm）。</summary>
    public double NewWheelDiameterMm => Values.GetNumber(CalibrationKeys.NewWheelDiameterMm);

    /// <summary>砂轮宽度（mm）。</summary>
    public double WheelWidthMm => Values.GetNumber(CalibrationKeys.WheelWidthMm);

    /// <summary>对刀偏移（mm）。</summary>
    public double TouchOffsetMm => Values.GetNumber(CalibrationKeys.TouchOffsetMm);

    /// <summary>对刀是不是自动方式。</summary>
    public bool TouchesAutomatically =>
        string.Equals(Values.GetChoice(CalibrationKeys.TouchMode), TouchModeChoices.Automatic, StringComparison.Ordinal);

    /// <summary>短行程电流（A）。</summary>
    public double ShortStrokeCurrentA => Values.GetNumber(CalibrationKeys.ShortStrokeCurrentA);

    /// <summary>辊形验收公差（直径量 µm）。</summary>
    public double ProfileToleranceMicrometer => Values.GetNumber(CalibrationKeys.ProfileToleranceMicrometer);

    /// <summary>圆度验收公差（直径量 µm）。</summary>
    public double RoundnessToleranceMicrometer => Values.GetNumber(CalibrationKeys.RoundnessToleranceMicrometer);

    /// <summary>对中验收公差（直径量 µm）。</summary>
    public double CentringToleranceMicrometer => Values.GetNumber(CalibrationKeys.CentringToleranceMicrometer);

    /// <summary>
    /// 砂轮磨掉了多少（mm，直径量）：新砂轮直径减当前直径。
    /// 到了该换砂轮的时候，诊断页按它提示。
    /// </summary>
    public double WheelWearDiameterMm => Math.Max(0.0, NewWheelDiameterMm - WheelDiameterMm);
}
