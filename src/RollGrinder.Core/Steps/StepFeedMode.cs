namespace RollGrinder.Core.Steps;

/// <summary>
/// 一道工序实际用到的横向（切入）进给分量。
///
/// 这是从两个进给量**推导**出来的分类，不是操作员选的开关——
/// 连续进给与周期进给是两个可以叠加的分量，各自置 0 表示"这一路不用"。
/// 依据：MGK84160 操作说明书的磨削实例表，粗磨 连续 0.05 mm/min 与 周期 0.005 mm
/// 同时非零。详见 docs/design/工艺参数语义.md。
/// </summary>
public enum StepFeedMode
{
    /// <summary>两路都是 0：不进给（测量、探伤、光磨、标记类工序）。</summary>
    None = 0,

    /// <summary>只有连续进给：拖板走行程的同时 X 轴持续切入，辊面留螺旋。</summary>
    Continuous = 1,

    /// <summary>只有周期进给：只在换向点一次性进给，每道次等深。</summary>
    PerReversal = 2,

    /// <summary>两路同时非零：连续分量负责去量，周期分量负责分层。粗磨、半精磨用这一种。</summary>
    Combined = 3,
}

/// <summary>变速（打散再生颤振）作用在谁身上。</summary>
public enum SpeedVariationTarget
{
    /// <summary>不变速。精磨末段与测量时必须用这个，转速要稳。</summary>
    Off = 0,

    /// <summary>轧辊（工件 / 头架）转速。打散工件表面的再生波纹，默认选它。</summary>
    Workpiece = 1,

    /// <summary>砂轮转速。打散砂轮自身的再生波纹；会同步改变线速度，幅度要保守。</summary>
    Wheel = 2,

    /// <summary>两者同时变速。</summary>
    Both = 3,
}

/// <summary>
/// 变速设置。幅度与周期缺一不可——变速是一条正弦曲线，只给幅度下发不了。
/// </summary>
/// <param name="Target">作用对象。</param>
/// <param name="AmplitudePercent">幅度（±%）。</param>
/// <param name="PeriodSeconds">周期（s）。</param>
public sealed record SpeedVariation(SpeedVariationTarget Target, double AmplitudePercent, double PeriodSeconds)
{
    /// <summary>不变速。</summary>
    public static SpeedVariation Off { get; } = new(SpeedVariationTarget.Off, 0.0, 0.0);

    /// <summary>是否作用在工件转速上。</summary>
    public bool AffectsWorkpiece => Target is SpeedVariationTarget.Workpiece or SpeedVariationTarget.Both;

    /// <summary>是否作用在砂轮转速上。</summary>
    public bool AffectsWheel => Target is SpeedVariationTarget.Wheel or SpeedVariationTarget.Both;

    /// <summary>某个转速在变速下能达到的峰值。限幅校验按峰值算，不按设定值算。</summary>
    public double PeakOf(double setpoint) => setpoint * (1.0 + (AmplitudePercent / 100.0));

    /// <summary>某个转速在变速下能达到的谷值。</summary>
    public double TroughOf(double setpoint) => setpoint * (1.0 - (AmplitudePercent / 100.0));
}
