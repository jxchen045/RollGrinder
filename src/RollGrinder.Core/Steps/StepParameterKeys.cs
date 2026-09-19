namespace RollGrinder.Core.Steps;

/// <summary>内置工序共用的参数键。</summary>
public static class StepParameterKeys
{
    /// <summary>本工序总去除量（直径量 µm）。</summary>
    public const string StockDiameterMicrometer = "stockDiameterMicrometer";

    /// <summary>每次走刀的切深（直径量 µm）。</summary>
    public const string InfeedPerPassDiameterMicrometer = "infeedPerPassDiameterMicrometer";

    /// <summary>轴向进给（mm/min）。</summary>
    public const string FeedMmPerMin = "feedMmPerMin";

    /// <summary>工件转速（r/min）。</summary>
    public const string WorkpieceSpeedRpm = "workpieceSpeedRpm";

    /// <summary>砂轮转速（r/min）。</summary>
    public const string WheelSpeedRpm = "wheelSpeedRpm";

    /// <summary>光磨次数。</summary>
    public const string SparkOutPassCount = "sparkOutPassCount";

    /// <summary>走刀次数。</summary>
    public const string PassCount = "passCount";
}
