using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core;
using RollGrinder.Core.Compensation;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services;
using RollGrinder.Services.Audit;
using RollGrinder.Services.Jobs;
using RollGrinder.Services.Measurement;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 补偿子视图里的补偿设定（修改稿 3②、5.5）：配置文件给默认值，制造商可改，改了写改动记录；
/// 限幅不能超过 machine.json 的上限；算补偿用的是生效的那一组。
/// </summary>
public sealed class CompensationTuningTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    private async Task<ServiceProvider> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);

        await new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName))
            .MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);
        ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ICompensationTuningService>().LoadAsync(CancellationToken.None);
        return provider;
    }

    [Fact]
    public async Task Without_changes_the_configured_values_are_used()
    {
        await using ServiceProvider services = await BuildAsync();
        ICompensationTuningService tuning = services.GetRequiredService<ICompensationTuningService>();

        tuning.IsOverridden.Should().BeFalse();
        tuning.Current.Should().Be(tuning.Configured);
        tuning.Current.Gain.Should().Be(0.7, "hmi.sample.json 里的增益");
        tuning.MachineLimitMicrometer.Should().BeApproximately(40.0, 1e-9, "machine.sample.json 限幅 0.02 mm 半径量 = 40 µm 直径量");
    }

    [Fact]
    public async Task A_saved_change_is_used_kept_across_restarts_and_logged()
    {
        await using (ServiceProvider services = await BuildAsync())
        {
            ICompensationTuningService tuning = services.GetRequiredService<ICompensationTuningService>();
            await tuning.SaveAsync(new CompensationTuning(0.5, 3, 20.0), "maker", CancellationToken.None);

            tuning.IsOverridden.Should().BeTrue();
            tuning.Current.Should().Be(new CompensationTuning(0.5, 3, 20.0));

            IReadOnlyList<ChangeLogEntry> log = await services.GetRequiredService<IChangeLog>().ListAsync(10, CancellationToken.None);
            log.Should().HaveCount(3).And.OnlyContain(entry => entry.Area == ChangeLogAreas.Compensation && entry.ChangedBy == "maker");
            log.Single(entry => entry.Item == CompensationTuningKeys.Gain).Should().Match<ChangeLogEntry>(
                entry => entry.OldValue == "0.7" && entry.NewValue == "0.5");
        }

        await using (ServiceProvider restarted = await BuildAsync())
        {
            restarted.GetRequiredService<ICompensationTuningService>().Current.Should().Be(new CompensationTuning(0.5, 3, 20.0));
        }
    }

    [Fact]
    public async Task The_limit_cannot_be_raised_above_the_machine_limit()
    {
        await using ServiceProvider services = await BuildAsync();
        ICompensationTuningService tuning = services.GetRequiredService<ICompensationTuningService>();

        await tuning.Invoking(t => t.SaveAsync(new CompensationTuning(0.7, 5, 41.0), "maker", CancellationToken.None))
            .Should().ThrowAsync<DomainException>();
        await tuning.Invoking(t => t.SaveAsync(new CompensationTuning(0.7, 4, 20.0), "maker", CancellationToken.None))
            .Should().ThrowAsync<DomainException>("滤波窗口要是奇数");
        await tuning.Invoking(t => t.SaveAsync(new CompensationTuning(0.0, 5, 20.0), "maker", CancellationToken.None))
            .Should().ThrowAsync<DomainException>();

        tuning.IsOverridden.Should().BeFalse();
        (await services.GetRequiredService<IChangeLog>().ListAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_goes_back_to_the_configured_values_and_is_logged()
    {
        await using ServiceProvider services = await BuildAsync();
        ICompensationTuningService tuning = services.GetRequiredService<ICompensationTuningService>();
        await tuning.SaveAsync(new CompensationTuning(0.5, 3, 20.0), "maker", CancellationToken.None);

        await tuning.ResetAsync("maker", CancellationToken.None);

        tuning.IsOverridden.Should().BeFalse();
        tuning.Current.Should().Be(tuning.Configured);
        (await services.GetRequiredService<IChangeLog>().ListAsync(10, CancellationToken.None)).Should().HaveCount(6);
    }

    [Fact]
    public async Task The_compensation_is_computed_with_the_tuning_in_force()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<ICompensationTuningService>()
            .SaveAsync(new CompensationTuning(1.0, 1, 40.0), "maker", CancellationToken.None);
        await services.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);
        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(
            GrindingJob.Create(
                "J-1",
                "R-1",
                RollGeometry.FromDiameter(2000.0, 650.0),
                ProfileTypeKeys.Cylindrical,
                ParameterSet.Empty,
                new[] { new GrindingJobStep(1, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()) }),
            CancellationToken.None);
        await services.GetRequiredService<IMeasurementService>().SaveAsync(
            "J-1",
            Enumerable.Range(0, 11).Select(i => new MeasurementPoint(200.0 * i, 325.004)).ToArray(),
            "DiameterGauge",
            CancellationToken.None);

        CompensationResult result = await services.GetRequiredService<ICompensationService>()
            .ComputeAndStoreAsync("J-1", CancellationToken.None);

        result.Compensation.Points.Should().OnlyContain(
            point => Math.Abs(point.RadiusOffsetMm + 0.004) < 1e-9, "增益 1、不平滑：一次把 4 µm（半径量）全补回去");
    }

    public void Dispose() => this.workspace.Dispose();
}
