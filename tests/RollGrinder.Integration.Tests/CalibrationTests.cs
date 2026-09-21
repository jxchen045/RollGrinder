using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Core;
using RollGrinder.Core.Calibration;
using RollGrinder.Core.Parameters;
using RollGrinder.Data;
using RollGrinder.Services.Calibration;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 现场标定值：换一次砂轮就变的那些数，存数据库、带改动记录、按权限改。
/// 与 machine.json 描述的机床固有能力是两回事。
/// </summary>
public sealed class CalibrationTests : IDisposable
{
    private readonly TempWorkspace workspace = new();
    private readonly SqliteDatabase database;

    public CalibrationTests()
    {
        this.database = new SqliteDatabase(Path.Combine(this.workspace.Root, "data", SqliteDatabase.FileName));
    }

    public void Dispose() => this.workspace.Dispose();

    private async Task<CalibrationService> ServiceAsync()
    {
        await this.database.MigrateAsync(CancellationToken.None);
        var service = new CalibrationService(new SqliteCalibrationRepository(this.database));
        await service.LoadAsync(CancellationToken.None);
        return service;
    }

    private static ParameterSet With(params (string Key, double Value)[] overrides) =>
        overrides.Aggregate(
            MachineCalibration.Schema.CreateDefaults(),
            (set, pair) => set.With(pair.Key, ParameterValue.FromNumber(pair.Value)));

    [Fact]
    public async Task An_empty_database_still_gives_a_complete_set_of_defaults()
    {
        CalibrationService service = await ServiceAsync();

        service.Current.Values.Count.Should().Be(MachineCalibration.Schema.Descriptors.Count);
        service.Current.ProfileToleranceMicrometer.Should().Be(10.0);
        service.Current.CentringToleranceMicrometer.Should().Be(20.0);
    }

    [Fact]
    public async Task Saved_values_come_back()
    {
        CalibrationService service = await ServiceAsync();

        await service.SaveAsync(
            With((CalibrationKeys.WheelDiameterMm, 873.5), (CalibrationKeys.WheelWidthMm, 120.0)),
            "wang",
            CancellationToken.None);

        var reloaded = new CalibrationService(new SqliteCalibrationRepository(this.database));
        await reloaded.LoadAsync(CancellationToken.None);

        reloaded.Current.WheelDiameterMm.Should().Be(873.5);
        reloaded.Current.WheelWidthMm.Should().Be(120.0);
        reloaded.Current.WheelRadiusMm.Should().BeApproximately(436.75, 1e-9, "内部算半径量");
    }

    [Fact]
    public async Task A_value_outside_its_range_is_refused()
    {
        CalibrationService service = await ServiceAsync();

        // 对刀偏移只允许 −2…+2 mm。
        await service.Invoking(s => s.SaveAsync(
                With((CalibrationKeys.TouchOffsetMm, 9.0)), "wang", CancellationToken.None))
            .Should().ThrowAsync<DomainException>();

        service.Current.TouchOffsetMm.Should().Be(0.0, "不合法的一整批都不该落库");
    }

    [Fact]
    public async Task The_first_save_establishes_a_baseline_for_every_value()
    {
        // 库里一条都没有时按一次保存：15 项全部落库，记在按保存的那个人头上。
        // 这就是装机标定那一次。
        CalibrationService service = await ServiceAsync();

        await service.SaveAsync(With((CalibrationKeys.WheelDiameterMm, 880.0)), "wang", CancellationToken.None);

        IReadOnlyList<CalibrationAudit> audit = await service.LoadAuditAsync(CancellationToken.None);

        audit.Should().HaveCount(MachineCalibration.Schema.Descriptors.Count);
        audit.Should().OnlyContain(entry => entry.ChangedBy == "wang");
        audit.Select(entry => entry.ParameterKey).Should().Contain(CalibrationKeys.WheelDiameterMm);
    }

