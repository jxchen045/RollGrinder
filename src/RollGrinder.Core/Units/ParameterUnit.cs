namespace RollGrinder.Core.Units;

/// <summary>
/// 参数的工程单位。界面按此决定显示格式与输入校验，代码里不再解析单位字符串。
/// </summary>
public enum ParameterUnit
{
    /// <summary>无量纲（次数、比值、开关）。</summary>
    None = 0,

    /// <summary>毫米（长度，辊身坐标）。</summary>
    Millimeter = 1,

    /// <summary>微米（直径量偏差，界面单位）。</summary>
    Micrometer = 2,

    /// <summary>毫米每分钟（进给）。</summary>
    MillimeterPerMinute = 3,

    /// <summary>转每分钟（转速）。</summary>
    RevolutionsPerMinute = 4,

    /// <summary>次（走刀/光磨次数）。</summary>
    Count = 5,

    /// <summary>度（角度）。</summary>
    Degree = 6,

    /// <summary>米每秒（砂轮线速度）。</summary>
    MeterPerSecond = 7,

    /// <summary>微米每分钟（连续进给，直径量）。</summary>
    MicrometerPerMinute = 8,

    /// <summary>秒（折返停顿、变速周期）。</summary>
    Second = 9,

    /// <summary>百分比（变速幅度）。</summary>
    Percent = 10,

    /// <summary>安培（磨削电流、短行程电流）。</summary>
    Ampere = 11,
}
