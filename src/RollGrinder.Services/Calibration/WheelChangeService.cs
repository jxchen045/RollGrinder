using System;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Core.Calibration;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Services.Calibration;

/// <summary>
/// 换砂轮向导的执行者：状态怎么走由 <see cref="WheelChangeWizard"/> 定，
/// 这里只负责在该写标定值的那几步把值写进去。
///
/// **向导不动机床。** 换砂轮、手动对刀、试磨都是人在机床上做的事；
/// 上位机做的是记下新直径、把对刀方式切过去再切回来、以及按试磨结果
/// 反推真实砂轮直径。所以上位机中途被关掉，机床上的活照样能做完。
/// </summary>
public interface IWheelChangeService
{
    /// <summary>当前这一次换砂轮；没在进行中时为 null。</summary>
    WheelChangeWizard? Current { get; }

    /// <summary>向导状态变了。</summary>
    event EventHandler? Changed;

    /// <summary>开始一次换砂轮，记下进来之前的对刀方式。</summary>
    void Begin();

    /// <summary>取消：不改任何标定值，对刀方式也还没动过就直接丢掉。</summary>
    Task CancelAsync(string changedBy, CancellationToken cancellationToken);

    /// <summary>填下新砂轮直径（mm）。</summary>
    void SetNewWheelDiameter(double diameterMm);

    /// <summary>把对刀方式改成手动，进入试磨。</summary>
    Task SwitchToManualTouchAsync(string changedBy, CancellationToken cancellationToken);

    /// <summary>记下试磨那一刀的预期直径与实测直径（mm）。</summary>
    void RecordTrial(double expectedRollDiameterMm, double measuredRollDiameterMm);

    /// <summary>接受按试磨结果反推出来的砂轮直径。</summary>
    void AcceptCorrection();

    /// <summary>不改砂轮直径，直接往下走。</summary>
    void SkipCorrection();

    /// <summary>把砂轮直径落库，对刀方式改回原来的样子，向导结束。</summary>
    Task FinishAsync(string changedBy, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWheelChangeService"/>
public sealed class WheelChangeService : IWheelChangeService
{
    private readonly ICalibrationService calibration;

    public WheelChangeService(ICalibrationService calibration)
    {
        this.calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
    }

    public WheelChangeWizard? Current { get; private set; }

    public event EventHandler? Changed;

    public void Begin()
    {
        Current = WheelChangeWizard.Begin(
            this.calibration.Current.Values.GetChoice(CalibrationKeys.TouchMode));
        Raise();
    }

    public async Task CancelAsync(string changedBy, CancellationToken cancellationToken)
    {
        WheelChangeWizard? wizard = Current;
        Current = null;

        // 已经切成手动对刀了才需要切回去；还没切就没什么要收拾的。
        if (wizard is not null && wizard.Stage > WheelChangeStage.SwitchToManualTouch)
        {
            await RestoreTouchModeAsync(wizard, changedBy, cancellationToken).ConfigureAwait(false);
        }

        Raise();
    }

    public void SetNewWheelDiameter(double diameterMm)
    {
        Current = Require().WithNewWheel(diameterMm);
        Raise();
    }

    public async Task SwitchToManualTouchAsync(string changedBy, CancellationToken cancellationToken)
    {
        WheelChangeWizard wizard = Require();

        // 自动对刀靠接触检测趋近，在直径还没标准的新砂轮上不可靠。
        await this.calibration.SaveAsync(
            this.calibration.Current.Values.With(
                CalibrationKeys.TouchMode, ParameterValue.FromChoice(TouchModeChoices.Manual)),
            changedBy,
            cancellationToken).ConfigureAwait(false);

        Current = wizard.Advance();
        Raise();
    }

    public void RecordTrial(double expectedRollDiameterMm, double measuredRollDiameterMm)
    {
        Current = Require().WithTrialResult(expectedRollDiameterMm, measuredRollDiameterMm);
        Raise();
    }

    public void AcceptCorrection()
    {
        Current = Require().AcceptCorrection();
        Raise();
    }

    public void SkipCorrection()
    {
        Current = Require().SkipCorrection();
        Raise();
    }

    public async Task FinishAsync(string changedBy, CancellationToken cancellationToken)
    {
        WheelChangeWizard wizard = Require();

        // 一次写完：砂轮直径、新砂轮直径基准、对刀方式。
        // 分几次写的话，中间被打断就会留下一台"直径改了、对刀方式还是手动"的机床。
        ParameterSet values = this.calibration.Current.Values
            .With(CalibrationKeys.WheelDiameterMm, ParameterValue.FromNumber(wizard.NewWheelDiameterMm))
            .With(CalibrationKeys.NewWheelDiameterMm, ParameterValue.FromNumber(wizard.NewWheelDiameterMm))
            .With(CalibrationKeys.TouchMode, ParameterValue.FromChoice(wizard.OriginalTouchMode));

        await this.calibration.SaveAsync(values, changedBy, cancellationToken).ConfigureAwait(false);

        Current = wizard.Advance();
        Raise();
    }

    private Task RestoreTouchModeAsync(
        WheelChangeWizard wizard, string changedBy, CancellationToken cancellationToken) =>
        this.calibration.SaveAsync(
            this.calibration.Current.Values.With(
                CalibrationKeys.TouchMode, ParameterValue.FromChoice(wizard.OriginalTouchMode)),
            changedBy,
            cancellationToken);

    private WheelChangeWizard Require() =>
        Current ?? throw new RollGrinder.Core.DomainException("No wheel change is in progress.");

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