    [Fact]
    public async Task A_later_change_is_recorded_against_whoever_made_it()
    {
        CalibrationService service = await ServiceAsync();
        await service.SaveAsync(With((CalibrationKeys.WheelDiameterMm, 880.0)), "wang", CancellationToken.None);

        await service.SaveAsync(With((CalibrationKeys.WheelDiameterMm, 875.0)), "li", CancellationToken.None);

        IReadOnlyList<CalibrationAudit> audit = await service.LoadAuditAsync(CancellationToken.None);
        audit.Single(entry => entry.ParameterKey == CalibrationKeys.WheelDiameterMm).ChangedBy.Should().Be("li");
        audit.Count(entry => entry.ChangedBy == "li").Should().Be(1, "只有真的改了的那一项算在 li 头上");
    }

    [Fact]
    public async Task Saving_without_changing_anything_does_not_refresh_the_change_log()
    {
        // 否则按一下保存，所有项的"最后改动"都会被刷成今天，
        // 追溯"上次换砂轮是什么时候"就没意义了。
        CalibrationService service = await ServiceAsync();

        // 先建立基线（装机标定那一次）。
        ParameterSet baseline = With((CalibrationKeys.WheelDiameterMm, 880.0));
        await service.SaveAsync(baseline, "wang", CancellationToken.None);
        IReadOnlyList<CalibrationAudit> before = await service.LoadAuditAsync(CancellationToken.None);

        // 一模一样再存一次：一条记录都不该动。
        await service.SaveAsync(baseline, "li", CancellationToken.None);
        IReadOnlyList<CalibrationAudit> after = await service.LoadAuditAsync(CancellationToken.None);

        after.Should().BeEquivalentTo(before, "值没变就不该记成别人改的");
        after.Should().OnlyContain(entry => entry.ChangedBy == "wang");
    }

    [Fact]
    public async Task Changing_one_value_leaves_the_others_change_log_alone()
    {
        CalibrationService service = await ServiceAsync();

        await service.SaveAsync(
            With((CalibrationKeys.WheelDiameterMm, 880.0), (CalibrationKeys.WheelWidthMm, 120.0)),
            "wang",
            CancellationToken.None);
        await service.SaveAsync(
            With((CalibrationKeys.WheelDiameterMm, 875.0), (CalibrationKeys.WheelWidthMm, 120.0)),
            "li",
            CancellationToken.None);

        IReadOnlyList<CalibrationAudit> audit = await service.LoadAuditAsync(CancellationToken.None);

        audit.Single(entry => entry.ParameterKey == CalibrationKeys.WheelDiameterMm).ChangedBy.Should().Be("li");
        audit.Single(entry => entry.ParameterKey == CalibrationKeys.WheelWidthMm).ChangedBy.Should().Be("wang");
    }

    [Fact]
    public async Task Wheel_wear_is_the_gap_between_new_and_current()
    {
        CalibrationService service = await ServiceAsync();

        await service.SaveAsync(
            With((CalibrationKeys.NewWheelDiameterMm, 1000.0), (CalibrationKeys.WheelDiameterMm, 873.0)),
            "wang",
            CancellationToken.None);

        service.Current.WheelWearDiameterMm.Should().Be(127.0);
    }

    [Fact]
    public async Task A_wheel_bigger_than_new_reports_no_wear_instead_of_a_negative_number()
    {
        CalibrationService service = await ServiceAsync();

        await service.SaveAsync(
            With((CalibrationKeys.NewWheelDiameterMm, 900.0), (CalibrationKeys.WheelDiameterMm, 950.0)),
            "wang",
            CancellationToken.None);

        service.Current.WheelWearDiameterMm.Should().Be(0.0);
    }

    [Fact]
    public async Task Loading_raises_changed_so_the_pages_follow_along()
    {
        CalibrationService service = await ServiceAsync();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await service.SaveAsync(With((CalibrationKeys.WheelWidthMm, 90.0)), "wang", CancellationToken.None);

        raised.Should().BeGreaterThan(0, "设置页改完，公差与砂轮直径在别的页面上要跟着变");
    }

    [Fact]
    public void Touch_mode_reads_as_a_flag_not_a_string_comparison_at_every_call_site()
    {
        new MachineCalibration(
                MachineCalibration.Schema.CreateDefaults()
                    .With(CalibrationKeys.TouchMode, ParameterValue.FromChoice(TouchModeChoices.Manual)))
            .TouchesAutomatically.Should().BeFalse();

        MachineCalibration.Defaults.TouchesAutomatically.Should().BeTrue("默认自动对刀");
    }
}
