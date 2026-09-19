using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Core.Units;
using Xunit;

namespace RollGrinder.Nc.Tests;

public sealed class NcJobTranslatorTests
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static MachineDescription CreateMachine(IReadOnlyDictionary<string, int>? stepTypeCodes = null) => new(
        1,
        "RG-1",
        "test",
        new ControllerDescription("SinumerikOne", 1),
        new[] { new AxisDescription("X", MachineAxisRoles.InfeedRadius, true, AxisClosedLoopKind.FullClosed) },
        Array.Empty<MeasurementChannelDescription>(),
        new Dictionary<string, bool>(),
        new Dictionary<string, double>(),
        new WorkpieceLimits(300.0, 5200.0, 150.0, 1300.0, 42000.0),
        stepTypeCodes ?? new Dictionary<string, int>
        {
            [StepTypeKeys.Rough] = 1,
            [StepTypeKeys.Finish] = 2,
            [StepTypeKeys.SparkOut] = 3,
            [StepTypeKeys.Measure] = 4,
        });

    private static NcJobTranslator CreateTranslator(ITagMap tagMap, MachineDescription? machine = null) => new(
        new RollProfileTypeRegistry(new IRollProfileType[] { new CylindricalProfileType(), new CrownProfileType() }),
        new GrindingStepTypeRegistry(new IGrindingStepType[]
        {
            new RoughGrindingStepType(), new FinishGrindingStepType(), new SparkOutStepType(), new MeasureStepType(),
        }),
        tagMap,
        machine ?? CreateMachine());

    private static GrindingJob CreateJob(double crownDiameterMicrometer = 120.0)
    {
        var crown = new CrownProfileType();
        return GrindingJob.Create(
            "J-1",
            "R-1",
            Geometry,
            ProfileTypeKeys.Crown,
            crown.Schema.CreateDefaults()
                .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(crownDiameterMicrometer)),
            new[]
            {
                new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
                new GrindingJobStep(2, StepTypeKeys.SparkOut, new SparkOutStepType().Schema.CreateDefaults()),
            });
    }

    private static double NumberOf(NcDownload download, string logicalName) =>
        Convert.ToDouble(
            download.Writes.Single(write => write.LogicalName == logicalName).Value.Raw,
            System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Handover_flag_is_the_very_last_write()
    {
        NcDownload download = CreateTranslator(FakeTagMap.Complete()).Translate(CreateJob(), null, 21, Now);

        download.Writes[^1].LogicalName.Should().Be(MachineTagKeys.JobParametersValid);
        download.Writes[^1].Value.Raw.Should().Be(true);
        download.Writes.Count(write => write.LogicalName == MachineTagKeys.JobParametersValid)
            .Should().Be(1, "整组参数写完之前 NC 不得认这份数据");
    }

    [Fact]
    public void Geometry_is_handed_over_as_radius_in_millimetres()
    {
        NcDownload download = CreateTranslator(FakeTagMap.Complete()).Translate(CreateJob(), null, 21, Now);

        NumberOf(download, MachineTagKeys.JobRollRadiusMm).Should().Be(325.0);
        NumberOf(download, MachineTagKeys.JobBodyLengthMm).Should().Be(2000.0);
    }

    [Fact]
    public void Steps_are_handed_over_with_their_configured_type_codes()
    {
        NcDownload download = CreateTranslator(FakeTagMap.Complete()).Translate(CreateJob(), null, 21, Now);

        NumberOf(download, MachineTagKeys.JobStepCount).Should().Be(2.0);
        NumberOf(download, TagKeySyntax.Indexed(MachineTagKeys.JobStepTypeCode, 0)).Should().Be(1.0);
        NumberOf(download, TagKeySyntax.Indexed(MachineTagKeys.JobStepTypeCode, 1)).Should().Be(3.0);
        NumberOf(download, TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, 0))
            .Should().Be(download.Plans[0].PassCount);
    }

    [Fact]
    public void An_unmapped_step_type_code_is_refused_rather_than_guessed()
    {
        NcJobTranslator translator = CreateTranslator(
            FakeTagMap.Complete(),
            CreateMachine(new Dictionary<string, int> { [StepTypeKeys.Rough] = 1 }));

        translator.Invoking(t => t.Translate(CreateJob(), null, 21, Now))
            .Should().Throw<GatewayException>().WithMessage("*SparkOut*");
    }

    [Fact]
    public void Profile_points_are_handed_over_as_radius_offsets()
    {
        NcDownload download = CreateTranslator(FakeTagMap.Complete()).Translate(CreateJob(120.0), null, 21, Now);

        NumberOf(download, MachineTagKeys.JobProfilePointCount).Should().Be(21.0);
        NumberOf(download, TagKeySyntax.Indexed(MachineTagKeys.JobProfileBodyPositionMm, 0)).Should().Be(0.0);
        NumberOf(download, TagKeySyntax.Indexed(MachineTagKeys.JobProfileBodyPositionMm, 20)).Should().Be(2000.0);

        // 中点的直径量凸度应等于设定值。
        double midRadiusOffsetMm = NumberOf(download, TagKeySyntax.Indexed(MachineTagKeys.JobProfileRadiusOffsetMm, 10));
        UnitConversion.RadiusMmToDiameterMicrometer(midRadiusOffsetMm).Should().BeApproximately(120.0, 1e-6);
    }

    [Fact]
    public void Sample_count_is_capped_by_the_slots_the_tag_map_provides()
    {
        NcDownload download = CreateTranslator(FakeTagMap.Complete(profileSlots: 11))
            .Translate(CreateJob(), null, requestedProfileSampleCount: 101, Now);

        download.TargetProfile.Points.Should().HaveCount(11);
        NumberOf(download, MachineTagKeys.JobProfilePointCount).Should().Be(11.0);
    }

    [Fact]
    public void More_steps_than_slots_is_refused()
    {
        var rough = new RoughGrindingStepType();
        GrindingJob job = GrindingJob.Create(
            "J-2", "R-1", Geometry, ProfileTypeKeys.Cylindrical, ParameterSet.Empty,
            Enumerable.Range(1, 3).Select(order =>
                new GrindingJobStep(order, StepTypeKeys.Rough, rough.Schema.CreateDefaults())).ToArray());

        CreateTranslator(FakeTagMap.Complete(stepSlots: 2)).Invoking(t => t.Translate(job, null, 21, Now))
            .Should().Throw<GatewayException>().WithMessage("*step slots*");
    }

    [Fact]
    public void Compensation_is_added_on_top_of_the_target_profile()
    {
        RollProfile compensation = RollProfile.Sample(
            Geometry.BodyLengthMm, 21, _ => UnitConversion.DiameterMicrometerToRadiusMm(10.0));

        NcDownload download = CreateTranslator(FakeTagMap.Complete()).Translate(CreateJob(120.0), compensation, 21, Now);

        double midRadiusOffsetMm = NumberOf(download, TagKeySyntax.Indexed(MachineTagKeys.JobProfileRadiusOffsetMm, 10));
        UnitConversion.RadiusMmToDiameterMicrometer(midRadiusOffsetMm).Should().BeApproximately(130.0, 1e-6);
    }

    [Fact]
    public void Missing_required_tags_are_reported_before_anything_is_written()
    {
        NcJobTranslator translator = CreateTranslator(
            FakeTagMap.Complete().Without(MachineTagKeys.JobRollRadiusMm));

        translator.FindMissingRequiredTags().Should().ContainSingle()
            .Which.Should().Be(MachineTagKeys.JobRollRadiusMm);
    }

    [Fact]
    public void A_complete_tag_map_reports_nothing_missing()
    {
        CreateTranslator(FakeTagMap.Complete()).FindMissingRequiredTags().Should().BeEmpty();
    }

    [Fact]
    public void A_tag_map_without_profile_slots_cannot_hand_over_a_profile()
    {
        NcJobTranslator translator = CreateTranslator(
            FakeTagMap.Complete().Without(MachineTagKeys.JobProfileBodyPositionMm));

        translator.Invoking(t => t.Translate(CreateJob(), null, 21, Now))
            .Should().Throw<GatewayException>();
    }
}
