using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Core.Geometry;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 升级场景：程序目录换了一批文件之后，现场的 config/*.json 与 data/ 必须原样还在。
/// </summary>
public sealed class UpgradeTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    [Fact]
    public async Task An_upgrade_keeps_site_configuration_and_the_database()
    {
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, this.workspace.Root);
        string samples = this.workspace.CreateSampleDirectory();

        // 第一次安装：从模板生成配置，建库，存一支辊。
        await ConfigBootstrapper.EnsureConfigurationAsync(options, samples, CancellationToken.None);
        string siteMachineConfig = await File.ReadAllTextAsync(options.MachineConfigFilePath);
        await File.WriteAllTextAsync(
            options.MachineConfigFilePath,
            siteMachineConfig.Replace("\"machineId\": \"RG-01\"", "\"machineId\": \"RG-42\"", StringComparison.Ordinal));

        var database = new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName));
        await database.MigrateAsync(CancellationToken.None);
        var rolls = new SqliteRollRepository(database);
        await rolls.UpsertAsync(
            new RollRecord("R-site", "WR-site", RollGeometry.FromDiameter(2000.0, 650.0), null, DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        // 升级：模板被新版本覆盖，正式配置与数据库不动。
        foreach (string sample in Directory.EnumerateFiles(samples, "*.sample.json"))
        {
            await File.WriteAllTextAsync(sample, await File.ReadAllTextAsync(sample) + Environment.NewLine);
        }

        await ConfigBootstrapper.EnsureConfigurationAsync(options, samples, CancellationToken.None);
        int schemaVersion = await database.MigrateAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(options.MachineConfigFilePath)).Should().Contain("RG-42", "现场配置不得被模板覆盖");
        schemaVersion.Should().Be(SqliteDatabase.ExpectedSchemaVersion);
        (await rolls.GetAsync("R-site", CancellationToken.None)).Should().NotBeNull("升级不得丢数据");
    }

    [Fact]
    public async Task A_missing_configuration_file_is_restored_from_its_template_on_the_next_start()
    {
        AppOptions options = AppOptions.Parse(new[] { "--stub" }, this.workspace.Root);
        string samples = this.workspace.CreateSampleDirectory();
        await ConfigBootstrapper.EnsureConfigurationAsync(options, samples, CancellationToken.None);

        File.Delete(options.TagMapFilePath);

        await ConfigBootstrapper.EnsureConfigurationAsync(options, samples, CancellationToken.None);

        File.Exists(options.TagMapFilePath).Should().BeTrue();
        File.Exists(options.MachineConfigFilePath).Should().BeTrue();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        this.workspace.Dispose();
    }
}
