using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 辊形编辑器边编边校验（第一轮甲方测试 辊形 1⑥⑨）：
/// 伸出辊身、断开、参数越界是错误，不能保存；重叠在叠加模型里是正常用法，只作提示。
/// </summary>
public sealed class ProfileLayoutCheckTests
{
    private const double Body = 2000.0;

    private static RollProfileTypeRegistry Registry => new(new IRollProfileType[]
    {
        new CylindricalProfileType(), new TaperProfileType(), new CrownProfileType(), new CvcProfileType(),
    });

    private static RollProfileSegment Segment(int order, string type, double from, double to, ParameterSet? parameters = null) =>
        RollProfileSegment.Create(order, type, from, to, parameters ?? Registry.Get(type).Schema.CreateDefaults());

    private static IReadOnlyList<ProfileIssue> Check(params RollProfileSegment[] segments) =>
        ProfileLayoutCheck.Check(segments, Body, Registry);

    [Fact]
    public void A_full_length_main_profile_with_end_tapers_is_clean_apart_from_overlap_hints()
    {
        IReadOnlyList<ProfileIssue> issues = Check(
            Segment(1, ProfileTypeKeys.Crown, 0.0, 2000.0),
            Segment(2, ProfileTypeKeys.Taper, 0.0, 150.0),
            Segment(3, ProfileTypeKeys.Taper, 1850.0, 2000.0));

        issues.Should().OnlyContain(issue => issue.Kind == ProfileIssueKind.Overlap && !issue.IsError);
        issues.Should().HaveCount(2, "两段端部锥度各叠在主辊形上一次");
    }

    [Fact]
    public void Every_profile_type_with_its_defaults_is_clean()
    {
        // 编辑器里插一段新的，默认值不能一上来就是红的。
        foreach (IRollProfileType type in Registry.All)
        {
            Check(Segment(1, type.Key, 0.0, Body)).Should().BeEmpty(type.Key);
        }
    }

    [Fact]
    public void An_empty_profile_is_an_error()
    {
        Check().Should().ContainSingle().Which.Should().Be(new ProfileIssue(ProfileIssueKind.NoSegments, true));
    }

    [Fact]
    public void The_field_test_profile_is_flagged_as_outside_the_body_and_not_covered()
    {
        // 甲方那条：设计长度 300 mm，段填到 100–800 mm。
        IReadOnlyList<ProfileIssue> issues = ProfileLayoutCheck.Check(
            new[] { Segment(1, ProfileTypeKeys.Crown, 100.0, 800.0), Segment(2, ProfileTypeKeys.Taper, 225.0, 300.0) },
            300.0,
            Registry);

        issues.Should().Contain(i => i.Kind == ProfileIssueKind.OutsideBody && i.SegmentOrder == 1 && i.IsError);
        issues.Should().Contain(i => i.Kind == ProfileIssueKind.NotCovered && i.FromMm == 0.0 && i.ToMm == 100.0);
    }

    [Fact]
    public void Gaps_between_segments_are_errors()
    {
        IReadOnlyList<ProfileIssue> issues = Check(
            Segment(1, ProfileTypeKeys.Cylindrical, 0.0, 800.0),
            Segment(2, ProfileTypeKeys.Cylindrical, 1000.0, 1900.0));

        issues.Where(i => i.Kind == ProfileIssueKind.NotCovered).Select(i => (i.FromMm, i.ToMm))
            .Should().Equal((800.0, 1000.0), (1900.0, 2000.0));
        issues.Should().OnlyContain(i => i.IsError);
    }

    [Fact]
    public void Segments_that_just_touch_do_not_overlap()
    {
        Check(
            Segment(1, ProfileTypeKeys.Cylindrical, 0.0, 1000.0),
            Segment(2, ProfileTypeKeys.Cylindrical, 1000.0, 2000.0)).Should().BeEmpty();
    }

    [Fact]
    public void An_out_of_range_parameter_is_an_error_on_its_segment()
    {
        ParameterDescriptor bounded = new CrownProfileType().Schema.Descriptors.First(d => d.MaxValue is not null);
        ParameterSet tooBig = new CrownProfileType().Schema.CreateDefaults()
            .With(bounded.Key, ParameterValue.FromNumber(bounded.MaxValue!.Value + 1.0));

        IReadOnlyList<ProfileIssue> issues = Check(Segment(1, ProfileTypeKeys.Crown, 0.0, 2000.0, tooBig));

        issues.Should().ContainSingle().Which.Should().Match<ProfileIssue>(i =>
            i.Kind == ProfileIssueKind.ParameterInvalid && i.IsError && i.SegmentOrder == 1
            && i.Violation!.ParameterKey == bounded.Key && i.Violation.Kind == ParameterViolationKind.AboveMaximum);
    }

    [Fact]
    public void Move_down_is_the_mirror_of_move_up()
    {
        var composite = new CompositeRollProfile(new[]
        {
            Segment(1, ProfileTypeKeys.Crown, 0.0, 2000.0),
            Segment(2, ProfileTypeKeys.Taper, 0.0, 150.0),
            Segment(3, ProfileTypeKeys.Cvc, 0.0, 2000.0),
        });

        composite.MoveDown(1).Segments.Select(s => s.ProfileTypeKey)
            .Should().Equal(ProfileTypeKeys.Taper, ProfileTypeKeys.Crown, ProfileTypeKeys.Cvc);
        composite.MoveDown(3).Should().BeSameAs(composite, "最后一段下不去了");
        composite.MoveDown(1).Segments.Select(s => s.Order).Should().Equal(1, 2, 3);
    }
}
