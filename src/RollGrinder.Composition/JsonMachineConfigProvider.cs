using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;

namespace RollGrinder.Composition;

/// <summary>
/// 从 machine.json 与 tagmap.json 载入机床描述与变量映射，结果缓存。
/// 配置缺失或结构不合法时抛出 <see cref="GatewayException"/>，不做默认值兜底。
/// </summary>
public sealed class JsonMachineConfigProvider : IMachineConfigProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IAppOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);

    private MachineDescription? machine;
    private ITagMap? tagMap;

    public JsonMachineConfigProvider(IAppOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<MachineDescription> GetMachineAsync(CancellationToken cancellationToken)
    {
        if (this.machine is not null)
        {
            return this.machine;
        }

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.machine ??= MapMachine(
                await ReadAsync<MachineJson>(this.options.MachineConfigFilePath, cancellationToken).ConfigureAwait(false),
                this.options.MachineConfigFilePath);
            return this.machine;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<ITagMap> GetTagMapAsync(CancellationToken cancellationToken)
    {
        if (this.tagMap is not null)
        {
            return this.tagMap;
        }

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.tagMap ??= MapTagMap(
                await ReadAsync<TagMapJson>(this.options.TagMapFilePath, cancellationToken).ConfigureAwait(false),
                this.options.TagMapFilePath);
            return this.tagMap;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new GatewayException($"Configuration file '{path}' was not found.");
        }

        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            T? parsed = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            return parsed ?? throw new GatewayException($"Configuration file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new GatewayException($"Configuration file '{path}' is not valid JSON: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new GatewayException($"Configuration file '{path}' could not be read: {ex.Message}", ex);
        }
    }

    private static MachineDescription MapMachine(MachineJson json, string path)
    {
        ControllerJson controller = json.Controller ?? throw Missing(path, "controller");
        WorkpieceJson workpiece = json.Workpiece ?? throw Missing(path, "workpiece");

        var axes = new List<AxisDescription>();
        foreach (AxisJson axis in json.Axes ?? throw Missing(path, "axes"))
        {
            axes.Add(new AxisDescription(
                Required(axis.Name, path, "axes[].name"),
                Required(axis.Role, path, "axes[].role"),
                axis.IsPresent,
                ParseClosedLoop(axis.ClosedLoop, path),
                axis.MinPositionMm,
                axis.MaxPositionMm,
                axis.MaxFeedMmPerMin,
                axis.MaxSpeedRpm));
        }

        var channels = new List<MeasurementChannelDescription>();
        foreach (MeasurementChannelJson channel in json.MeasurementChannels ?? new List<MeasurementChannelJson>())
        {
            channels.Add(new MeasurementChannelDescription(
                Required(channel.Name, path, "measurementChannels[].name"),
                channel.IsPresent,
                Required(channel.Quantity, path, "measurementChannels[].quantity"),
                channel.ResolutionMicrometer));
        }

        return new MachineDescription(
            json.SchemaVersion,
            Required(json.MachineId, path, "machineId"),
            Required(json.DisplayName, path, "displayName"),
            new ControllerDescription(
                Required(controller.Kind, path, "controller.kind"),
                controller.ChannelNumber,
                controller.EndpointUrl),
            axes,
            channels,
            json.Options ?? new Dictionary<string, bool>(),
            json.Thresholds ?? new Dictionary<string, double>(),
            new WorkpieceLimits(
                workpiece.MinBodyLengthMm,
                workpiece.MaxBodyLengthMm,
                workpiece.MinDiameterMm,
                workpiece.MaxDiameterMm,
                workpiece.MaxWeightKg),
            json.StepTypeCodes ?? new Dictionary<string, int>());
    }

    private static ITagMap MapTagMap(TagMapJson json, string path)
    {
        var tags = new List<TagDescriptor>();
        foreach (TagJson tag in json.Tags ?? throw Missing(path, "tags"))
        {
            tags.Add(new TagDescriptor(
                Required(tag.Key, path, "tags[].key"),
                Required(tag.Address, path, "tags[].address"),
                ParseEnum<TagDataType>(tag.DataType, path, "tags[].dataType"),
                ParseEnum<TagAccess>(tag.Access, path, "tags[].access"),
                tag.Unit,
                tag.Scale ?? 1.0,
                tag.Description,
                tag.ArrayLength ?? 1));
        }

        return new TagMap(tags);
    }

    private static AxisClosedLoopKind ParseClosedLoop(string? value, string path) =>
        ParseEnum<AxisClosedLoopKind>(value, path, "axes[].closedLoop");

    private static TEnum ParseEnum<TEnum>(string? value, string path, string field)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse(Required(value, path, field), ignoreCase: true, out TEnum parsed))
        {
            throw new GatewayException($"Configuration file '{path}' has an unknown value '{value}' for '{field}'.");
        }

        return parsed;
    }

    private static string Required(string? value, string path, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Missing(path, field)
            : value;

    private static GatewayException Missing(string path, string field) =>
        new($"Configuration file '{path}' is missing required field '{field}'.");
}
