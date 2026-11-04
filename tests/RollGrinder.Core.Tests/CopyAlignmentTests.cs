using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 复制方案的长度对齐（设计说明 4.5，第 2 期验收）：长度相同直接复制；凸度类套用须确认；
/// CVC / 点表更短截取、稍长延伸、超容差被拒。
/// </summary>
public sealed class CopyAlignmentTests
{
    private static readonly RollProfileTypeRegistry Registry = RollProfileTypeRegistry.CreateDefault();

    private static CompositeRollProfile Crown(double nominal = 1800.0) => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
        new SequentialSegment(ProfileTypeKeys.Crown, nominal - 80.0,
            new CrownProfileType().Schema.CreateDefaults().With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(300.0))),
        new SequentialSegment(ProfileTypeKeys.Cylindrical, 40.0, ParameterSet.Empty),
    });

    private static CompositeRollProfile Cvc(double length = 1800.0) => CompositeRollProfile.Sequential(0.0, new[]
    {
        new SequentialSegment(ProfileTypeKeys.Cvc, length, new CvcProfileType().Schema.CreateDefaults()
            .With(CvcProfileType.A1DiameterMicrometerKey, ParameterValue.FromNumber(200.0))
            .With(CvcProfileType.A3DiameterMicrometerKey, ParameterValue.FromNumber(-150.0))),
    });

    private static double AtCentered(CompositeRollProfile profile, double length, double z) =>
        UnitConversion.RadiusMmToDiameterMicrometer(
            profile.Compose(RollGeometry.FromDiameter(length, 600.0), Registry, 2001).RadiusOffsetAtMm(z + (length / 2.0)));

    [Fact]
    public void The_same_nominal_length_copies_as_is_without_asking()
    {
        CompositeRollProfile source = Crown();

        CopyAlignment result = ProfileCopyAlignment.Align(source, 1800.0, 1800.0, 1.0, 36.0, Registry, 401);

        result.Kind.Should().Be(CopyAlignmentKind.Same);
        result.NeedsConfirm.Should().BeFalse();
        result.Profile.Should().BeSameAs(source);
    }

    [Fact]
    public void A_difference_within_the_copy_threshold_counts_as_the_same()
    {
        CopyAlignment result = ProfileCopyAlignment.Align(Crown(), 1800.0, 1799.4, 1.0, 36.0, Registry, 401);

        result.Kind.Should().Be(CopyAlignmentKind.Same);
        result.NeedsConfirm.Should().BeFalse();
        result.Profile!.EndZMm.Should().BeApproximately(1799.4, 1e-9);
    }

    [Fact]
    public void A_crown_keeps_its_end_segments_and_fills_the_middle_but_needs_confirming()
    {
        CopyAlignment result = ProfileCopyAlignment.Align(Crown(), 1800.0, 1790.0, 1.0, 36.0, Registry, 401);

        result.Kind.Should().Be(CopyAlignmentKind.MiddleFilled);
        result.NeedsConfirm.Should().BeTrue("凸度类套用须确认");
        result.SegmentsBefore.Should().Equal(40.0, 1720.0, 40.0);
        result.SegmentsAfter.Should().Equal(40.0, 1710.0, 40.0);
        result.Profile!.Segments[1].Parameters.Get(CrownProfileType.CrownDiameterMicrometerKey).Number.Should().Be(300.0, "形状参数不变");
        AtCentered(result.Profile, 1790.0, 0.0).Should().BeApproximately(AtCentered(Crown(), 1800.0, 0.0), 0.05);
    }

    [Fact]
    public void A_shorter_cvc_is_trimmed_at_the_centre()
    {
        CopyAlignment result = ProfileCopyAlignment.Align(Cvc(), 1800.0, 1790.0, 1.0, 36.0, Registry, 1801);

        result.Kind.Should().Be(CopyAlignmentKind.Trimmed);
        result.PerEndMm.Should().BeApproximately(-5.0, 1e-9);
        result.NeedsConfirm.Should().BeTrue("整根 CVC 没有端部圆柱段：截进了曲线，看过叠图再复制");
        foreach (double z in new[] { -890.0, -300.0, 0.0, 450.0, 890.0 })
        {
            AtCentered(result.Profile!, 1790.0, z).Should().BeApproximately(AtCentered(Cvc(), 1800.0, z), 0.3);
        }
    }

    [Fact]
    public void A_slightly_longer_cvc_is_extended_by_the_tangent()
    {
        CopyAlignment result = ProfileCopyAlignment.Align(Cvc(), 1800.0, 1806.0, 1.0, 36.0, Registry, 1807);

        result.Kind.Should().Be(CopyAlignmentKind.Extended);
        result.CanCopy.Should().BeTrue();
        result.PerEndMm.Should().BeApproximately(3.0, 1e-9);
    }

    [Fact]
    public void A_cvc_longer_than_the_tolerance_is_refused()
    {
        CopyAlignment result = ProfileCopyAlignment.Align(Cvc(), 1800.0, 1900.0, 1.0, 36.0, Registry, 401);

        result.Kind.Should().Be(CopyAlignmentKind.Refused);
        result.CanCopy.Should().BeFalse();
        result.Profile.Should().BeNull();
    }

    [Fact]
    public void Trimming_inside_the_end_cylinders_of_a_curve_needs_no_confirmation()
    {
        CompositeRollProfile cvcWithEnds = CompositeRollProfile.Sequential(0.0, new[]
        {
            new SequentialSegment(ProfileTypeKeys.Cylindrical, 30.0, ParameterSet.Empty),
            new SequentialSegment(ProfileTypeKeys.Cvc, 1740.0, new CvcProfileType().Schema.CreateDefaults()),
            new SequentialSegment(ProfileTypeKeys.Cylindrical, 30.0, ParameterSet.Empty),
        });

        CopyAlignment result = ProfileCopyAlignment.Align(cvcWithEnds, 1800.0, 1790.0, 1.0, 36.0, Registry, 401);

        result.Kind.Should().Be(CopyAlignmentKind.Trimmed);
        result.NeedsConfirm.Should().BeFalse();
        result.SegmentsAfter.Should().Equal(25.0, 1740.0, 25.0);
    }
}
