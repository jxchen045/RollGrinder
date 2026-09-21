using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Calibration;
using RollGrinder.Data;
using RollGrinder.Services;
using RollGrinder.Services.Calibration;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 换砂轮向导的执行：该切对刀方式的时候切，该落库的时候落。
///
/// 向导不动机床——换砂轮、手动对刀、试磨都是人在机床上做的事。
/// </summary>
public sealed class WheelChangeTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private async Task<ServiceProvider> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(
            options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);

        var database = new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName));
        await database.MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);

        ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ICalibrationService>().LoadAsync(CancellationToken.None);
        return provider;
    }

    [Fact]
    public async Task The_whole_run_lands_a_corrected_wheel_diameter_and_puts_the_touch_mode_back()
    {
        await using ServiceProvider services = await BuildAsync();
        ICalibrationService calibration = services.GetRequiredService<ICalibrationService>();
        IWheelChangeService wheelChange = services.GetRequiredService<IWheelChangeService>();

        bool wasAutomatic = calibration.Current.TouchesAutomatically;

        wheelChange.Begin();
        wheelChange.SetNewWheelDiameter(1000.0);
        await wheelChange.SwitchToManualTouchAsync("wang", CancellationToken.None);

        // 切过去了才试磨：自动对刀在直径还没标准的新砂轮上不可靠。
        calibration.Current.TouchesAutomatically.Should().BeFalse();

        wheelChange.RecordTrial(650.000, 650.400);
        wheelChange.AcceptCorrection();
        await wheelChange.FinishAsync("wang", CancellationToken.None);

        calibration.Current.WheelDiameterMm.Should().BeApproximately(999.6, 1e-6);
        calibration.Current.NewWheelDiameterMm.Should().BeApproximately(999.6, 1e-6);
        calibration.Current.TouchesAutomatically.Should().Be(wasAutomatic, "对刀方式要改回原来的样子");
        calibration.Current.WheelWearDiameterMm.Should().Be(0.0, "刚换的砂轮还没磨损");
        wheelChange.Current!.Stage.Should().Be(WheelChangeStage.Done);
    }

    [Fact]
    public async Task Abandoning_half_way_puts_the_touch_mode_back_too()
    {
        // 最怕的是留下一台"对刀方式还是手动"的机床，下一支辊就按错的方式对刀了。
        await using ServiceProvider services = await BuildAsync();
        ICalibrationService calibration = services.GetRequiredService<ICalibrationService>();
        IWheelChangeService wheelChange = services.GetRequiredService<IWheelChangeService>();

        double originalDiameterMm = calibration.Current.WheelDiameterMm;
        bool wasAutomatic = calibration.Current.TouchesAutomatically;

        wheelChange.Begin();
        wheelChange.SetNewWheelDiameter(1000.0);
        await wheelChange.SwitchToManualTouchAsync("wang", CancellationToken.None);
        await wheelChange.CancelAsync("wang", CancellationToken.None);

        wheelChange.Current.Should().BeNull();
        calibration.Current.TouchesAutomatically.Should().Be(wasAutomatic);
        calibration.Current.WheelDiameterMm.Should().Be(originalDiameterMm, "放弃了就一个标定值都不该改");
    }

    [Fact]
    public async Task Abandoning_before_the_touch_mode_was_touched_changes_nothing()
    {
        await using ServiceProvider services = await BuildAsync();
        ICalibrationService calibration = services.GetRequiredService<ICalibrationService>();
        IWheelChangeService wheelChange = services.GetRequiredService<IWheelChangeService>();

        double originalDiameterMm = calibration.Current.WheelDiameterMm;

        wheelChange.Begin();
        await wheelChange.CancelAsync("wang", CancellationToken.None);

        wheelChange.Current.Should().BeNull();
        calibration.Current.WheelDiameterMm.Should().Be(originalDiameterMm);
    }

    [Fact]
    public async Task Skipping_the_correction_still_restores_the_touch_mode()
    {
        await using ServiceProvider services = await BuildAsync();
        ICalibrationService calibration = services.GetRequiredService<ICalibrationService>();
        IWheelChangeService wheelChange = services.GetRequiredService<IWheelChangeService>();

        bool wasAutomatic = calibration.Current.TouchesAutomatically;

        wheelChange.Begin();
        wheelChange.SetNewWheelDiameter(1000.0);
        await wheelChange.SwitchToManualTouchAsync("wang", CancellationToken.None);
        wheelChange.RecordTrial(650.000, 650.400);
        wheelChange.SkipCorrection();
        await wheelChange.FinishAsync("wang", CancellationToken.None);

        calibration.Current.WheelDiameterMm.Should().BeApproximately(1000.0, 1e-6, "不修正就用标称值");
        calibration.Current.TouchesAutomatically.Should().Be(wasAutomatic);
    }

    [Fact]
    public async Task The_change_is_recorded_against_whoever_made_it()
    {
        // 砂轮直径是台账上要说清楚的事：这支辊磨小了，得查得到是谁改的。
        await using ServiceProvider services = await BuildAsync();
        ICalibrationService calibration = services.GetRequiredService<ICalibrationService>();
        IWheelChangeService wheelChange = services.GetRequiredService<IWheelChangeService>();

        wheelChange.Begin();
        wheelChange.SetNewWheelDiameter(1234.0);
        await wheelChange.SwitchToManualTouchAsync("wang", CancellationToken.None);
        wheelChange.RecordTrial(650.0, 650.0);
        wheelChange.AcceptCorrection();
        await wheelChange.FinishAsync("wang", CancellationToken.None);

        (await calibration.LoadAuditAsync(CancellationToken.None))
            .Should().Contain(entry =>
                entry.ParameterKey == CalibrationKeys.WheelDiameterMm && entry.ChangedBy == "wang");
    }

    [Fact]
    public async Task Nothing_works_before_the_wizard_is_started()
    {
        await using ServiceProvider services = await BuildAsync();
        IWheelChangeService wheelChange = services.GetRequiredService<IWheelChangeService>();

        wheelChange.Current.Should().BeNull();
        wheelChange.Invoking(w => w.SetNewWheelDiameter(1000.0))
            .Should().Throw<RollGrinder.Core.DomainException>();
    }
}
