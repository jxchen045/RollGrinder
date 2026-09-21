using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using Xunit;

namespace RollGrinder.Core.Tests;

/// <summary>
/// 「行 = 参数，列 = 工序」的投影。自动磨削时要一屏看完，
/// 一次只显示一道工序的参数格，操作工得靠翻页在脑子里拼。
/// </summary>
public sealed class StepParameterMatrixTests
{
    private static readonly RollGeometry Geometry = RollGeometry.FromDiameter(2000.0, 650.0);

    private static GrindingStepTypeRegistry Registry => new(new IGrindingStepType[]
    {
        new StartStepType(), new ShortStrokeStepType(), new RoughGrindingStepType(),
        new FinishGrindingStepType(), new MeasureStepType(), new EndStepType(),
    });

    private static GrindingJob JobWith(params (int Order, string TypeKey)[] steps)
    {
        GrindingStepTypeRegistry registry = Registry;
        return GrindingJob.Create(
            "J-1",
            "R-1",
            Geometry,
            ProfileTypeKeys.Cylindrical,
            new CylindricalProfileType().Schema.CreateDefaults(),
            steps.Select(step => new GrindingJobStep(
                step.Order, step.TypeKey, registry.Get(step.TypeKey).Schema.CreateDefaults())));
    }

    [Fact]
    public void Every_step_gets_a_column_and_every_parameter_a_row()
    {
        GrindingJob job = JobWith((1, StepTypeKeys.Start), (2, StepTypeKeys.Rough), (3, StepTypeKeys.End));

        StepParameterMatrix matrix = StepParameterMatrix.Build(job, Registry);

        matrix.Steps.Should().HaveCount(3);
        matrix.Rows.Should().NotBeEmpty();
        matrix.Rows.Should().OnlyContain(row => row.Cells.Count == 3, "每一行的格子数必须等于工序数");
        matrix.Rows.Select(row => row.Descriptor.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_step_that_does_not_have_a_parameter_leaves_the_cell_blank()
    {
        // 开始与结束工序没有任何参数，粗磨有一整套：那两列在每一行上都该是空的。
        GrindingJob job = JobWith((1, StepTypeKeys.Start), (2, StepTypeKeys.Rough), (3, StepTypeKeys.End));

        StepParameterMatrix matrix = StepParameterMatrix.Build(job, Registry);
        StepMatrixRow? feed = matrix.RowOf(StepParameterKeys.FeedMmPerMin);

        feed.Should().NotBeNull();
        feed!.Cells[0].IsApplicable.Should().BeFalse("开始工序不进给");
        feed.Cells[1].IsApplicable.Should().BeTrue();
        feed.Cells[2].IsApplicable.Should().BeFalse("结束工序不进给");
    }

    [Fact]
    public void The_row_order_follows_where_each_parameter_first_shows_up()
    {
        // 开始工序没有参数，所以主序由第一道纵磨工序的 schema 决定——
        // 砂轮线速度、头架转速、拖板速度、两路进给……与工艺人员看参数的顺序一致。
        GrindingJob job = JobWith((1, StepTypeKeys.Start), (2, StepTypeKeys.Rough), (3, StepTypeKeys.Measure));

        string[] keys = StepParameterMatrix.Build(job, Registry).Rows
            .Select(row => row.Descriptor.Key).ToArray();

        keys.Should().StartWith(new[]
        {
            StepParameterKeys.WheelSurfaceSpeedMPerSec,
            StepParameterKeys.WorkpieceSpeedRpm,
            StepParameterKeys.FeedMmPerMin,
            StepParameterKeys.ContinuousInfeedDiameterMicrometerPerMin,
            StepParameterKeys.InfeedPerPassDiameterMicrometer,
        });

        // 测量工序带来的新参数接在后面，不会插到前面去。
        keys.Should().Contain(StepParameterKeys.MeasurePointCount);
        Array.IndexOf(keys, StepParameterKeys.MeasurePointCount)
            .Should().BeGreaterThan(Array.IndexOf(keys, StepParameterKeys.FeedMmPerMin));
    }

    [Fact]
    public void Cells_carry_the_value_the_step_actually_has()
    {
        var rough = new RoughGrindingStepType();
        GrindingJob job = GrindingJob.Create(
            "J-1",
            "R-1",
            Geometry,
            ProfileTypeKeys.Cylindrical,
            new CylindricalProfileType().Schema.CreateDefaults(),
            new[]
            {
                new GrindingJobStep(
                    1,
                    StepTypeKeys.Rough,
                    rough.Schema.CreateDefaults()
                        .With(StepParameterKeys.FeedMmPerMin, ParameterValue.FromNumber(1234.0))),
                new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
            });

        StepMatrixRow feed = StepParameterMatrix.Build(job, Registry).RowOf(StepParameterKeys.FeedMmPerMin)!;

        feed.Cells[0].Value!.Number.Should().Be(1234.0);
        feed.Cells[1].Value!.Number.Should().Be(800.0, "精磨用它自己的默认值");
    }

    [Fact]
    public void A_step_stored_without_a_parameter_shows_its_default_rather_than_a_hole()
    {
        // 从旧程序里调出来、少存了一个键的工序：矩阵上该显示这类工序的默认值，
        // 而不是空格——空格的含义是"这类工序没有这个参数"，两者不能混。
        GrindingJob job = GrindingJob.Create(
            "J-1",
            "R-1",
            Geometry,
            ProfileTypeKeys.Cylindrical,
            new CylindricalProfileType().Schema.CreateDefaults(),
            new[] { new GrindingJobStep(1, StepTypeKeys.Rough, ParameterSet.Empty) });

        StepMatrixRow feed = StepParameterMatrix.Build(job, Registry).RowOf(StepParameterKeys.FeedMmPerMin)!;

        feed.Cells[0].IsApplicable.Should().BeTrue();
        feed.Cells[0].Value!.Number.Should().Be(2300.0);
    }

    [Fact]
    public void The_cells_carry_the_step_order_so_the_view_can_line_them_up()
    {
        GrindingJob job = JobWith((1, StepTypeKeys.Rough), (2, StepTypeKeys.Finish), (3, StepTypeKeys.End));

        StepParameterMatrix matrix = StepParameterMatrix.Build(job, Registry);

        matrix.Rows.Should().OnlyContain(row =>
            row.Cells[0].StepOrder == 1 && row.Cells[1].StepOrder == 2 && row.Cells[2].StepOrder == 3);
    }

    [Fact]
    public void An_unknown_parameter_key_has_no_row()
    {
        GrindingJob job = JobWith((1, StepTypeKeys.Rough));

        StepParameterMatrix.Build(job, Registry).RowOf("nothing").Should().BeNull();
    }
}
