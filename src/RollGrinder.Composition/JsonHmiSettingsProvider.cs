using System;
using System.IO;
using System.Text.Json;
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

    private static void Validate(HmiSettings settings, string path)
    {
        Require(settings.PollIntervalMs is >= 20 and <= 2000, path, nameof(settings.PollIntervalMs));
        Require(settings.UiRefreshHz is >= 5 and <= 10, path, nameof(settings.UiRefreshHz));
        Require(settings.ProfileSampleCount is >= 3 and <= 1001, path, nameof(settings.ProfileSampleCount));
        Require(settings.ChartHistorySeconds is >= 10 and <= 7200, path, nameof(settings.ChartHistorySeconds));
        Require(settings.RecordRetentionDays is >= 1 and <= 36500, path, nameof(settings.RecordRetentionDays));
        Require(settings.AlarmHistoryLimit is >= 10 and <= 10000, path, nameof(settings.AlarmHistoryLimit));
        Require(!string.IsNullOrWhiteSpace(settings.Culture), path, nameof(settings.Culture));
        Require(settings.CompensationGain is > 0.0 and <= 1.0, path, nameof(settings.CompensationGain));
        Require(
            settings.CompensationSmoothingPoints >= 1 && settings.CompensationSmoothingPoints % 2 == 1,
            path,
            nameof(settings.CompensationSmoothingPoints));
        Require(
            settings.ProfileToleranceDiameterMicrometer is > 0.0 and <= 1000.0,
            path,
            nameof(settings.ProfileToleranceDiameterMicrometer));
    }

    private static void Require(bool condition, string path, string field)
    {
        if (!condition)
        {
            throw new GatewayException($"Configuration file '{path}' has an out-of-range value for '{field}'.");
        }
    }
}
