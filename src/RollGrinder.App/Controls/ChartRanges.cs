using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.App.Controls;

/// <summary>
/// 曲线图的坐标范围（分辨率适配方案第 4 节，不依赖 WPF，单测直接链接）。
///
/// 辊形图：Y 范围只由<b>目标辊形</b>定（含 0 基线，外扩 25%，最小跨度 100 µm，按 1-2-5 取整），
/// 同一条辊形在哪一页都是同一个范围；配上固定 4 : 1 的绘图区，形状处处一样。
/// 偏差图：以 0 为中心对称，半幅 = max(2 × 公差, 数据) 取整，切换曲线时框不跳。
/// </summary>
public static class ChartRanges
{
    /// <summary>绘图区宽 : 高（不含坐标轴）。</summary>
    public const double PlotAspect = 4.0;

    /// <summary>偏差图绘图区最宽的宽高比（铺满宽度时不超过它）。</summary>
    public const double DeviationMaxAspect = 8.0;

    /// <summary>辊形图 Y 的最小跨度（直径量 µm）：平辊也不至于把 1 µm 的起伏放成一座山。</summary>
    public const double MinProfileSpanMicrometer = 100.0;

    public static (double Low, double High) Profile(IEnumerable<double> targetMicrometer)
    {
        ArgumentNullException.ThrowIfNull(targetMicrometer);
        double[] values = targetMicrometer.Where(double.IsFinite).ToArray();
        double lo = values.Length == 0 ? 0.0 : Math.Min(0.0, values.Min());
        double hi = values.Length == 0 ? 0.0 : Math.Max(0.0, values.Max());
        double span = hi - lo;
        double pad = Math.Max(span * 0.25, (MinProfileSpanMicrometer - span) / 2.0);
        return Round(lo - pad, hi + pad);
    }

    public static (double Low, double High) Deviation(double toleranceMicrometer, IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        double peak = values.Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty(0.0).Max();
        double half = Math.Max(Math.Max(2.0 * toleranceMicrometer, peak * 1.1), 10.0);
        double step = NiceStep(half / 2.0);
        half = Math.Ceiling(half / step) * step;
        return (-half, half);
    }

    /// <summary>其他沿辊身的量（圆度、电流……）：按数据取整，最小跨度 1。</summary>
    public static (double Low, double High) Data(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        double[] v = values.Where(double.IsFinite).ToArray();
        double lo = v.Length == 0 ? 0.0 : v.Min();
        double hi = v.Length == 0 ? 1.0 : v.Max();
        double pad = Math.Max((hi - lo) * 0.1, (1.0 - (hi - lo)) / 2.0);
        return Round(lo - pad, hi + pad);
    }

    /// <summary>1、2、5 × 10ⁿ 里不小于 raw 的最小一个。</summary>
    public static double NiceStep(double raw)
    {
        if (!(raw > 0.0) || !double.IsFinite(raw))
        {
            return 1.0;
        }

        double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(raw)));
        double unit = raw / magnitude;
        double nice = unit <= 1.0 ? 1.0 : unit <= 2.0 ? 2.0 : unit <= 5.0 ? 5.0 : 10.0;
        return nice * magnitude;
    }

    private static (double Low, double High) Round(double lo, double hi)
    {
        double step = NiceStep((hi - lo) / 5.0);
        return (Math.Floor(lo / step) * step, Math.Ceiling(hi / step) * step);
    }
}
