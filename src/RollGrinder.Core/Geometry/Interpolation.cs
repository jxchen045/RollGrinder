using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Core.Geometry;

/// <summary>点表的插值方式（问题 Q8 的决定：四种都做，全部自己实现）。</summary>
public enum InterpolationMethod
{
    /// <summary>折线：点与点之间连直线。</summary>
    Linear = 0,

    /// <summary>自然三次样条：过每个点、二阶导连续，两端二阶导为 0。点稀时会在点之间鼓出来（过冲）。</summary>
    CubicSpline = 1,

    /// <summary>保形分段三次（Fritsch–Carlson）：过每个点、一阶导连续，不在点之间鼓出来。</summary>
    ShapePreserving = 2,

    /// <summary>
    /// 平滑样条（Reinsch）：不强求过每个点，在"贴近点"与"曲线光顺"之间取舍，给带噪声的测量数据用。
    /// 平滑度 0 就是自然三次样条，越大越平，到 1 趋于一条最小二乘直线。
    /// </summary>
    SmoothingSpline = 3,
}

/// <summary>一维插值。横坐标必须严格递增；超出范围取端点的值。</summary>
public static class Interpolation
{
    /// <summary>平滑度上限。到 1 时方程组会退化，取一个很接近 1 的值代替。</summary>
    public const double MaxSmoothing = 0.999;

    /// <summary>按方法建一个插值函数。</summary>
    /// <param name="method">插值方式。</param>
    /// <param name="xs">横坐标，严格递增，至少两个点。</param>
    /// <param name="ys">纵坐标。</param>
    /// <param name="smoothing">平滑度 0–1，只对平滑样条有意义。</param>
    public static Func<double, double> Build(
        InterpolationMethod method, IReadOnlyList<double> xs, IReadOnlyList<double> ys, double smoothing = 0.0)
    {
        ArgumentNullException.ThrowIfNull(xs);
        ArgumentNullException.ThrowIfNull(ys);
        if (xs.Count != ys.Count || xs.Count < 2)
        {
            throw new DomainException("Interpolation needs at least two points with matching coordinates.");
        }

        for (int i = 1; i < xs.Count; i++)
        {
            if (!(xs[i] > xs[i - 1]))
            {
                throw new DomainException("Interpolation abscissae must be strictly increasing.");
            }
        }

        double[] x = xs.ToArray();
        double[] y = ys.ToArray();
        if (x.Length == 2)
        {
            return Clamped(x, y, Linear(x, y));
        }

        return method switch
        {
            InterpolationMethod.Linear => Clamped(x, y, Linear(x, y)),
            InterpolationMethod.CubicSpline => Clamped(x, y, Spline(x, y, 0.0)),
            InterpolationMethod.ShapePreserving => Clamped(x, y, ShapePreserving(x, y)),
            InterpolationMethod.SmoothingSpline => Clamped(x, y, Spline(x, y, SmoothingWeight(x, smoothing))),
            _ => throw new DomainException($"Unsupported interpolation method {method}."),
        };
    }

    /// <summary>
    /// 平滑度 s（0–1）换成 Reinsch 目标函数里的权重 α：α = s / (1 − s) · h̄³ / 6。
    /// s = 0.5 时 α = h̄³/6，与常见 csaps 的默认平滑程度相当；乘上 h̄³ 让同一个 s 不随点距变。
    /// </summary>
    public static double SmoothingWeight(IReadOnlyList<double> xs, double smoothing)
    {
        ArgumentNullException.ThrowIfNull(xs);
        double s = Math.Clamp(smoothing, 0.0, MaxSmoothing);
        double meanSpacing = (xs[^1] - xs[0]) / (xs.Count - 1);
        return s / (1.0 - s) * Math.Pow(meanSpacing, 3) / 6.0;
    }

    private static Func<double, double> Clamped(double[] x, double[] y, Func<double, int, double> inside) =>
        value =>
        {
            if (value <= x[0])
            {
                return inside(x[0], 0);
            }

            if (value >= x[^1])
            {
                return inside(x[^1], x.Length - 2);
            }

            int index = Array.BinarySearch(x, value);
            int segment = index >= 0 ? Math.Min(index, x.Length - 2) : ~index - 1;
            return inside(value, segment);
        };

    private static Func<double, int, double> Linear(double[] x, double[] y) =>
        (value, i) =>
        {
            double t = (value - x[i]) / (x[i + 1] - x[i]);
            return y[i] + (t * (y[i + 1] - y[i]));
        };

    /// <summary>
    /// Reinsch 平滑样条：min Σ(yᵢ − fᵢ)² + α ∫ f″²。
    /// 内点二阶导 γ 满足 (R + α QᵀQ) γ = Qᵀy，节点值 f = y − α Q γ；α = 0 就是过每个点的自然三次样条。
    /// R 三对角、QᵀQ 五对角，按带宽 2 的带状矩阵解，点数上千也很快。
    /// </summary>
    private static Func<double, int, double> Spline(double[] x, double[] y, double alpha)
    {
        int n = x.Length;
        int m = n - 2;
        double[] h = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            h[i] = x[i + 1] - x[i];
        }

        // Q 的第 r 列（对应内点 i = r + 1）只有三个非零：第 i−1、i、i+1 行。
        double[,] q = new double[m, 3];
        for (int r = 0; r < m; r++)
        {
            int i = r + 1;
            q[r, 0] = 1.0 / h[i - 1];
            q[r, 1] = (-1.0 / h[i - 1]) - (1.0 / h[i]);
            q[r, 2] = 1.0 / h[i];
        }

