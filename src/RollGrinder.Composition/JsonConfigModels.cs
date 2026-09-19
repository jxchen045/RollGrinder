using System.Collections.Generic;

namespace RollGrinder.Composition;

// 以下为 JSON 文件的原始形状，仅用于反序列化；对外一律转换为 Contracts 中的不可变 record。
// 保持可空与可变，是为了让缺字段能给出明确的报错，而不是悄悄取默认值。

internal sealed class MachineJson
{
    public int SchemaVersion { get; set; }

    public string? MachineId { get; set; }

    public string? DisplayName { get; set; }

    public ControllerJson? Controller { get; set; }

    public List<AxisJson>? Axes { get; set; }

    public List<MeasurementChannelJson>? MeasurementChannels { get; set; }

    public Dictionary<string, bool>? Options { get; set; }

    public Dictionary<string, double>? Thresholds { get; set; }

    public WorkpieceJson? Workpiece { get; set; }

    public Dictionary<string, int>? StepTypeCodes { get; set; }
}

internal sealed class ControllerJson
{
    public string? Kind { get; set; }

    public int ChannelNumber { get; set; }

    public string? EndpointUrl { get; set; }

    public bool? UseSecurity { get; set; }

    public bool? AutoAcceptUntrustedCertificates { get; set; }

    public int? SessionTimeoutMs { get; set; }

    public int? OperationTimeoutMs { get; set; }
}

internal sealed class AxisJson
{
    public string? Name { get; set; }

    public string? Role { get; set; }

    public bool IsPresent { get; set; }

    public string? ClosedLoop { get; set; }

    public double? MinPositionMm { get; set; }

    public double? MaxPositionMm { get; set; }

    public double? MaxFeedMmPerMin { get; set; }

    public double? MaxSpeedRpm { get; set; }
}

internal sealed class MeasurementChannelJson
{
    public string? Name { get; set; }

    public bool IsPresent { get; set; }

    public string? Quantity { get; set; }

    public double ResolutionMicrometer { get; set; }
}

internal sealed class WorkpieceJson
{
    public double MinBodyLengthMm { get; set; }

    public double MaxBodyLengthMm { get; set; }

    public double MinDiameterMm { get; set; }

    public double MaxDiameterMm { get; set; }

    public double MaxWeightKg { get; set; }
}

internal sealed class TagMapJson
{
    public int SchemaVersion { get; set; }

    public List<TagJson>? Tags { get; set; }
}

internal sealed class TagJson
{
    public string? Key { get; set; }

    public string? Address { get; set; }

    public string? DataType { get; set; }

    public string? Access { get; set; }

    public string? Unit { get; set; }

    public double? Scale { get; set; }

    public string? Description { get; set; }

    public int? ArrayLength { get; set; }

    public int? IndexOffset { get; set; }
}
