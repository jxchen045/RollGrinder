using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;

namespace RollGrinder.Composition;

/// <summary>界面上能编辑的两份配置文件。</summary>
public enum ConfigFileKind
{
    /// <summary>machine.json：机床描述。</summary>
    Machine = 0,

    /// <summary>tagmap.json：逻辑变量到 NC / PLC 地址的映射。</summary>
    TagMap = 1,
}

/// <summary>两个版本之间变了的一项：路径（例如 axes[0].maxPositionMm）、原值、新值。</summary>
public sealed record ConfigDiff(string Path, string? OldValue, string? NewValue);

/// <summary>配置里一处不成立的地方：哪一项（路径，整份文件的问题为空串）、为什么（资源键 + 参数）。</summary>
public sealed record ConfigIssue(string Path, string ReasonResourceKey, string? Detail = null);

/// <summary>
/// 机床配置与标签映射的编辑存取（修改稿 5.8）。
///
/// 按 JSON 树编辑，不经过强类型模型——文件里界面不认识的字段原样保留。
/// 保存前用启动时同一套解析校验一遍（外加几条跨字段规则），不成立就不写；
/// 写之前把原文件备份到 config/backup/，写的时候先写临时文件再替换，半截断电也不会留下一份坏文件。
/// **新配置在上位机重启后才生效**：正在运行的连接与监视都还按启动时那一份。
/// </summary>
public sealed class ConfigDocumentStore
{
    /// <summary>备份子目录名。</summary>
    public const string BackupFolderName = "backup";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // 标签映射的说明是中文：写回去要还是中文，不能变成 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions LeafOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAppOptions options;
    private readonly TimeProvider timeProvider;

    public ConfigDocumentStore(IAppOptions options, TimeProvider timeProvider)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>这份配置的文件路径。</summary>
    public string PathOf(ConfigFileKind kind) =>
        kind == ConfigFileKind.Machine ? this.options.MachineConfigFilePath : this.options.TagMapFilePath;

    /// <summary>备份放在哪儿。</summary>
    public string BackupDirectory => Path.Combine(this.options.ConfigDirectory, BackupFolderName);

    /// <summary>读出文件（JSON 树）。</summary>
    public async Task<JsonObject> LoadAsync(ConfigFileKind kind, CancellationToken cancellationToken)
    {
        string path = PathOf(kind);
        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(text, path);
    }

    /// <summary>
    /// 校验一份配置：先按启动时同一套解析走一遍（缺字段、枚举不认识、变量重名……），
    /// 再查几条跨字段的规则。成立返回空表。
    /// </summary>
    public static IReadOnlyList<ConfigIssue> Validate(ConfigFileKind kind, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var issues = new List<ConfigIssue>();
        try
        {
            string text = document.ToJsonString();
            if (kind == ConfigFileKind.Machine)
            {
                MachineJson json = JsonSerializer.Deserialize<MachineJson>(text, JsonMachineConfigProvider.SerializerOptions)
                    ?? throw new GatewayException("empty");
                JsonMachineConfigProvider.MapMachine(json, "machine.json");
            }
            else
            {
                TagMapJson json = JsonSerializer.Deserialize<TagMapJson>(text, JsonMachineConfigProvider.SerializerOptions)
                    ?? throw new GatewayException("empty");
                JsonMachineConfigProvider.MapTagMap(json, "tagmap.json");
            }
        }
        catch (Exception ex) when (ex is GatewayException or JsonException or InvalidOperationException or FormatException)
        {
            issues.Add(new ConfigIssue(string.Empty, "Cfg_Issue_Structure", ex.Message));
        }

        issues.AddRange(kind == ConfigFileKind.Machine ? MachineRules(document) : TagMapRules(document));
        return issues;
    }

