using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Composition;

/// <summary>载入 hmi.json。取值有范围限制，越界直接报错而不是悄悄夹紧。</summary>
public static class JsonHmiSettingsProvider
{
    public const string FileName = "hmi.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 权限在配置里写成名字（Operator / Administrator / Manufacturer），比数字可读。
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static async Task<HmiSettings> LoadAsync(IAppOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        string path = Path.Combine(options.ConfigDirectory, FileName);
        if (!File.Exists(path))
        {
            throw new GatewayException($"Configuration file '{path}' was not found.");
        }

        HmiSettings settings;
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            settings = await JsonSerializer.DeserializeAsync<HmiSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new GatewayException($"Configuration file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new GatewayException($"Configuration file '{path}' is not valid JSON: {ex.Message}", ex);
        }

        Validate(settings, path);
        return settings;
    }

    /// <summary>
    /// 只改 hmi.json 里的界面语言（Ctrl+L，界面最终稿 4.6），其余字段与注释外的格式原样保留。
    /// 重启上位机后生效：界面文字在载入时取定，换语言不该让一半字是中文一半是英文。
    /// </summary>
    public static async Task SaveCultureAsync(IAppOptions options, string culture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);

        string path = Path.Combine(options.ConfigDirectory, FileName);
        System.Text.Json.Nodes.JsonNode root;
        try
        {
            string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            root = System.Text.Json.Nodes.JsonNode.Parse(
                text,
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?? throw new GatewayException($"Configuration file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new GatewayException($"Configuration file '{path}' is not valid JSON: {ex.Message}", ex);
        }

        root["culture"] = culture;
        string updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, updated + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(HmiSettings settings, string path)
    {
        Require(settings.PollIntervalMs is >= 20 and <= 2000, path, nameof(settings.PollIntervalMs));
        Require(settings.UiRefreshHz is >= 5 and <= 10, path, nameof(settings.UiRefreshHz));
        Require(settings.ProfileSampleCount is >= 3 and <= 1001, path, nameof(settings.ProfileSampleCount));
        Require(settings.ChartHistorySeconds is >= 10 and <= 7200, path, nameof(settings.ChartHistorySeconds));
        Require(settings.RecordRetentionDays is >= 1 and <= 36500, path, nameof(settings.RecordRetentionDays));
        Require(settings.AlarmHistoryLimit is >= 10 and <= 10000, path, nameof(settings.AlarmHistoryLimit));
        Require(!string.IsNullOrWhiteSpace(settings.Culture), path, nameof(settings.Culture));
        Require(settings.Layout is "auto" or "standard" or "compact", path, nameof(settings.Layout));
        Require(Enum.IsDefined(settings.DefaultRole), path, nameof(settings.DefaultRole));
        Require(settings.CompensationGain is > 0.0 and <= 1.0, path, nameof(settings.CompensationGain));
        Require(
            settings.CompensationSmoothingPoints >= 1 && settings.CompensationSmoothingPoints % 2 == 1,
            path,
            nameof(settings.CompensationSmoothingPoints));
    }

    private static void Require(bool condition, string path, string field)
    {
        if (!condition)
        {
            throw new GatewayException(
                $"Configuration file '{path}' has a missing or out-of-range value for '{field}'. "
                + StaleConfigHint(path));
        }
    }

    /// <summary>
    /// 旧版本生成的配置缺新字段时最常见。与其让人去猜该填什么，
    /// 不如直接说清楚"删掉它、重启会从模板重新生成"。
    /// </summary>
    internal static string StaleConfigHint(string path)
    {
        string sample = Path.GetFileNameWithoutExtension(path) + ConfigBootstrapper.SampleSuffix;
        return $"If this file was created by an older version, delete it and restart: "
            + $"it will be recreated from '{sample}'. Site-specific edits must then be re-applied.";
    }
}
