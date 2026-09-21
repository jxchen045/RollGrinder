using System;
using System.Collections.Generic;
using System.Linq;

namespace RollGrinder.Sim;

/// <summary>
/// 仿真辊面：每个辊身位置、每个圆周角度上的实际半径。
///
/// 这是仿真里唯一"有物理"的地方，也是让无机床开发说得过去的关键——
/// 只有辊面真的带着误差、磨削真的把它磨掉、机床真的留下一份可重复的系统性偏差，
/// 磨前磨后曲线、误差曲线与补偿收敛才有东西可验。
///
/// 半径写成几项之和：
///
/// <code>
/// r(z, θ) = 名义半径
///         + 指令辊形(z)        ← 下发下来的目标辊形（已含补偿）
///         + 系统性偏差(z)      ← 机床自己的、可重复的那一份，补偿要对付的就是它
///         + 来料误差(z)        ← 这支辊进来时就有的，磨掉就没了
///         + 余量               ← 还没磨掉的量
///         + 圆度(θ, z)         ← 多瓣形
///         + 偏心(θ, z)         ← 装夹偏心，每转一个周期
/// </code>
///
/// 全部由种子决定，**没有一处取当前时间或全局随机数**：同一个种子跑出来的辊子
/// 每次都一样，测试才敢断言具体数值。
/// </summary>
public sealed class RollSurfaceModel
{
    /// <summary>进来时的余量（半径量 mm）。</summary>
    public const double InitialStockRadiusMm = 0.30;

    /// <summary>圆度的瓣数。三瓣形是卡盘/中心架最常留下的那一种。</summary>
    public const int RoundnessLobes = 3;

    private readonly double bodyLengthMm;
    private readonly double nominalRadiusMm;
    private readonly int seed;

    /// <summary>下发下来的辊形点列（辊身坐标 mm → 半径偏差 mm），按坐标升序。</summary>
    private readonly List<(double BodyPositionMm, double RadiusOffsetMm)> commandedProfile = new();

    private double stockRadiusMm = InitialStockRadiusMm;

    /// <summary>来料误差还剩多少比例；磨削把它磨掉，到 0 就是磨干净了。</summary>
    private double asReceivedFraction = 1.0;

    public RollSurfaceModel(double bodyLengthMm, double nominalRadiusMm, int seed = 20260921)
    {
        if (bodyLengthMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(bodyLengthMm));
        }

