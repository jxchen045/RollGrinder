using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 长度对齐（设计说明《轧辊与磨削方案》4.5）：以辊身中心为基准截取或延伸，任何时候都不等比伸缩；
/// 只发生在端部非工作区，工作区内曲线始终是原曲线。
/// </summary>
public sealed class LengthAlignmentTests
{
    private static readonly RollProfileTypeRegistry Registry = RollProfileTypeRegistry.CreateDefault();

    /// <summary>名义 1800：圆柱 40 + 凸度 1720（300 µm）+ 圆柱 40——"不修形的端部写成圆柱段"。</summary>
    private static CompositeRollProfile CrownWithEnds(double end = 40.0, double nominal = 1800.0) => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Cylindrical, end, ParameterSet.Empty),
        new SequentialSegment(ProfileTypeKeys.Crown, nominal - (2.0 * end),
            new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(300.0))),
        new SequentialSegment(ProfileTypeKeys.Cylindrical, end, ParameterSet.Empty),
    });

    /// <summary>整根一段 CVC（曲线按轧机坐标定义，两端没有圆柱段）。</summary>
    private static CompositeRollProfile Cvc(double length) => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Cvc, length, new CvcProfileType().Schema.CreateDefaults()
            .With(CvcProfileType.A1DiameterMicrometerKey, ParameterValue.FromNumber(200.0))
            .With(CvcProfileType.A3DiameterMicrometerKey, ParameterValue.FromNumber(-150.0))),
    });

    /// <summary>以辊身中心为 0 的 Z 处的直径偏差（µm）。</summary>
    private static double AtCentered(CompositeRollProfile profile, double lengthMm, double centeredZMm) =>
        UnitConversion.RadiusMmToDiameterMicrometer(
            profile.Compose(RollGeometry.FromDiameter(lengthMm, 600.0), Registry, 2001).RadiusOffsetAtMm(centeredZMm + (lengthMm / 2.0)));

    [Fact]
    public void The_end_zone_defaults_to_the_end_cylinders_and_is_zero_without_them()
    {
        ProfileLengthAlignment.DefaultEndZones(CrownWithEnds(40.0)).Should().Be((40.0, 40.0));
        ProfileLengthAlignment.DefaultEndZones(Cvc(1800.0)).Should().Be((0.0, 0.0));
    }

    [Fact]
    public void The_same_length_is_used_as_is()
    {
        CompositeRollProfile profile = CrownWithEnds();

        LengthAlignment result = ProfileLengthAlignment.Align(profile, 1800.0, 1800.0, 36.0, null, Registry, 401);

        result.Kind.Should().Be(LengthAlignmentKind.Exact);
        result.Profile.Should().BeSameAs(profile);
    }

    [Fact]
    public void A_shorter_body_trims_both_end_cylinders_and_leaves_the_working_zone_untouched()
    {
        LengthAlignment result = ProfileLengthAlignment.Align(CrownWithEnds(), 1800.0, 1797.0, 36.0, null, Registry, 401);

        result.Kind.Should().Be(LengthAlignmentKind.Trimmed);
        result.DifferenceMm.Should().BeApproximately(-3.0, 1e-9);
        result.PerEndMm.Should().BeApproximately(-1.5, 1e-9, "两端各截 1.5 mm");
        CompositeRollProfile aligned = result.Profile!;
        aligned.Segments.Select(s => s.LengthMm).Should().Equal(38.5, 1720.0, 38.5);
        aligned.EndZMm.Should().BeApproximately(1797.0, 1e-9);

        // 不伸缩：以中心为 0，工作区里逐点与原曲线相同。
        foreach (double z in new[] { -850.0, -400.0, 0.0, 333.0, 850.0 })
        {
            AtCentered(aligned, 1797.0, z).Should().BeApproximately(AtCentered(CrownWithEnds(), 1800.0, z), 0.05);
        }
    }

    [Fact]
    public void A_slightly_longer_body_extends_the_end_cylinders_by_their_tangent()
    {
        LengthAlignment result = ProfileLengthAlignment.Align(CrownWithEnds(), 1800.0, 1804.0, 36.0, null, Registry, 401);

        result.Kind.Should().Be(LengthAlignmentKind.Extended);
        result.Profile!.Segments.Select(s => s.LengthMm).Should().Equal(42.0, 1720.0, 42.0);
        AtCentered(result.Profile, 1804.0, 0.0).Should().BeApproximately(AtCentered(CrownWithEnds(), 1800.0, 0.0), 0.05);
    }

    [Fact]
    public void Trimming_into_the_working_zone_or_beyond_the_tolerance_is_refused()
    {
        // 每端要截 25 mm，端部圆柱只有 20 mm：会截进工作区。
        ProfileLengthAlignment.Align(CrownWithEnds(20.0), 1800.0, 1750.0, 36.0, null, Registry, 401)
            .Should().Match<LengthAlignment>(r => r.Kind == LengthAlignmentKind.OutOfTolerance && r.Profile == null && !r.CanUse);

        // 非工作区够宽，但总差 50 超过下发容差 36。
        ProfileLengthAlignment.Align(CrownWithEnds(40.0), 1800.0, 1750.0, 36.0, null, Registry, 401)
            .Kind.Should().Be(LengthAlignmentKind.OutOfTolerance);
    }

    [Fact]
    public void A_cvc_without_end_cylinders_is_refused_unless_the_plan_gives_an_end_zone()
    {
        ProfileLengthAlignment.Align(Cvc(1800.0), 1800.0, 1797.0, 36.0, null, Registry, 401)
            .Kind.Should().Be(LengthAlignmentKind.OutOfTolerance, "默认非工作区为 0：不能截进 CVC 曲线");

        LengthAlignment result = ProfileLengthAlignment.Align(Cvc(1800.0), 1800.0, 1797.0, 36.0, 10.0, Registry, 1801);

        result.Kind.Should().Be(LengthAlignmentKind.Trimmed);
        CompositeRollProfile aligned = result.Profile!;
        aligned.Segments.Should().ContainSingle().Which.ProfileTypeKey.Should().Be(ProfileTypeKeys.PointTable);

        // 截取只缩短定义域：留下部分逐点与原曲线相同，端点就是原曲线在那一点的值（不产生台阶）。
        foreach (double z in new[] { -898.5, -500.0, 0.0, 450.0, 898.5 })
        {
            AtCentered(aligned, 1797.0, z).Should().BeApproximately(AtCentered(Cvc(1800.0), 1800.0, z), 0.2);
        }
    }

    [Fact]
    public void A_tangent_extension_keeps_height_and_slope_continuous()
    {
        CompositeRollProfile cvc = Cvc(1800.0);
        LengthAlignment result = ProfileLengthAlignment.Align(cvc, 1800.0, 1806.0, 36.0, 10.0, Registry, 1807);

        result.Kind.Should().Be(LengthAlignmentKind.Extended);
        double slope = (AtCentered(cvc, 1800.0, 900.0) - AtCentered(cvc, 1800.0, 899.0)) / 1.0;
        double edge = AtCentered(cvc, 1800.0, 900.0);

        // 名义端点以外 3 mm：沿端点切线外推。
        AtCentered(result.Profile!, 1806.0, 903.0).Should().BeApproximately(edge + (3.0 * slope), 0.3);
        AtCentered(result.Profile!, 1806.0, 900.0).Should().BeApproximately(edge, 0.2);
    }

    [Fact]
    public void Lengths_are_never_scaled()
    {
        // 名义 1800 的凸度到 1797：凸度段还是 1720 长、300 µm——等比伸缩会把它变成 1717.1。
        LengthAlignment result = ProfileLengthAlignment.Align(CrownWithEnds(), 1800.0, 1797.0, 36.0, null, Registry, 401);

        RollProfileSegment crown = result.Profile!.Segments[1];
        crown.LengthMm.Should().Be(1720.0);
        crown.Parameters.Get(CrownProfileType.CrownDiameterMicrometerKey).Number.Should().Be(300.0);
        Math.Abs(result.PerEndMm).Should().BeLessThan(result.HeadZoneMm);
    }
}
