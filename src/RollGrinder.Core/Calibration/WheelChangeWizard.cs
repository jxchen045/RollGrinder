using System;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Core.Calibration;

/// <summary>换砂轮向导走到哪一步。</summary>
public enum WheelChangeStage
{
    /// <summary>填新砂轮的直径（厂家标称或量出来的）。</summary>
    EnterNewWheel = 0,

    /// <summary>把对刀方式改成手动。</summary>
    SwitchToManualTouch = 1,

    /// <summary>手动对刀，试磨一刀。这一步机床由操作工自己开，上位机只等。</summary>
    TrialGrind = 2,

    /// <summary>量实际磨出来的直径，比出砂轮直径误差并修正。</summary>
    Verify = 3,

    /// <summary>把对刀方式改回原来的样子。</summary>
    RestoreTouchMode = 4,

    /// <summary>走完了。</summary>
    Done = 5,
}

/// <summary>
/// 换砂轮向导：一个纯状态机。
///
/// 为什么要有这套顺序：新砂轮的直径只是个**标称值**，真实直径与它差几毫米很常见。
/// 这个误差会原样变成辊径误差——上位机以为砂轮大了 δ，X 轴就少进 δ，
/// 辊就磨大 2δ。自动对刀靠接触检测趋近，在直径还没标准的新砂轮上不可靠，
/// 所以先改手动对刀、手动试磨一刀，用**磨出来的实际直径**反推砂轮直径，
/// 修正之后再改回自动。
///
/// 这里刻意不引用任何机床访问：状态怎么走、误差怎么算是可以单独测的，
/// 写标定值、动机床是服务层的事。
/// </summary>
public sealed record WheelChangeWizard
{
    private WheelChangeWizard(
        WheelChangeStage stage,
        double newWheelDiameterMm,
        string originalTouchMode,
        double? expectedRollDiameterMm,
        double? measuredRollDiameterMm)
    {
        Stage = stage;
        NewWheelDiameterMm = newWheelDiameterMm;
        OriginalTouchMode = originalTouchMode;
        ExpectedRollDiameterMm = expectedRollDiameterMm;
        MeasuredRollDiameterMm = measuredRollDiameterMm;
    }

    /// <summary>当前这一步。</summary>
    public WheelChangeStage Stage { get; init; }

    /// <summary>这次换上去的砂轮直径（mm）。<see cref="Verify"/> 之后是修正过的值。</summary>
    public double NewWheelDiameterMm { get; init; }

    /// <summary>进向导之前的对刀方式：走完之后要原样改回去，而不是一律改成自动。</summary>
    public string OriginalTouchMode { get; init; }

    /// <summary>试磨那一刀，上位机以为会磨成多少（mm）。</summary>
    public double? ExpectedRollDiameterMm { get; init; }

    /// <summary>试磨那一刀，实际量出来是多少（mm）。</summary>
    public double? MeasuredRollDiameterMm { get; init; }

    /// <summary>
    /// 砂轮直径误差（mm，直径量，正表示上位机以为的砂轮比实际大）。
    /// 两个直径都有了才算得出来。
    /// </summary>
    public double? WheelDiameterErrorMm =>
        ExpectedRollDiameterMm is double expected && MeasuredRollDiameterMm is double measured
            ? measured - expected
            : null;

    /// <summary>修正之后的砂轮直径（mm）。</summary>
    public double? CorrectedWheelDiameterMm =>
        WheelDiameterErrorMm is double error ? NewWheelDiameterMm - error : null;

    /// <summary>开始一次换砂轮。</summary>
    /// <param name="currentTouchMode">进来之前的对刀方式，走完之后原样改回去。</param>
    public static WheelChangeWizard Begin(string currentTouchMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentTouchMode);

        return new WheelChangeWizard(
            WheelChangeStage.EnterNewWheel,
            newWheelDiameterMm: 0.0,
            currentTouchMode,
            expectedRollDiameterMm: null,
            measuredRollDiameterMm: null);
    }

    /// <summary>填下新砂轮直径，进入下一步。</summary>
    public WheelChangeWizard WithNewWheel(double diameterMm)
    {
        Require(WheelChangeStage.EnterNewWheel);

        // 砂轮直径的取值范围由标定值的模式声明，这里不再写第二份数字。
        ParameterDescriptor descriptor = MachineCalibration.Schema.Get(CalibrationKeys.WheelDiameterMm);
        if (diameterMm < descriptor.MinValue || diameterMm > descriptor.MaxValue)
        {
            throw new DomainException(
                $"New wheel diameter {diameterMm} mm is outside the range declared for "
                + $"'{CalibrationKeys.WheelDiameterMm}'.");
        }

        return this with { Stage = WheelChangeStage.SwitchToManualTouch, NewWheelDiameterMm = diameterMm };
    }

    /// <summary>记下试磨那一刀的两个直径，进入下一步。</summary>
    public WheelChangeWizard WithTrialResult(double expectedRollDiameterMm, double measuredRollDiameterMm)
    {
        Require(WheelChangeStage.TrialGrind);

        if (expectedRollDiameterMm <= 0.0 || measuredRollDiameterMm <= 0.0)
        {
            throw new DomainException("Trial grind diameters must be positive.");
        }

        return this with
        {
            Stage = WheelChangeStage.Verify,
            ExpectedRollDiameterMm = expectedRollDiameterMm,
            MeasuredRollDiameterMm = measuredRollDiameterMm,
        };
    }

    /// <summary>
    /// 接受修正值：把砂轮直径改成反推出来的那个数。
    ///
    /// 修正之后必须仍然落在声明的范围内——超出去说明这一刀的问题不在砂轮直径上
    /// （对错刀、测错值、辊装歪了都会长成这样），这时候改砂轮直径只会把错误固化下来。
    /// </summary>
    public WheelChangeWizard AcceptCorrection()
    {
        Require(WheelChangeStage.Verify);

        double corrected = CorrectedWheelDiameterMm
            ?? throw new DomainException("No trial grind result to correct from.");

        ParameterDescriptor descriptor = MachineCalibration.Schema.Get(CalibrationKeys.WheelDiameterMm);
        if (corrected < descriptor.MinValue || corrected > descriptor.MaxValue)
        {
            throw new DomainException(
                $"Corrected wheel diameter {corrected} mm is outside the range declared for "
                + $"'{CalibrationKeys.WheelDiameterMm}'; the trial grind is not measuring the wheel.");
        }

        return this with { Stage = WheelChangeStage.RestoreTouchMode, NewWheelDiameterMm = corrected };
    }

    /// <summary>
    /// 跳过修正：误差小到不值得改，或者这一刀不作数。
    /// 砂轮直径保持填进来的标称值，向导照样要把对刀方式改回去。
    /// </summary>
    public WheelChangeWizard SkipCorrection()
    {
        Require(WheelChangeStage.Verify);
        return this with { Stage = WheelChangeStage.RestoreTouchMode };
    }

    /// <summary>确认当前这一步做完了，往下走一步。用于没有输入的那几步。</summary>
    public WheelChangeWizard Advance() => Stage switch
    {
        WheelChangeStage.SwitchToManualTouch => this with { Stage = WheelChangeStage.TrialGrind },
        WheelChangeStage.RestoreTouchMode => this with { Stage = WheelChangeStage.Done },
        _ => throw new DomainException($"Stage {Stage} needs its own input, not a plain advance."),
    };

    private void Require(WheelChangeStage expected)
    {
        if (Stage != expected)
        {
            throw new DomainException($"Wheel change is at {Stage}, not {expected}.");
        }
    }
}