        double[,] band = new double[m, 5];
        double[] rhs = new double[m];
        for (int r = 0; r < m; r++)
        {
            int i = r + 1;
            band[r, 2] = (h[i - 1] + h[i]) / 3.0;
            if (r + 1 < m)
            {
                band[r, 3] = h[i] / 6.0;
                band[r + 1, 1] = h[i] / 6.0;
            }

            rhs[r] = ((y[i + 1] - y[i]) / h[i]) - ((y[i] - y[i - 1]) / h[i - 1]);

            // (QᵀQ)[r, s]：两列在重叠的行上逐项相乘。第 r 列占行 r..r+2，第 s 列占行 s..s+2。
            for (int s = Math.Max(0, r - 2); s <= Math.Min(m - 1, r + 2); s++)
            {
                double sum = 0.0;
                for (int row = Math.Max(r, s); row <= Math.Min(r, s) + 2; row++)
                {
                    sum += q[r, row - r] * q[s, row - s];
                }

                band[r, s - r + 2] += alpha * sum;
            }
        }

        double[] gammaInner = SolveBanded(band, rhs);
        double[] gamma = new double[n];
        Array.Copy(gammaInner, 0, gamma, 1, m);

        double[] f = (double[])y.Clone();
        if (alpha > 0.0)
        {
            for (int r = 0; r < m; r++)
            {
                f[r] -= alpha * q[r, 0] * gammaInner[r];
                f[r + 1] -= alpha * q[r, 1] * gammaInner[r];
                f[r + 2] -= alpha * q[r, 2] * gammaInner[r];
            }
        }

        return (value, i) =>
        {
            double width = h[i];
            double a = x[i + 1] - value;
            double b = value - x[i];
            return (f[i] * a / width) + (f[i + 1] * b / width)
                + (gamma[i] / 6.0 * ((a * a * a / width) - (width * a)))
                + (gamma[i + 1] / 6.0 * ((b * b * b / width) - (width * b)));
        };
    }

    /// <summary>带宽 2 的对称正定带状方程组，高斯消元不选主元。band[i, j − i + 2] 存 A[i, j]。</summary>
    private static double[] SolveBanded(double[,] band, double[] rhs)
    {
        int m = rhs.Length;
        double[,] a = (double[,])band.Clone();
        double[] b = (double[])rhs.Clone();

        for (int k = 0; k < m; k++)
        {
            double pivot = a[k, 2];
            for (int i = k + 1; i <= Math.Min(k + 2, m - 1); i++)
            {
                double factor = a[i, k - i + 2] / pivot;
                if (factor == 0.0)
                {
                    continue;
                }

                for (int j = k; j <= Math.Min(k + 2, m - 1); j++)
                {
                    a[i, j - i + 2] -= factor * a[k, j - k + 2];
                }

                b[i] -= factor * b[k];
            }
        }

        double[] solution = new double[m];
        for (int k = m - 1; k >= 0; k--)
        {
            double sum = b[k];
            for (int j = k + 1; j <= Math.Min(k + 2, m - 1); j++)
            {
                sum -= a[k, j - k + 2] * solution[j];
            }

            solution[k] = sum / a[k, 2];
        }

        return solution;
    }

    /// <summary>Fritsch–Carlson 保形分段三次 Hermite：单调的数据插出来仍单调，极值只落在数据点上。</summary>
    private static Func<double, int, double> ShapePreserving(double[] x, double[] y)
    {
        int n = x.Length;
        double[] h = new double[n - 1];
        double[] delta = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            h[i] = x[i + 1] - x[i];
            delta[i] = (y[i + 1] - y[i]) / h[i];
        }

        double[] slope = new double[n];
        for (int k = 1; k < n - 1; k++)
        {
            if (delta[k - 1] * delta[k] <= 0.0)
            {
                slope[k] = 0.0;
                continue;
            }

            double w1 = (2.0 * h[k]) + h[k - 1];
            double w2 = h[k] + (2.0 * h[k - 1]);
            slope[k] = (w1 + w2) / ((w1 / delta[k - 1]) + (w2 / delta[k]));
        }

        slope[0] = EndSlope(h[0], h[1], delta[0], delta[1]);
        slope[n - 1] = EndSlope(h[n - 2], h[n - 3], delta[n - 2], delta[n - 3]);

        return (value, i) =>
        {
            double width = h[i];
            double t = (value - x[i]) / width;
            double t2 = t * t;
            double t3 = t2 * t;
            return (((2.0 * t3) - (3.0 * t2) + 1.0) * y[i])
                + ((t3 - (2.0 * t2) + t) * width * slope[i])
                + (((-2.0 * t3) + (3.0 * t2)) * y[i + 1])
                + ((t3 - t2) * width * slope[i + 1]);
        };
    }

    /// <summary>端点斜率：三点公式，再按保形条件修正（与 MATLAB pchip 同法）。</summary>
    private static double EndSlope(double h0, double h1, double d0, double d1)
    {
        double slope = (((2.0 * h0) + h1) * d0 - (h0 * d1)) / (h0 + h1);
        if (Math.Sign(slope) != Math.Sign(d0))
        {
            return 0.0;
        }

        if (Math.Sign(d0) != Math.Sign(d1) && Math.Abs(slope) > Math.Abs(3.0 * d0))
        {
            return 3.0 * d0;
        }

        return slope;
    }
}
