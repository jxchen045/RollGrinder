using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 轧辊数据：实机"轧辊数据"屏上属于这支辊本身的那几项。
///
/// 与几何分开：几何是算辊形要用的，这些是吊装、找正、验收要用的。
/// </summary>
public sealed class RollDataTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    private async Task<IRollRepository> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(
            options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        var database = new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName));
        await database.MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddDataStore(options);
        return services.BuildServiceProvider().GetRequiredService<IRollRepository>();
    }

    [Fact]
    public async Task A_roll_with_nothing_registered_reads_back_as_nothing_not_as_zero()
    {
        // 逼着填只会让人乱填一个数，而一个乱填的重量会让中心架托瓦按错的压力顶上去。
        IRollRepository rolls = await BuildAsync();
        await rolls.UpsertAsync(
            new RollRecord("R-1", "WR-1", Geometry, null, DateTimeOffset.UnixEpoch), CancellationToken.None);

        RollRecord? loaded = await rolls.GetAsync("R-1", CancellationToken.None);

        loaded!.Data.NetWeightKg.Should().BeNull();
        loaded.Data.CurveToleranceMicrometer.Should().BeNull();
        loaded.Data.TotalWeightKg.Should().BeNull();
    }

    [Fact]
    public async Task The_registered_values_survive_a_round_trip()
    {
        IRollRepository rolls = await BuildAsync();
        var sheet = new RollDataSheet(120.0, 1800.0, 10.0, 24500.0, 1800.0, 1750.0);

        await rolls.UpsertAsync(
            new RollRecord("R-1", "WR-1", Geometry, "9Cr2Mo", DateTimeOffset.UnixEpoch) { Data = sheet },
            CancellationToken.None);

        RollRecord? loaded = await rolls.GetAsync("R-1", CancellationToken.None);

        loaded!.Data.Should().Be(sheet);
        loaded.Material.Should().Be("9Cr2Mo");
    }

    [Fact]
    public void The_lifting_weight_needs_all_three_pieces()
    {
        // 少算一个轴承箱会让人按偏轻的重量挂吊具。
        new RollDataSheet(null, null, null, 24500.0, 1800.0, 1750.0).TotalWeightKg.Should().Be(28050.0);
        new RollDataSheet(null, null, null, 24500.0, 1800.0, null).TotalWeightKg.Should().BeNull();
        new RollDataSheet(null, null, null, null, 1800.0, 1750.0).TotalWeightKg.Should().BeNull();
    }

    [Fact]
    public async Task Registering_the_data_later_does_not_disturb_the_geometry()
    {
        // 辊件档案先建了才登记这些，所以更新不能把长度直径顺手覆盖掉。
        IRollRepository rolls = await BuildAsync();
        await rolls.UpsertAsync(
            new RollRecord("R-1", "WR-1", Geometry, null, DateTimeOffset.UnixEpoch), CancellationToken.None);

        RollRecord existing = (await rolls.GetAsync("R-1", CancellationToken.None))!;
        await rolls.UpsertAsync(
            existing with { Data = new RollDataSheet(120.0, null, null, null, null, null) },
            CancellationToken.None);

        RollRecord? loaded = await rolls.GetAsync("R-1", CancellationToken.None);

        loaded!.Geometry.Should().Be(Geometry);
        loaded.Data.GrindStartPositionMm.Should().Be(120.0);
    }
}
