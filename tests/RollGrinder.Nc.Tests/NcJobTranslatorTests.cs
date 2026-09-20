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
    public void Every_program_step_switch_reaches_the_machine()
    {
        // NC 程序按这八个开关决定要不要走那几段辅助子程序，所以八个都得下发，
        // 关着的那些写 false——"没写"和"写了 false"在 NC 侧不是一回事。
        NcDownload download = CreateTranslator(FakeTagMap.Complete())
            .Translate(CreateJob(), null, 21, Now);

        foreach (ProgramOptionDescriptor option in ProgramOptionCatalog.All)
        {
            TagWrite write = download.Writes.Single(candidate =>
                candidate.LogicalName == MachineTagKeys.JobOption(option.Key));

            write.Value.Raw.Should().Be(option.DefaultEnabled, $"开关 {option.Key} 应当按它的取值下发");
        }
    }

    [Fact]
    public void A_switch_that_is_turned_off_is_written_as_false()
    {
        GrindingJob job = CreateJob() with
        {
            ProgramOptions = ProgramOptionCatalog.Defaults
                .With(ProgramOptionKeys.PreGrindMeasure, ParameterValue.FromBoolean(false)),
        };

        NcDownload download = CreateTranslator(FakeTagMap.Complete()).Translate(job, null, 21, Now);

        download.Writes
            .Single(write => write.LogicalName == MachineTagKeys.JobOption(ProgramOptionKeys.PreGrindMeasure))
            .Value.Raw.Should().Be(false);
    }

    [Fact]
    public void A_complete_tag_map_reports_nothing_missing()
    {
        CreateTranslator(FakeTagMap.Complete()).FindMissingRequiredTags().Should().BeEmpty();
    }

    [Fact]
    public void Only_one_of_the_two_infeed_quantities_ever_reaches_the_machine()
    {
        // 这是问题的根子：如果两个进给量同时非零，NC 侧就不知道该听谁的，
        // 而这道工序的实际切除量会变成"取决于行程时间"。下发内容必须只有一个是活的。
        NcDownload download = CreateTranslator(FakeTagMap.Complete())
            .Translate(CreateJob(), null, 21, Now);

        for (int i = 0; i < download.Plans.Count; i++)
        {
            double perReversal = NumberAt(download, MachineTagKeys.JobStepInfeedPerPassRadiusMm, i);
            double continuous = NumberAt(download, MachineTagKeys.JobStepContinuousInfeedRadiusMmPerMin, i);

            (perReversal > 0.0 && continuous > 0.0).Should().BeFalse(
                $"第 {i + 1} 道工序同时下发了两种进给量");
        }
    }

    [Fact]
    public void The_feed_mode_written_matches_the_quantity_that_is_non_zero()
    {
        NcDownload download = CreateTranslator(FakeTagMap.Complete())
            .Translate(CreateJob(), null, 21, Now);

        for (int i = 0; i < download.Plans.Count; i++)
        {
            var mode = (StepFeedMode)(int)NumberAt(download, MachineTagKeys.JobStepFeedMode, i);
            double perReversal = NumberAt(download, MachineTagKeys.JobStepInfeedPerPassRadiusMm, i);
            double continuous = NumberAt(download, MachineTagKeys.JobStepContinuousInfeedRadiusMmPerMin, i);

            mode.Should().Be(download.Plans[i].FeedMode);
            switch (mode)
            {
                case StepFeedMode.Continuous:
                    perReversal.Should().Be(0.0);
                    break;

                case StepFeedMode.PerReversal:
                    continuous.Should().Be(0.0);
                    break;

                default:
                    perReversal.Should().Be(0.0);
                    continuous.Should().Be(0.0);
                    break;
            }
        }
    }

    [Fact]
    public void Speed_variation_reaches_the_machine_as_target_amplitude_and_period()
    {
        NcDownload download = CreateTranslator(FakeTagMap.Complete())
            .Translate(CreateJob(), null, 21, Now);

        int rough = download.Plans.ToList().FindIndex(plan => plan.StepTypeKey == StepTypeKeys.Rough);
        rough.Should().BeGreaterThanOrEqualTo(0);

        NumberAt(download, MachineTagKeys.JobStepSpeedVariationTarget, rough)
            .Should().Be((double)(int)SpeedVariationTarget.Workpiece, "变速默认作用在轧辊转速上");
        NumberAt(download, MachineTagKeys.JobStepSpeedVariationPercent, rough).Should().BeGreaterThan(0.0);
        NumberAt(download, MachineTagKeys.JobStepSpeedVariationPeriodSeconds, rough)
            .Should().BeGreaterThan(0.0, "只给幅度不给周期，机床没法生成这条正弦曲线");

        // 光磨不变速：转速在这一段必须稳。
        int sparkOut = download.Plans.ToList().FindIndex(plan => plan.StepTypeKey == StepTypeKeys.SparkOut);
        NumberAt(download, MachineTagKeys.JobStepSpeedVariationTarget, sparkOut)
            .Should().Be((double)(int)SpeedVariationTarget.Off);
    }

    private static double NumberAt(NcDownload download, string baseKey, int index)
    {
        string key = TagKeySyntax.Indexed(baseKey, index);
        TagWrite write = download.Writes.Single(candidate =>
            string.Equals(candidate.LogicalName, key, StringComparison.Ordinal));

        return Convert.ToDouble(write.Value.Raw, System.Globalization.CultureInfo.InvariantCulture);
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

/// <summary>数组变量的下标偏置：R[130] 起的一组参数。</summary>
public sealed class IndexedTagTests
{
    [Fact]
    public void An_index_offset_shifts_the_rendered_address()
    {
        var descriptor = new TagDescriptor(
            MachineTagKeys.JobStepPassCount,
            "ns=2;s=/Channel/Parameter/R[{index}]",
            TagDataType.Int32,
            TagAccess.ReadWrite,
            ArrayLength: 8,
            IndexOffset: 140);

        descriptor.AtIndex(0).Address.Should().Be("ns=2;s=/Channel/Parameter/R[140]");
        descriptor.AtIndex(3).Address.Should().Be("ns=2;s=/Channel/Parameter/R[143]");
        descriptor.AtIndex(3).Key.Should().Be(TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, 3));
    }

    [Fact]
    public void An_index_beyond_the_array_is_refused()
    {
        var descriptor = new TagDescriptor(
            "job.step.passCount", "ns=2;s=R[{index}]", TagDataType.Int32, TagAccess.ReadWrite, ArrayLength: 2);

        descriptor.Invoking(d => d.AtIndex(2)).Should().Throw<GatewayException>();
    }
}
