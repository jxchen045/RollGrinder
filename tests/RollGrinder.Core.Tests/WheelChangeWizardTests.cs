using System;
using FluentAssertions;
using RollGrinder.Core;
using RollGrinder.Core.Calibration;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 换砂轮向导。
///
/// 新砂轮的直径只是个标称值，真实直径差几毫米很常见，而这个误差会原样
/// 变成辊径误差。所以有这套顺序：切手动对刀 → 试磨一刀 → 按实测反推 → 切回去。
/// </summary>
public sealed class WheelChangeWizardTests
{
    private static WheelChangeWizard AtTrial(double newWheelDiameterMm = 1000.0) =>
        WheelChangeWizard.Begin(TouchModeChoices.Automatic)
            .WithNewWheel(newWheelDiameterMm)
            .Advance();

    [Fact]
    public void The_wizard_walks_the_steps_in_order()
    {
        WheelChangeWizard wizard = WheelChangeWizard.Begin(TouchModeChoices.Automatic);
        wizard.Stage.Should().Be(WheelChangeStage.EnterNewWheel);

        wizard = wizard.WithNewWheel(1000.0);
        wizard.Stage.Should().Be(WheelChangeStage.SwitchToManualTouch);

        wizard = wizard.Advance();
        wizard.Stage.Should().Be(WheelChangeStage.TrialGrind);

        wizard = wizard.WithTrialResult(650.000, 650.000);
        wizard.Stage.Should().Be(WheelChangeStage.Verify);

        wizard = wizard.AcceptCorrection();
        wizard.Stage.Should().Be(WheelChangeStage.RestoreTouchMode);

        wizard.Advance().Stage.Should().Be(WheelChangeStage.Done);
    }

    [Fact]
    public void A_roll_that_came_out_too_big_means_the_wheel_is_smaller_than_assumed()
    {
        // 上位机以为砂轮大了 δ ⇒ X 轴少进 δ ⇒ 辊磨大 2δ（直径量）。
        // 所以实测比预期大 0.4 mm，就是砂轮直径被高估了 0.4 mm。
        WheelChangeWizard wizard = AtTrial(1000.0).WithTrialResult(650.000, 650.400);

        wizard.WheelDiameterErrorMm.Should().BeApproximately(0.4, 1e-9);
        wizard.CorrectedWheelDiameterMm.Should().BeApproximately(999.6, 1e-9);
    }

    [Fact]
    public void A_roll_that_came_out_too_small_corrects_the_other_way()
    {
        WheelChangeWizard wizard = AtTrial(1000.0).WithTrialResult(650.000, 649.700);

        wizard.WheelDiameterErrorMm.Should().BeApproximately(-0.3, 1e-9);
        wizard.CorrectedWheelDiameterMm.Should().BeApproximately(1000.3, 1e-9);
    }

    [Fact]
    public void Accepting_the_correction_is_what_changes_the_wheel_diameter()
    {
        WheelChangeWizard wizard = AtTrial(1000.0).WithTrialResult(650.000, 650.400);

        wizard.AcceptCorrection().NewWheelDiameterMm.Should().BeApproximately(999.6, 1e-9);
    }

    [Fact]
    public void Skipping_the_correction_keeps_the_nominal_diameter()
    {
        // 这一刀不作数时，砂轮直径保持填进来的标称值——但对刀方式照样要改回去。
        WheelChangeWizard wizard = AtTrial(1000.0).WithTrialResult(650.000, 650.400).SkipCorrection();

        wizard.Stage.Should().Be(WheelChangeStage.RestoreTouchMode);
        wizard.NewWheelDiameterMm.Should().Be(1000.0);
    }

    [Fact]
    public void A_correction_that_lands_outside_the_declared_range_is_refused()
    {
        // 差出几百毫米说明这一刀量的不是砂轮：对错刀、测错值、辊装歪了都会长成这样，
        // 这时候改砂轮直径只会把错误固化下来。
        WheelChangeWizard wizard = AtTrial(10.0).WithTrialResult(650.0, 700.0);

        wizard.Invoking(w => w.AcceptCorrection()).Should().Throw<DomainException>();
    }

    [Fact]
    public void The_touch_mode_it_came_in_with_is_the_one_it_goes_back_to()
    {
        // 一律改回自动的话，本来就用手动对刀的机床会被向导偷偷改掉。
        WheelChangeWizard wizard = WheelChangeWizard.Begin(TouchModeChoices.Manual).WithNewWheel(1000.0);

        wizard.OriginalTouchMode.Should().Be(TouchModeChoices.Manual);
    }

    [Fact]
    public void A_wheel_diameter_outside_the_declared_range_is_refused_up_front()
    {
        WheelChangeWizard wizard = WheelChangeWizard.Begin(TouchModeChoices.Automatic);

        wizard.Invoking(w => w.WithNewWheel(5000.0)).Should().Throw<DomainException>();
        wizard.Invoking(w => w.WithNewWheel(0.0)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Steps_cannot_be_taken_out_of_order()
    {
        // 顺序本身就是这套流程的价值：跳过切手动对刀，试磨那一刀就不作数了。
        WheelChangeWizard fresh = WheelChangeWizard.Begin(TouchModeChoices.Automatic);

        fresh.Invoking(w => w.WithTrialResult(650.0, 650.0)).Should().Throw<DomainException>();
        fresh.Invoking(w => w.AcceptCorrection()).Should().Throw<DomainException>();
        fresh.Invoking(w => w.Advance()).Should().Throw<DomainException>();
    }

    [Fact]
    public void A_trial_needs_two_real_diameters()
    {
        AtTrial().Invoking(w => w.WithTrialResult(0.0, 650.0)).Should().Throw<DomainException>();
        AtTrial().Invoking(w => w.WithTrialResult(650.0, -1.0)).Should().Throw<DomainException>();
    }
}
