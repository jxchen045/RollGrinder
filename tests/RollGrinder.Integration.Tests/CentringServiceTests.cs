using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Centring;
using RollGrinder.Data;
using RollGrinder.Services;
using RollGrinder.Services.Measurement;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 对中服务：只读机床，不动机床。测量臂开到哪一端由操作工自己操作，
/// 这里只在按下"记录本端"时把当时的几个数抓下来。
/// </summary>
public sealed class CentringServiceTests : IDisposable
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
        await provider.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);
        return provider;
    }

    [Fact]
    public async Task Nothing_to_compare_until_both_ends_are_captured()
    {
        // 只有一端时也不能凑合着比：差算不出来，判出来的"合格"是假的。
        await using ServiceProvider services = await BuildAsync();
        ICentringService centring = services.GetRequiredService<ICentringService>();

        centring.Compare().Should().BeNull();

        await centring.CaptureAsync(RollEnd.Head, CancellationToken.None);

        centring.Reading(RollEnd.Head).Should().NotBeNull();
        centring.Reading(RollEnd.Tail).Should().BeNull();
        centring.Compare().Should().BeNull();

        await centring.CaptureAsync(RollEnd.Tail, CancellationToken.None);

        centring.Compare().Should().NotBeNull();
    }

    [Fact]
    public async Task A_reading_carries_the_position_it_was_taken_at()
    {
        // 位置不同的两组读数没有可比性，所以位置得跟着读数一起记下来。
        await using ServiceProvider services = await BuildAsync();
        ICentringService centring = services.GetRequiredService<ICentringService>();

        CentringReading reading = await centring.CaptureAsync(RollEnd.Head, CancellationToken.None);

        reading.End.Should().Be(RollEnd.Head);
        reading.DiameterMm.Should().BePositive();
        reading.CapturedAtUtc.Should().NotBe(default);

        // 轴名来自 machine.json，不在代码里写死。
        centring.CarriageAxisName.Should().NotBeNullOrWhiteSpace();
        centring.InfeedAxisName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Capturing_the_same_end_twice_replaces_it()
    {
        // 记错了就重记一次，不必先清空——现场没人想为了改一个数按两下。
        await using ServiceProvider services = await BuildAsync();
        ICentringService centring = services.GetRequiredService<ICentringService>();

        CentringReading first = await centring.CaptureAsync(RollEnd.Head, CancellationToken.None);
        CentringReading second = await centring.CaptureAsync(RollEnd.Head, CancellationToken.None);

        centring.Reading(RollEnd.Head).Should().BeSameAs(second);
        second.CapturedAtUtc.Should().BeOnOrAfter(first.CapturedAtUtc);
    }

    [Fact]
    public async Task Clearing_starts_the_alignment_over()
    {
        // 换辊之后上一支的对中记录与这一支无关。
        await using ServiceProvider services = await BuildAsync();
        ICentringService centring = services.GetRequiredService<ICentringService>();

        await centring.CaptureAsync(RollEnd.Head, CancellationToken.None);
        await centring.CaptureAsync(RollEnd.Tail, CancellationToken.None);

        centring.Clear();

        centring.Reading(RollEnd.Head).Should().BeNull();
        centring.Reading(RollEnd.Tail).Should().BeNull();
        centring.Compare().Should().BeNull();
    }

    [Fact]
    public async Task The_comparison_uses_the_centring_tolerance_from_the_settings_page()
    {
        // 对中公差是现场标定值，不是辊形公差——两者混用会把一台正常的机床判成不合格。
        await using ServiceProvider services = await BuildAsync();
        ICentringService centring = services.GetRequiredService<ICentringService>();

        await centring.CaptureAsync(RollEnd.Head, CancellationToken.None);
        await centring.CaptureAsync(RollEnd.Tail, CancellationToken.None);

        CentringComparison comparison = centring.Compare()!;
        double expected = services.GetRequiredService<RollGrinder.Services.Calibration.ICalibrationService>()
            .Current.CentringToleranceMicrometer;

        comparison.ToleranceMicrometer.Should().Be(expected);
    }
}
