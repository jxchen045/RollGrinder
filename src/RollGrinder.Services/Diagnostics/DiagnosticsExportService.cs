using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Alarms;
using RollGrinder.Services.Monitoring;

namespace RollGrinder.Services.Diagnostics;

/// <summary>
/// 诊断快照与现场备份。
///
/// 两件事都是"把现场的状态打包带走"：现场打个电话说不清的时候，
/// 导一份发过来比来回念数字快得多。
/// </summary>
public interface IDiagnosticsExportService
{
    /// <summary>
    /// 导一份诊断快照（纯文本）：连接状态、版本、当前所有变量的值、最近的报警。
    /// </summary>
    Task ExportSnapshotAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// 备份 config/ 与 data/ 成一个 zip。
    ///
    /// **只备份不恢复。** 恢复要停机、要核对版本、要有人盯着——
    /// 那是维护动作，不该做成界面上一个随手能按的键。
    /// </summary>
    Task BackupAsync(string filePath, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDiagnosticsExportService"/>
public sealed class DiagnosticsExportService : IDiagnosticsExportService
{
    private readonly IAppOptions options;
    private readonly IMachineMonitor monitor;
    private readonly IAlarmLog alarms;
    private readonly MachineDescription machine;
    private readonly ITagMap tagMap;
    private readonly TimeProvider timeProvider;

    public DiagnosticsExportService(
        IAppOptions options,
        IMachineMonitor monitor,
        IAlarmLog alarms,
        MachineDescription machine,
        ITagMap tagMap,
        TimeProvider timeProvider)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.machine = machine ?? throw new ArgumentNullException(nameof(machine));
        this.tagMap = tagMap ?? throw new ArgumentNullException(nameof(tagMap));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task ExportSnapshotAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        MachineStateSnapshot snapshot = this.monitor.Current;
        var text = new StringBuilder();

        text.AppendLine(CultureInfo.InvariantCulture, $"# RollGrinder diagnostics snapshot");
        text.AppendLine(CultureInfo.InvariantCulture, $"generatedAtUtc: {this.timeProvider.GetUtcNow():O}");
        text.AppendLine(CultureInfo.InvariantCulture, $"machineId: {this.machine.MachineId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"controller: {this.machine.Controller.Kind}");
        text.AppendLine(CultureInfo.InvariantCulture, $"connection: {snapshot.ConnectionState}");
        text.AppendLine(CultureInfo.InvariantCulture, $"snapshotAtUtc: {snapshot.CapturedAtUtc:O}");
        text.AppendLine(CultureInfo.InvariantCulture, $"mappedTags: {this.tagMap.Tags.Count}");
        text.AppendLine();

        // 当前所有变量的值原样摆出来，不做单位换算也不补默认值——
        // 诊断看的就是"机床到底报了什么"。
        text.AppendLine("## tags");
        foreach (TagValue value in snapshot.Values)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{value.Key}\t{value.Raw}\t{(value.IsGood ? "good" : "bad")}\t{value.SampledAtUtc:O}");
        }

        text.AppendLine();
        text.AppendLine("## alarms");
        foreach (AlarmEntry entry in this.alarms.Snapshot())
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{entry.RaisedAtUtc:O}\t{entry.Code}\t{entry.Severity}\t{entry.MessageResourceKey}\t{entry.Detail}");
        }

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(filePath, text.ToString(), new UTF8Encoding(true), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task BackupAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        // 同步 API：ZipArchive 没有异步的写法，而备份是人按一下、等一会儿的事。
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        AddDirectory(archive, this.options.ConfigDirectory, "config");
        AddDirectory(archive, this.options.DataDirectory, "data");

        return Task.CompletedTask;
    }

    /// <summary>
    /// 把一个目录整个放进 zip。
    ///
    /// 数据库文件正开着，所以按共享读取打开——独占打开会失败，
    /// 而"备份失败"比"备份到一个正在写的库"更难现场解释。
    /// </summary>
    private static void AddDirectory(ZipArchive archive, string directory, string prefix)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            ZipArchiveEntry entry = archive.CreateEntry(prefix + "/" + relative, CompressionLevel.Optimal);

            using Stream target = entry.Open();
            using var source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            source.CopyTo(target);
        }
    }
}
