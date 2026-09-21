using System;
using RollGrinder.Core.Units;

namespace RollGrinder.Core.Centring;

/// <summary>辊的哪一端。</summary>
public enum RollEnd
{
    /// <summary>头架侧。</summary>
    Head = 0,

    /// <summary>尾架侧。</summary>
    Tail = 1,
}

/// <summary>
/// 在辊的一端记下的一组读数。
///
/// 对中量的是**装夹**，不是辊形：两端各记一次，比的是两次之间的差。
/// 所以一条读数要把当时的位置一起记下来——位置不同，两组数没有可比性。
/// </summary>
/// <param name="End">在哪一端记的。</param>
/// <param name="ProbeARadiusMm">A 测头读数（半径量 mm）。</param>
/// <param name="ProbeBRadiusMm">B 测头读数（半径量 mm）。</param>
/// <param name="DiameterMm">测径仪读到的直径（mm）。</param>
/// <param name="CarriagePositionMm">记这一组时拖板在哪（mm）。</param>
/// <param name="InfeedPositionMm">记这一组时进给轴在哪（mm）。</param>
/// <param name="CapturedAtUtc">记录时刻。</param>
public sealed record CentringReading(
    RollEnd End,
    double ProbeARadiusMm,
    double ProbeBRadiusMm,
    double DiameterMm,
    double CarriagePositionMm,
    double InfeedPositionMm,
    DateTimeOffset CapturedAtUtc)
{
    /// <summary>
    /// 这一端的安装偏差（半径量 mm）= (A − B) / 2。
    ///
    /// 两个测头夹着辊面：辊心偏了，一个测头多进、另一个就少进同样多，
    /// 两读数之差的一半就是辊心相对测量架的偏移。
    /// </summary>
    public double MountingDeviationRadiusMm => (ProbeARadiusMm - ProbeBRadiusMm) / 2.0;
}

/// <summary>对中不合格时往哪边调。</summary>
public enum CentringAdjustment
{
    /// <summary>不用调。</summary>
    None = 0,

    /// <summary>头架侧偏高，把头架侧往里调。</summary>
    HeadInward = 1,

    /// <summary>尾架侧偏高，把尾架侧往里调。</summary>
    TailInward = 2,
}

/// <summary>
/// 两端读数的比较结果。
/// </summary>
/// <param name="Head">头架侧那一组。</param>
/// <param name="Tail">尾架侧那一组。</param>
/// <param name="DeviationDifferenceMicrometer">
/// 两端安装偏差之差（直径量 µm，头 − 尾）。对中就是把它调到零。
/// </param>
/// <param name="DiameterDifferenceMicrometer">两端直径之差（µm，头 − 尾）：辊本身的锥度，调中心架调不掉它。</param>
/// <param name="ToleranceMicrometer">判定用的对中公差（直径量 µm）。</param>
public sealed record CentringComparison(
    CentringReading Head,
    CentringReading Tail,
    double DeviationDifferenceMicrometer,
    double DiameterDifferenceMicrometer,
    double ToleranceMicrometer)
{
    /// <summary>在公差之内。</summary>
    public bool IsWithinTolerance => Math.Abs(DeviationDifferenceMicrometer) <= ToleranceMicrometer;

    /// <summary>往哪边调。</summary>
    public CentringAdjustment Adjustment => IsWithinTolerance
        ? CentringAdjustment.None
        : DeviationDifferenceMicrometer > 0.0
            ? CentringAdjustment.HeadInward
            : CentringAdjustment.TailInward;

    /// <summary>
    /// 比较两端。
    ///
    /// 两个差分开报：安装偏差之差是调得掉的（中心架、尾座），
    /// 直径之差是辊本身的锥度，调中心架调不掉——混成一个数会让人白调半天。
    /// </summary>
    public static CentringComparison Compare(
        CentringReading head, CentringReading tail, double toleranceMicrometer)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(tail);

        if (head.End != RollEnd.Head || tail.End != RollEnd.Tail)
        {
            throw new DomainException("Centring compares one head-side reading against one tail-side reading.");
        }

        if (toleranceMicrometer <= 0.0)
        {
            throw new DomainException("Centring tolerance must be positive.");
        }

        return new CentringComparison(
            head,
            tail,
            UnitConversion.RadiusMmToDiameterMicrometer(
                head.MountingDeviationRadiusMm - tail.MountingDeviationRadiusMm),
            UnitConversion.MmToMicrometer(head.DiameterMm - tail.DiameterMm),
            toleranceMicrometer);
    }
}
