namespace RollGrinder.Core.Steps;

/// <summary>
/// 内置工序类型的键。对应 docs/design/B-Steps-工序编程.html 的工序序列。
/// 新增一类工序只写一个实现类并注册，这里加一个键——界面、NC 生成器与数据库都不动。
/// </summary>
public static class StepTypeKeys
{
    /// <summary>开始：程序起点标记，不产生运动。</summary>
    public const string Start = "Start";

    /// <summary>短行程：小范围往复，把辊面走顺、找正接触。</summary>
    public const string ShortStroke = "ShortStroke";

    /// <summary>粗磨：去除大部分余量。</summary>
    public const string Rough = "Rough";

    /// <summary>砂轮修整。</summary>
    public const string WheelDress = "WheelDress";

    /// <summary>半精磨。</summary>
    public const string SemiFinish = "SemiFinish";

    /// <summary>精磨。</summary>
    public const string Finish = "Finish";

    /// <summary>无火花光磨：只走行程不进给，消除弹性变形。</summary>
    public const string SparkOut = "SparkOut";

    /// <summary>辊形测量。</summary>
    public const string Measure = "Measure";

    /// <summary>抛光：极轻载，只降粗糙度。</summary>
    public const string Polish = "Polish";

    /// <summary>倒角。</summary>
    public const string Chamfer = "Chamfer";

    /// <summary>涡流探伤。</summary>
    public const string EddyCurrent = "EddyCurrent";

    /// <summary>结束：程序终点标记，不产生运动。</summary>
    public const string End = "End";
}
