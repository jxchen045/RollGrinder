using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using RollGrinder.Core;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;

namespace RollGrinder.Data;

/// <summary>
/// 辊形、程序、作业的 JSON 形式：库版本留档（旧版本只读）与磨削记录的下发快照用它。
/// 参数值按 <see cref="ParameterValue.ToInvariantString"/> 存，与库表里的写法一致，不随界面语言变。
/// </summary>
public static class LibrarySnapshotJson
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Write(RollProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var node = new JsonObject
        {
            ["profileId"] = profile.ProfileId,
            ["name"] = profile.Name,
            ["version"] = profile.Version,
            ["bodyLengthMm"] = profile.BodyLengthMm,
            ["nominalDiameterMm"] = profile.NominalDiameterMm,
            ["toleranceMicrometer"] = profile.ToleranceMicrometer,
            ["createdAtUtc"] = profile.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["modifiedAtUtc"] = profile.ModifiedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["profile"] = WriteProfile(profile.Profile),
        };
        return node.ToJsonString(Indented);
    }

    public static RollProfileDefinition ReadProfile(string json)
    {
        JsonObject node = Parse(json);
        return new RollProfileDefinition(
            Text(node, "profileId"),
            Text(node, "name"),
            node["bodyLengthMm"]!.GetValue<double>(),
            ReadComposite(node["profile"]!.AsObject()),
            DateTimeOffset.Parse(Text(node, "createdAtUtc"), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(Text(node, "modifiedAtUtc"), CultureInfo.InvariantCulture))
        {
            Version = node["version"]?.GetValue<int>() ?? 1,
            NominalDiameterMm = node["nominalDiameterMm"]?.GetValue<double?>(),
            ToleranceMicrometer = node["toleranceMicrometer"]?.GetValue<double?>(),
        };
    }

    public static string Write(GrindingProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var node = new JsonObject
        {
            ["programId"] = program.ProgramId,
            ["name"] = program.Name,
            ["version"] = program.Version,
            ["standardStockMicrometer"] = program.StandardStockMicrometer,
            ["applicableRollKind"] = program.ApplicableRollKind.ToString(),
            ["applicableMaterial"] = program.ApplicableMaterial,
            ["createdAtUtc"] = program.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["modifiedAtUtc"] = program.ModifiedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["options"] = WriteParameters(program.ProgramOptions),
            ["steps"] = WriteSteps(program.Steps),
        };
        return node.ToJsonString(Indented);
    }

    public static GrindingProgram ReadProgram(string json)
    {
        JsonObject node = Parse(json);
        GrindingProgram program = GrindingProgram.Create(
            Text(node, "programId"),
            Text(node, "name"),
            ReadSteps(node["steps"]!.AsArray()),
            DateTimeOffset.Parse(Text(node, "createdAtUtc"), CultureInfo.InvariantCulture),
            ReadParameters(node["options"]?.AsArray()));
        return program with
        {
            ModifiedAtUtc = DateTimeOffset.Parse(Text(node, "modifiedAtUtc"), CultureInfo.InvariantCulture),
            Version = node["version"]?.GetValue<int>() ?? 1,
            StandardStockMicrometer = node["standardStockMicrometer"]?.GetValue<double?>(),
            ApplicableRollKind = Enum.TryParse(node["applicableRollKind"]?.GetValue<string>(), out RollKind kind) ? kind : RollKind.Unspecified,
            ApplicableMaterial = node["applicableMaterial"]?.GetValue<string?>(),
        };
    }

    /// <summary>
    /// 下发快照（关系设计 V3）：调整后的工序参数、合成后的辊形点列（直径量 µm）、核对结果。
    /// 之后改辊形库、程序库都不影响它——售后排查看的就是"当时实际下发了什么"。
    /// </summary>
    /// <param name="job">下发的作业。</param>
    /// <param name="points">合成后的辊形点列（辊身 Z mm，直径量 µm）。</param>
    /// <param name="checks">核对结果（项、结论、说明键）。</param>
    /// <param name="operatorName">谁下发的。</param>
    /// <param name="atUtc">下发时刻。</param>
    public static string WriteDownload(
        GrindingJob job,
        IReadOnlyList<(double ZMm, double DiameterMicrometer)> points,
        IReadOnlyList<(string Item, string Status, string MessageKey)> checks,
        string operatorName,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        var node = new JsonObject
        {
            ["jobId"] = job.JobId,
            ["rollId"] = job.RollId,
            ["downloadedAtUtc"] = atUtc.ToString("O", CultureInfo.InvariantCulture),
            ["operator"] = operatorName,
            ["profileName"] = job.ProfileName,
            ["profileVersion"] = job.ProfileVersion,
            ["programName"] = job.ProgramName,
            ["programVersion"] = job.ProgramVersion,
            ["bodyLengthMm"] = job.Geometry.BodyLengthMm,
            ["startDiameterMm"] = job.StartDiameterMm,
            ["stockMicrometer"] = job.StockMicrometer,
            ["targetDiameterMm"] = job.TargetDiameterMm,
            ["scrapDiameterMm"] = job.ScrapDiameterMm,
            ["deviation"] = job.Deviation.ToString(),
            ["deviationReason"] = job.DeviationReason,
            ["options"] = WriteParameters(job.ProgramOptions),
            ["steps"] = WriteSteps(job.Steps),
            ["points"] = new JsonArray(points.Select(p => (JsonNode)new JsonArray(Math.Round(p.ZMm, 3), Math.Round(p.DiameterMicrometer, 3))).ToArray()),
            ["checks"] = new JsonArray(checks.Select(c => (JsonNode)new JsonObject
            {
                ["item"] = c.Item,
                ["status"] = c.Status,
                ["message"] = c.MessageKey,
            }).ToArray()),
        };
        return node.ToJsonString(Indented);
    }

    /// <summary>读回下发快照里的工序与点列，供记录页"下发参数"显示。</summary>
    public static DownloadSnapshot ReadDownload(string json)
    {
        JsonObject node = Parse(json);
        IReadOnlyList<GrindingJobStep> steps = ReadSteps(node["steps"]!.AsArray());
        var points = node["points"]!.AsArray()
            .Select(p => (p![0]!.GetValue<double>(), p[1]!.GetValue<double>()))
            .ToArray();
        var checks = node["checks"]?.AsArray()
            .Select(c => (Text(c!.AsObject(), "item"), Text(c!.AsObject(), "status"), Text(c!.AsObject(), "message")))
            .ToArray() ?? Array.Empty<(string, string, string)>();
        return new DownloadSnapshot(
            DateTimeOffset.Parse(Text(node, "downloadedAtUtc"), CultureInfo.InvariantCulture),
            node["operator"]?.GetValue<string>() ?? string.Empty,
            node["profileName"]?.GetValue<string?>(),
            node["profileVersion"]?.GetValue<int?>(),
            node["programName"]?.GetValue<string?>(),
            node["programVersion"]?.GetValue<int?>(),
            node["startDiameterMm"]?.GetValue<double?>(),
            node["stockMicrometer"]?.GetValue<double?>(),
            steps,
            points,
            checks);
    }

    private static JsonObject WriteProfile(CompositeRollProfile profile) => new()
    {
        ["layout"] = profile.Layout.ToString(),
        ["segments"] = new JsonArray(profile.Segments.Select(segment => (JsonNode)new JsonObject
        {
            ["order"] = segment.Order,
            ["type"] = segment.ProfileTypeKey,
            ["fromMm"] = segment.FromMm,
            ["toMm"] = segment.ToMm,
            ["mirrored"] = segment.IsMirrored,
            ["parameters"] = WriteParameters(segment.Parameters),
        }).ToArray()),
    };

    private static CompositeRollProfile ReadComposite(JsonObject node)
    {
        ProfileLayout layout = Enum.Parse<ProfileLayout>(Text(node, "layout"));
        IEnumerable<RollProfileSegment> segments = node["segments"]!.AsArray().Select(s =>
        {
            JsonObject segment = s!.AsObject();
            return RollProfileSegment.Create(
                segment["order"]!.GetValue<int>(),
                Text(segment, "type"),
                segment["fromMm"]!.GetValue<double>(),
                segment["toMm"]!.GetValue<double>(),
                ReadParameters(segment["parameters"]?.AsArray()),
                segment["mirrored"]?.GetValue<bool>() ?? false);
        });
        return new CompositeRollProfile(segments, layout);
    }

    private static JsonArray WriteSteps(IReadOnlyList<GrindingJobStep> steps) =>
        new(steps.Select(step => (JsonNode)new JsonObject
        {
            ["order"] = step.Order,
            ["type"] = step.StepTypeKey,
            ["parameters"] = WriteParameters(step.Parameters),
        }).ToArray());

    private static IReadOnlyList<GrindingJobStep> ReadSteps(JsonArray steps) =>
        steps.Select(s =>
        {
            JsonObject step = s!.AsObject();
            return new GrindingJobStep(step["order"]!.GetValue<int>(), Text(step, "type"), ReadParameters(step["parameters"]?.AsArray()));
        }).ToArray();

    private static JsonArray WriteParameters(ParameterSet parameters) =>
        new(parameters.Keys.Select(key => (JsonNode)new JsonArray(key, parameters.Get(key).Kind.ToString(), parameters.Get(key).ToInvariantString())).ToArray());

    private static ParameterSet ReadParameters(JsonArray? array)
    {
        if (array is null)
        {
            return ParameterSet.Empty;
        }

        return new ParameterSet(array.Select(item => new KeyValuePair<string, ParameterValue>(
            item![0]!.GetValue<string>(),
            ParameterValue.Parse(Enum.Parse<ParameterValueKind>(item[1]!.GetValue<string>()), item[2]!.GetValue<string>()))));
    }

    private static JsonObject Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            return JsonNode.Parse(json)!.AsObject();
        }
        catch (JsonException ex)
        {
            throw new DataStoreException("Stored snapshot is not valid JSON: " + ex.Message, ex);
        }
    }

    private static string Text(JsonObject node, string key) =>
        node[key]?.GetValue<string>() ?? throw new DataStoreException($"Stored snapshot is missing '{key}'.");
}

/// <summary>读回来的下发快照。</summary>
public sealed record DownloadSnapshot(
    DateTimeOffset DownloadedAtUtc,
    string Operator,
    string? ProfileName,
    int? ProfileVersion,
    string? ProgramName,
    int? ProgramVersion,
    double? StartDiameterMm,
    double? StockMicrometer,
    IReadOnlyList<GrindingJobStep> Steps,
    IReadOnlyList<(double ZMm, double DiameterMicrometer)> Points,
    IReadOnlyList<(string Item, string Status, string MessageKey)> Checks);