        this.bodyLengthMm = bodyLengthMm;
        this.nominalRadiusMm = nominalRadiusMm;
        this.seed = seed;
    }

    /// <summary>还剩多少余量（半径量 mm）。</summary>
    public double RemainingStockRadiusMm => this.stockRadiusMm;

    /// <summary>来料误差还剩多少（0–1）。</summary>
    public double AsReceivedFraction => this.asReceivedFraction;

    /// <summary>
    /// 机床自己的系统性偏差（半径量 mm）：可重复、与磨了几遍无关。
    ///
    /// 两项：X 轴随行程下垂（靠尾架端越明显），加一点点二次项（热变形）。
    /// **补偿算法要对付的就是它**——磨完一遍测出来的误差基本就是这条曲线，
    /// 补偿把它反过来叠进指令辊形，下一遍误差就该小一圈。
    /// </summary>
    public double SystematicErrorAtMm(double bodyPositionMm)
    {
        double t = Clamp01(bodyPositionMm / this.bodyLengthMm);

        // 线性下垂 8 µm（直径量 16 µm）+ 中凸 3 µm 的热变形。
        return (-0.008 * t) + (0.003 * 4.0 * t * (1.0 - t));
    }

    /// <summary>这支辊进来时自带的误差（半径量 mm）。磨削会把它磨掉。</summary>
    public double AsReceivedErrorAtMm(double bodyPositionMm)
    {
        double t = Clamp01(bodyPositionMm / this.bodyLengthMm);

        // 一条不对称的波形，外加按位置抖动的小噪声——来料不会是一条漂亮的曲线。
        return (0.015 * Math.Sin(Math.PI * t))
               + (0.006 * Math.Sin(3.4 * Math.PI * t))
               + (0.002 * ValueNoise(t * 37.0));
    }

    /// <summary>指令辊形在某个位置的半径偏差（mm）。还没下发过时为 0。</summary>
    public double CommandedOffsetAtMm(double bodyPositionMm)
    {
        if (this.commandedProfile.Count == 0)
        {
            return 0.0;
        }

        if (this.commandedProfile.Count == 1 || bodyPositionMm <= this.commandedProfile[0].BodyPositionMm)
        {
            return this.commandedProfile[0].RadiusOffsetMm;
        }

        if (bodyPositionMm >= this.commandedProfile[^1].BodyPositionMm)
        {
            return this.commandedProfile[^1].RadiusOffsetMm;
        }

        for (int i = 1; i < this.commandedProfile.Count; i++)
        {
            (double toZ, double toOffset) = this.commandedProfile[i];
            if (bodyPositionMm > toZ)
            {
                continue;
            }

            (double fromZ, double fromOffset) = this.commandedProfile[i - 1];
            double span = toZ - fromZ;
            double ratio = span <= 0.0 ? 0.0 : (bodyPositionMm - fromZ) / span;
            return fromOffset + ((toOffset - fromOffset) * ratio);
        }

        return this.commandedProfile[^1].RadiusOffsetMm;
    }

    /// <summary>
    /// 一圈平均下来的半径（mm）——测径仪读到的就是这个，圆度与偏心在一转里平均掉了。
    /// </summary>
    public double MeanRadiusAtMm(double bodyPositionMm) =>
        this.nominalRadiusMm
        + CommandedOffsetAtMm(bodyPositionMm)
        + SystematicErrorAtMm(bodyPositionMm)
        + (this.asReceivedFraction * AsReceivedErrorAtMm(bodyPositionMm))
        + this.stockRadiusMm;

    /// <summary>
    /// 某个圆周角上的半径（mm）。圆度与偏心分量在这里，测圆度要绕一圈取多点。
    /// </summary>
    /// <param name="bodyPositionMm">辊身坐标。</param>
    /// <param name="angleRadians">圆周角，0 对齐头架圆周分度脉冲（原理图 I37.1）。</param>
    public double RadiusAtMm(double bodyPositionMm, double angleRadians) =>
        MeanRadiusAtMm(bodyPositionMm)
        + RoundnessAtMm(bodyPositionMm, angleRadians)
        + EccentricityAtMm(bodyPositionMm, angleRadians);

    /// <summary>圆度分量（半径量 mm）：三瓣形，幅值随磨削变小。</summary>
    public double RoundnessAtMm(double bodyPositionMm, double angleRadians)
    {
        double t = Clamp01(bodyPositionMm / this.bodyLengthMm);
        double amplitude = 0.004 * ((0.25 + (0.75 * this.asReceivedFraction)) + (0.2 * ValueNoise(t * 11.0)));
        return amplitude * Math.Cos(RoundnessLobes * angleRadians);
    }

    /// <summary>
    /// 偏心分量（半径量 mm）：每转一个周期，幅值沿辊身缓变。
    /// 装夹偏心磨不掉——它跟着回转中心走，不是辊面形状。
    /// </summary>
    public double EccentricityAtMm(double bodyPositionMm, double angleRadians)
    {
        double t = Clamp01(bodyPositionMm / this.bodyLengthMm);
        double amplitude = 0.006 * (0.6 + (0.4 * t));
        return amplitude * Math.Cos(angleRadians);
    }

    /// <summary>记下一个下发进来的辊形点。</summary>
    public void SetCommandedPoint(int index, double bodyPositionMm, double radiusOffsetMm)
    {
        this.commandedProfile.RemoveAll(point =>
            Math.Abs(point.BodyPositionMm - bodyPositionMm) < 1e-9);
        this.commandedProfile.Add((bodyPositionMm, radiusOffsetMm));
        this.commandedProfile.Sort((left, right) => left.BodyPositionMm.CompareTo(right.BodyPositionMm));
    }

    /// <summary>换一支辊：余量与来料误差回到进来时的样子，指令辊形留着。</summary>
    public void ResetStock()
    {
        this.stockRadiusMm = InitialStockRadiusMm;
        this.asReceivedFraction = 1.0;
    }

    /// <summary>
    /// 磨掉一点。余量与来料误差一起减——磨到余量见底，辊面就落在
    /// 「指令辊形 + 系统性偏差」上，这正是补偿要看到的那条误差曲线。
    /// </summary>
    /// <param name="removalRadiusMm">这一步磨掉多少（半径量 mm）。</param>
    public void Remove(double removalRadiusMm)
    {
        if (removalRadiusMm <= 0.0)
        {
            return;
        }

        double before = this.stockRadiusMm;
        this.stockRadiusMm = Math.Max(0.0, this.stockRadiusMm - removalRadiusMm);

        // 来料误差按余量磨掉的比例一起消：余量磨完，来料误差也就磨没了。
        if (before > 0.0)
        {
            double consumed = (before - this.stockRadiusMm) / InitialStockRadiusMm;
            this.asReceivedFraction = Math.Max(0.0, this.asReceivedFraction - consumed);
        }
    }

    /// <summary>
    /// 磨削电流（A）：空载打底，加上与去除率成正比的一项，再叠一点随位置变化的波动。
    /// 余量越大切得越狠，电流越高——现场就是按这个判断要不要往下压进给的。
    /// </summary>
    public double GrindingCurrentA(double removalRateRadiusMmPerMin, double bodyPositionMm, bool isCutting)
    {
        if (!isCutting)
        {
            return 6.0;
        }

        double load = 260.0 * Math.Max(0.0, removalRateRadiusMmPerMin);
        double stockTerm = 18.0 * (this.stockRadiusMm / InitialStockRadiusMm);
        double ripple = 1.5 * ValueNoise(bodyPositionMm * 0.05);

        return 6.0 + load + stockTerm + ripple;
    }

    /// <summary>沿辊身等距取若干点的平均半径，测量扫查用。</summary>
    public IReadOnlyList<(double BodyPositionMm, double RadiusMm)> SampleMeanProfile(int pointCount)
    {
        if (pointCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(pointCount));
        }

        return Enumerable.Range(0, pointCount)
            .Select(i =>
            {
                double z = this.bodyLengthMm * i / (pointCount - 1);
                return (z, MeanRadiusAtMm(z));
            })
            .ToArray();
    }

    /// <summary>
    /// 由种子与位置决定的伪噪声，取值约在 −1…1。
    ///
    /// 用位置算而不是拉一个随机数序列：读取顺序不确定（界面按需要读哪个点读哪个点），
    /// 用序列的话同一个点两次读数会不一样，测试就没法断言了。
    /// </summary>
    private double ValueNoise(double x)
    {
        unchecked
        {
            int h = this.seed;
            h = (h * 397) ^ (int)(x * 1024.0);
            h ^= h >> 13;
            h *= 1274126177;
            h ^= h >> 16;
            return ((h & 0xFFFF) / 32767.5) - 1.0;
        }
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}