    /// <summary>
    /// 保存：校验、备份原文件、写新文件。返回备份文件的路径。不成立时抛 <see cref="GatewayException"/>，文件不动。
    /// </summary>
    public async Task<string> SaveAsync(ConfigFileKind kind, JsonObject document, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConfigIssue> issues = Validate(kind, document);
        if (issues.Count > 0)
        {
            throw new GatewayException(
                $"The {kind} configuration is not valid: {issues[0].Path} {issues[0].ReasonResourceKey} {issues[0].Detail}".Trim());
        }

        string path = PathOf(kind);
        Directory.CreateDirectory(BackupDirectory);
        string backup = Path.Combine(
            BackupDirectory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Path.GetFileNameWithoutExtension(path)}.{this.timeProvider.GetUtcNow():yyyyMMdd-HHmmssfff}.json"));
        if (File.Exists(path))
        {
            File.Copy(path, backup, overwrite: true);
        }

        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, document.ToJsonString(WriteOptions) + Environment.NewLine, cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        return backup;
    }

    /// <summary>这份配置的备份，新的在前。</summary>
    public IReadOnlyList<string> ListBackups(ConfigFileKind kind)
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return Array.Empty<string>();
        }

        string prefix = Path.GetFileNameWithoutExtension(PathOf(kind)) + ".";
        return Directory.EnumerateFiles(BackupDirectory, prefix + "*.json")
            .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>读最近一份备份（"恢复上一版"）；没有备份返回 null。</summary>
    public async Task<JsonObject?> LoadLatestBackupAsync(ConfigFileKind kind, CancellationToken cancellationToken)
    {
        string? latest = ListBackups(kind).FirstOrDefault();
        if (latest is null)
        {
            return null;
        }

        return Parse(await File.ReadAllTextAsync(latest, cancellationToken).ConfigureAwait(false), latest);
    }

    /// <summary>两份之间逐项比：叶子值按路径比，一边没有的算新增或删除。</summary>
    public static IReadOnlyList<ConfigDiff> Diff(JsonNode? before, JsonNode? after)
    {
        var old = new Dictionary<string, string?>(StringComparer.Ordinal);
        var @new = new Dictionary<string, string?>(StringComparer.Ordinal);
        Flatten(before, string.Empty, old);
        Flatten(after, string.Empty, @new);

        var diffs = new List<ConfigDiff>();
        foreach (string path in old.Keys.Union(@new.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            old.TryGetValue(path, out string? oldValue);
            @new.TryGetValue(path, out string? newValue);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                diffs.Add(new ConfigDiff(path, oldValue, newValue));
            }
        }

        return diffs;
    }

    /// <summary>把 JSON 树摊成"路径 → 叶子值"。数组按下标（tags 数组按 key，免得插一行后面全算改动）。</summary>
    private static void Flatten(JsonNode? node, string path, IDictionary<string, string?> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string name, JsonNode? child) in obj)
                {
                    Flatten(child, path.Length == 0 ? name : path + "." + name, into);
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    string label = array[i] is JsonObject item && item["key"] is JsonValue key && key.TryGetValue(out string? keyText)
                        ? keyText
                        : i.ToString(CultureInfo.InvariantCulture);
                    Flatten(array[i], string.Create(CultureInfo.InvariantCulture, $"{path}[{label}]"), into);
                }

                break;
            case null:
                into[path] = null;
                break;
            default:
                into[path] = node.ToJsonString(LeafOptions);
                break;
        }
    }

    private static IEnumerable<ConfigIssue> MachineRules(JsonObject document)
    {
        if (document["workpiece"] is JsonObject workpiece)
        {
            foreach ((string min, string max) in new[] { ("minBodyLengthMm", "maxBodyLengthMm"), ("minDiameterMm", "maxDiameterMm") })
            {
                if (Number(workpiece[min]) is double low && Number(workpiece[max]) is double high && !(low < high))
                {
                    yield return new ConfigIssue("workpiece." + max, "Cfg_Issue_MaxNotAboveMin");
                }
            }

            foreach ((string name, JsonNode? value) in workpiece)
            {
                if (Number(value) is double number && !(number > 0.0))
                {
                    yield return new ConfigIssue("workpiece." + name, "Cfg_Issue_MustBePositive");
                }
            }
        }

        if (document["controller"] is JsonObject controller)
        {
            if (Number(controller["channelNumber"]) is double channel && channel < 1)
            {
                yield return new ConfigIssue("controller.channelNumber", "Cfg_Issue_MustBePositive");
            }

            foreach (string timeout in new[] { "sessionTimeoutMs", "operationTimeoutMs" })
            {
                if (Number(controller[timeout]) is double ms && !(ms > 0.0))
                {
                    yield return new ConfigIssue("controller." + timeout, "Cfg_Issue_MustBePositive");
                }
            }
        }

        if (document["axes"] is JsonArray axes)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < axes.Count; i++)
            {
                if (axes[i] is not JsonObject axis)
                {
                    continue;
                }

                string prefix = string.Create(CultureInfo.InvariantCulture, $"axes[{i}].");
                if (axis["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? name) && !names.Add(name))
                {
                    yield return new ConfigIssue(prefix + "name", "Cfg_Issue_Duplicate");
                }

                if (Number(axis["minPositionMm"]) is double low && Number(axis["maxPositionMm"]) is double high && !(low < high))
                {
                    yield return new ConfigIssue(prefix + "maxPositionMm", "Cfg_Issue_MaxNotAboveMin");
                }

                foreach (string limit in new[] { "maxFeedMmPerMin", "maxSpeedRpm" })
                {
                    if (Number(axis[limit]) is double value && !(value > 0.0))
                    {
                        yield return new ConfigIssue(prefix + limit, "Cfg_Issue_MustBePositive");
                    }
                }
            }
        }

        if (document["thresholds"] is JsonObject thresholds)
        {
            foreach ((string name, JsonNode? value) in thresholds)
            {
                // 固定位置（position…Mm：测量架归位、磨架安全位……）是坐标，可以是 0 或负数；其余都是上限 / 速度，要为正。
                bool isPosition = name.StartsWith("position", StringComparison.Ordinal)
                    && !name.StartsWith("positioning", StringComparison.Ordinal);
                if (!isPosition && Number(value) is double number && !(number > 0.0))
                {
                    yield return new ConfigIssue("thresholds." + name, "Cfg_Issue_MustBePositive");
                }
            }
        }

        foreach (string codes in new[] { "stepTypeCodes", "auxiliaryActionCodes" })
        {
            if (document[codes] is not JsonObject map)
            {
                continue;
            }

            var seen = new HashSet<double>();
            foreach ((string name, JsonNode? value) in map)
            {
                if (Number(value) is not double code || code != Math.Floor(code))
                {
                    yield return new ConfigIssue(codes + "." + name, "Cfg_Issue_MustBeInteger");
                }
                else if (!seen.Add(code))
                {
                    yield return new ConfigIssue(codes + "." + name, "Cfg_Issue_Duplicate");
                }
            }
        }
    }

    private static IEnumerable<ConfigIssue> TagMapRules(JsonObject document)
    {
        if (document["tags"] is not JsonArray tags)
        {
            yield break;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in tags)
        {
            if (node is not JsonObject tag || tag["key"] is not JsonValue keyValue || !keyValue.TryGetValue(out string? key))
            {
                continue;
            }

            if (!keys.Add(key))
            {
                yield return new ConfigIssue("tags[" + key + "].key", "Cfg_Issue_Duplicate");
            }

            if (tag["address"] is not JsonValue address || !address.TryGetValue(out string? addressText) || string.IsNullOrWhiteSpace(addressText))
            {
                yield return new ConfigIssue("tags[" + key + "].address", "Cfg_Issue_Required");
            }
        }
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    private static JsonObject Parse(string text, string path)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new GatewayException($"Configuration file '{path}' is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new GatewayException($"Configuration file '{path}' is not valid JSON: {ex.Message}", ex);
        }
    }
}
