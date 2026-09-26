using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Nc;
using RollGrinder.Services;
using RollGrinder.Services.Records;
using RollGrinder.Sim;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// "磨得快得不可能"的判定。第一轮甲方测试：预计一个多小时的辊 3 秒就报磨完，界面上毫无提示。
/// </summary>
public sealed class CyclePlausibilityMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 25, 40, TimeSpan.Zero);

    [Fact]
    public void Three_seconds_for_an_hour_long_program_is_implausible()
    {
        var monitor = new CyclePlausibilityMonitor(MachineTimeScale.RealTime);
        monitor.OnHandedOver("J1", TimeSpan.FromMinutes(64), T0);

        CyclePlausibility? verdict = monitor.OnCompleted("J1", T0.AddSeconds(3));

        verdict.Should().NotBeNull();
        verdict!.IsImplausible.Should().BeTrue();
        verdict.Actual.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Finishing_in_about_the_estimated_time_is_fine()
    {
        var monitor = new CyclePlausibilityMonitor(MachineTimeScale.RealTime);
        monitor.OnHandedOver("J1", TimeSpan.FromMinutes(64), T0);

        monitor.OnCompleted("J1", T0.AddMinutes(50))!.IsImplausible.Should().BeFalse();
    }

    [Fact]
    public void Simulated_speed_is_taken_into_account()
    {
        // 仿真 20 倍速：墙上 3 分钟 = 机床 60 分钟，正常。
        var monitor = new CyclePlausibilityMonitor(new MachineTimeScale(20.0));
        monitor.OnHandedOver("J1", TimeSpan.FromMinutes(64), T0);

        CyclePlausibility verdict = monitor.OnCompleted("J1", T0.AddMinutes(3))!;

        verdict.IsImplausible.Should().BeFalse();
        verdict.Actual.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void Short_estimates_and_unknown_jobs_are_not_judged()
    {
        var monitor = new CyclePlausibilityMonitor(MachineTimeScale.RealTime);
        monitor.OnHandedOver("short", TimeSpan.FromSeconds(30), T0);

        monitor.OnCompleted("short", T0.AddSeconds(1))!.IsImplausible.Should().BeFalse("太短的估算本身误差就大");
        monitor.OnCompleted("never-handed-over", T0).Should().BeNull("上位机重启过，没有预计值可比");
    }

    [Fact]
    public void Each_handover_is_judged_once()
    {
        var monitor = new CyclePlausibilityMonitor(MachineTimeScale.RealTime);
        monitor.OnHandedOver("J1", TimeSpan.FromMinutes(10), T0);

        monitor.OnCompleted("J1", T0.AddSeconds(1)).Should().NotBeNull();
        monitor.OnCompleted("J1", T0.AddSeconds(2)).Should().BeNull();
    }

    /// <summary>
    /// 防误报：一支正常的程序真实下发到仿真机床，跑完的用时要远高于报警线。
    /// 仿真每个单程算一道，用时约为估算（按往返）的一半——离 <see cref="CyclePlausibilityMonitor.MinimumRatio"/> 还有富余。
    /// </summary>
    [Fact]
    public async Task A_normal_program_run_on_the_simulator_is_not_flagged()
    {
        using var workspace = new TempWorkspace();
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);
        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);
        await new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName)).MigrateAsync(CancellationToken.None);
        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);
        await using ServiceProvider provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<GrindingStepTypeRegistry>();
        string[] keys =
        {
            StepTypeKeys.Start, StepTypeKeys.Rough, StepTypeKeys.SemiFinish, StepTypeKeys.Finish,
            StepTypeKeys.SparkOut, StepTypeKeys.Measure, StepTypeKeys.End,
        };
        var geometry = RollGeometry.FromDiameter(2000.0, 650.0);
        GrindingJob job = GrindingJob.Create(
            "J-SIM",
            "R-SIM",
            geometry,
            ProfileTypeKeys.Cylindrical,
            new CylindricalProfileType().Schema.CreateDefaults(),
            keys.Select((key, i) => new GrindingJobStep(i + 1, key, registry.Get(key).Schema.CreateDefaults())).ToArray());

        NcDownload download = provider.GetRequiredService<NcJobTranslator>().Translate(job, null, 64, DateTimeOffset.UtcNow);
        var sim = new SimulatedMachine(machine);
        foreach (TagWrite write in download.Writes)
        {
            sim.Write(write.LogicalName, write.Value);
        }

        sim.ChannelState.Should().Be(NcChannelState.Running);
        double seconds = 0.0;
        while (sim.ChannelState == NcChannelState.Running && seconds < 6 * 3600.0)
        {
            sim.Advance(TimeSpan.FromSeconds(0.5));
            seconds += 0.5;
        }

        sim.ChannelState.Should().Be(NcChannelState.Reset, "程序要能走完");
        TimeSpan estimate = CyclePlausibilityMonitor.Estimate(download.Plans, geometry);
        estimate.Should().BeGreaterThan(CyclePlausibilityMonitor.MinimumEstimate, "不然这条测试什么也没测");
        (seconds / estimate.TotalSeconds).Should().BeGreaterThan(CyclePlausibilityMonitor.MinimumRatio * 1.5);
    }
}
