using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Data;
using RollGrinder.Services;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Diagnostics;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 诊断快照与现场备份：把现场的状态打包带走。
///
/// 现场打个电话说不清的时候，导一份发过来比来回念数字快得多。
/// </summary>
public sealed class DiagnosticsExportTests : IDisposable
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
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_snapshot_carries_the_machine_the_tags_and_the_alarms()
    {
        await using ServiceProvider services = await BuildAsync();
        services.GetRequiredService<IAlarmSink>().Raise(
            AlarmSeverity.Warning, "Alarm_MachineAlarm", "冷却液液位低", 700045);

        string path = Path.Combine(this.workspace.Root, "snapshot.txt");
        await services.GetRequiredService<IDiagnosticsExportService>()
            .ExportSnapshotAsync(path, CancellationToken.None);

        string text = await File.ReadAllTextAsync(path);

        text.Should().Contain("machineId:");
        text.Should().Contain("connection:");
        text.Should().Contain("## tags");
        text.Should().Contain("## alarms");

        // 报警按机床给的号原样写出来，现场按号查手册。
        text.Should().Contain("700045");
        text.Should().Contain("冷却液液位低");
    }

    [Fact]
    public async Task The_backup_holds_both_the_site_configuration_and_the_database()
    {
        // 升级时要保留的就是这两样（架构约束 ⑫）。
        await using ServiceProvider services = await BuildAsync();

        string path = Path.Combine(this.workspace.Root, "backup.zip");
        await services.GetRequiredService<IDiagnosticsExportService>()
            .BackupAsync(path, CancellationToken.None);

        using ZipArchive archive = ZipFile.OpenRead(path);
        string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();

        entries.Should().Contain(name => name.StartsWith("config/", StringComparison.Ordinal));
        entries.Should().Contain(name => name.StartsWith("data/", StringComparison.Ordinal));
        entries.Should().Contain(name => name.EndsWith("machine.json", StringComparison.Ordinal));
        entries.Should().Contain(name => name.EndsWith("tagmap.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backing_up_while_the_database_is_open_still_works()
    {
        // 数据库正开着。独占打开会失败，而"备份失败"比"备份到一个正在写的库"
        // 更难现场解释——所以按共享读取打开。
        await using ServiceProvider services = await BuildAsync();

        var database = new SqliteDatabase(Path.Combine(
            AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root).DataDirectory,
            SqliteDatabase.FileName));
        await using Microsoft.Data.Sqlite.SqliteConnection connection =
            await database.OpenAsync(CancellationToken.None);

        string path = Path.Combine(this.workspace.Root, "backup.zip");
        Func<Task> backup = () => services.GetRequiredService<IDiagnosticsExportService>()
            .BackupAsync(path, CancellationToken.None);

        await backup.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Exporting_twice_overwrites_rather_than_failing()
    {
        await using ServiceProvider services = await BuildAsync();
        IDiagnosticsExportService exports = services.GetRequiredService<IDiagnosticsExportService>();

        string path = Path.Combine(this.workspace.Root, "backup.zip");
        await exports.BackupAsync(path, CancellationToken.None);

        Func<Task> again = () => exports.BackupAsync(path, CancellationToken.None);

        await again.Should().NotThrowAsync();
    }
}
