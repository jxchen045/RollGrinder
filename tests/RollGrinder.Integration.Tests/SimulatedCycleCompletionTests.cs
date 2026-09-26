using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Sim;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 仿真机床的"循环正常结束"位（job.cycleComplete，对应 NC 程序里 R124=0 / R124=1）：
/// 走完最后一道才置 1；半路被中止不置；测量这类没有进给的工序不去除材料。
/// </summary>
public sealed class SimulatedCycleCompletionTests
{
    private static async Task<SimulatedMachine> BuildAsync(TempWorkspace workspace)
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, workspace.CreateSampleDirectory(), CancellationToken.None);
        MachineDescription machine = await new JsonMachineConfigProvider(options).GetMachineAsync(CancellationToken.None);
        return new SimulatedMachine(machine);
    }

    private static void Write(SimulatedMachine sim, string key, object value) =>
        sim.Write(key, new TagValue(key, value is bool ? TagDataType.Boolean : TagDataType.Double, value, DateTimeOffset.UtcNow));

    /// <summary>下发一支两道工序的作业：第 1 道磨 2 刀，第 2 道是测量（0 刀、无进给）。</summary>
    private static void HandOver(SimulatedMachine sim)
    {
        Write(sim, MachineTagKeys.JobRollRadiusMm, 300.0);
        Write(sim, MachineTagKeys.JobBodyLengthMm, 100.0);
        Write(sim, MachineTagKeys.JobFeedMmPerMin, 2000.0);
        Write(sim, MachineTagKeys.JobStepCount, 2.0);
        Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, 0), 2.0);
        Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepInfeedPerPassRadiusMm, 0), 0.01);
        Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, 1), 0.0);
        Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepInfeedPerPassRadiusMm, 1), 0.0);
        Write(sim, MachineTagKeys.JobParametersValid, true);
    }

    private static int CycleComplete(SimulatedMachine sim) => Convert.ToInt32(sim.Read(MachineTagKeys.JobCycleComplete));

    [Fact]
    public async Task Finishing_the_last_step_sets_the_flag_and_resets_the_channel()
    {
        using var workspace = new TempWorkspace();
        SimulatedMachine sim = await BuildAsync(workspace);
        HandOver(sim);

        sim.ChannelState.Should().Be(NcChannelState.Running);
        CycleComplete(sim).Should().Be(0, "程序开头 R124=0");

        for (int i = 0; i < 10_000 && sim.ChannelState == NcChannelState.Running; i++)
        {
            sim.Advance(TimeSpan.FromSeconds(0.5));
        }

        sim.ChannelState.Should().Be(NcChannelState.Reset);
        CycleComplete(sim).Should().Be(1, "走完最后一道、M30 之前 R124=1");
    }

    [Fact]
    public async Task A_program_that_starts_with_a_non_traversing_step_takes_real_time()
    {
        // 第一轮甲方测试：程序以"开始"打头（进给 0），仿真把它当成每一拍走完一刀，
        // 9 道工序 3 秒就"磨完"。现在按每道自己的进给走，拖板不走的工序原地停一会儿。
        using var workspace = new TempWorkspace();
        SimulatedMachine sim = await BuildAsync(workspace);
        Write(sim, MachineTagKeys.JobRollRadiusMm, 300.0);
        Write(sim, MachineTagKeys.JobBodyLengthMm, 2000.0);
        Write(sim, MachineTagKeys.JobFeedMmPerMin, 0.0);
        Write(sim, MachineTagKeys.JobStepCount, 3.0);
        double[] feeds = { 0.0, 2300.0, 0.0 };
        double[] passes = { 0.0, 10.0, 0.0 };
        for (int i = 0; i < 3; i++)
        {
            Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepFeedMmPerMin, i), feeds[i]);
            Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, i), passes[i]);
            Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepInfeedPerPassRadiusMm, i), i == 1 ? 0.0025 : 0.0);
        }

        Write(sim, MachineTagKeys.JobParametersValid, true);

        double seconds = 0.0;
        while (sim.ChannelState == NcChannelState.Running && seconds < 3600.0)
        {
            sim.Advance(TimeSpan.FromSeconds(0.1));
            seconds += 0.1;
            if (seconds is > 29.95 and < 30.05)
            {
                sim.ChannelState.Should().Be(NcChannelState.Running, "30 秒时粗磨才刚开始第一刀");
            }
        }

        // 开始 5 s + 粗磨 10 刀 × (2000 mm / 2300 mm/min ≈ 52 s) + 结束 5 s ≈ 532 s。
        seconds.Should().BeInRange(500.0, 560.0);
        CycleComplete(sim).Should().Be(1);
    }

    [Fact]
    public async Task An_aborted_program_resets_without_the_flag()
    {
        using var workspace = new TempWorkspace();
        SimulatedMachine sim = await BuildAsync(workspace);
        HandOver(sim);
        sim.Advance(TimeSpan.FromSeconds(1));

        Write(sim, MachineTagKeys.JobParametersValid, false);

        sim.ChannelState.Should().Be(NcChannelState.Reset);
        CycleComplete(sim).Should().Be(0, "半路停下的不算磨完");
    }

    [Fact]
    public async Task A_new_program_clears_the_flag_of_the_previous_one()
    {
        using var workspace = new TempWorkspace();
        SimulatedMachine sim = await BuildAsync(workspace);
        HandOver(sim);
        for (int i = 0; i < 10_000 && sim.ChannelState == NcChannelState.Running; i++)
        {
            sim.Advance(TimeSpan.FromSeconds(0.5));
        }

        CycleComplete(sim).Should().Be(1);
        HandOver(sim);

        CycleComplete(sim).Should().Be(0, "上一支辊的结束位不能被当成这一支的");
    }

    [Fact]
    public async Task A_step_without_infeed_removes_no_material()
    {
        using var workspace = new TempWorkspace();
        SimulatedMachine sim = await BuildAsync(workspace);
        Write(sim, MachineTagKeys.JobRollRadiusMm, 300.0);
        Write(sim, MachineTagKeys.JobBodyLengthMm, 100.0);
        Write(sim, MachineTagKeys.JobFeedMmPerMin, 2000.0);
        Write(sim, MachineTagKeys.JobStepCount, 1.0);
        Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepPassCount, 0), 0.0);
        Write(sim, TagKeySyntax.Indexed(MachineTagKeys.JobStepInfeedPerPassRadiusMm, 0), 0.0);
        Write(sim, MachineTagKeys.JobParametersValid, true);
        double before = sim.CurrentRadiusMm;

        sim.Advance(TimeSpan.FromSeconds(1));

        sim.CurrentRadiusMm.Should().Be(before, "测量工序量的是来料，不该一边量一边磨掉");
    }
}
