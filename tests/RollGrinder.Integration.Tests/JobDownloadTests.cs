using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services;
using RollGrinder.Services.Jobs;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 端到端：编排 → 校验 → 生成 NC 参数 → 写入仿真机床 → 落库。
/// </summary>
public sealed class JobDownloadTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    /// <summary>装配之前改一改现场配置；null 表示照样例原样用。</summary>
    private Func<AppOptions, Task>? patchConfiguration;

    private async Task<ServiceProvider> BuildAsync(params string[] extraArgs)
    {
        string[] args = new[] { "--gateway", "sim" }.Concat(extraArgs).ToArray();
        AppOptions options = AppOptions.Parse(args, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);

        if (this.patchConfiguration is not null)
        {
            await this.patchConfiguration(options);
        }

        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);

        var database = new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName));
        await database.MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 把现场配置里某个选装装置关掉，模拟"这台机床没装这件东西"。
    ///
    /// 样例配置是按 MK84160 实机写的，该装的都装了——所以"装置缺失"这条规则
    /// 得自己造一台缺装置的机床来试，而不是指望样例里正好有一项是 false。
    /// </summary>
    private async Task RemoveMachineOptionAsync(AppOptions options, string optionKey)
    {
        string path = options.MachineConfigFilePath;
        JsonNode machine = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        machine["options"]![optionKey] = false;
        await File.WriteAllTextAsync(path, machine.ToJsonString());
    }

    private static GrindingJob CreateJob(string jobId = "J-1", double crownDiameterMicrometer = 120.0)
    {
        var crown = new CrownProfileType();
        return GrindingJob.Create(
            jobId,
            "R-1",
            RollGeometry.FromDiameter(2000.0, 650.0),
            ProfileTypeKeys.Crown,
            crown.Schema.CreateDefaults()
                .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(crownDiameterMicrometer)),
            new[]
            {
                new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
                new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
                new GrindingJobStep(3, StepTypeKeys.SparkOut, new SparkOutStepType().Schema.CreateDefaults()),
            });
    }

    [Fact]
    public async Task A_program_of_every_available_step_with_defaults_passes_validation()
    {
        // 工序页"校验"键跑的是这一套；每种工序拿默认值拼一支程序，应该一条问题都没有，
        // 否则现场一插工序就是红的。自检的 Steps/FullJobValidates 用的也是这样一支。
        await using ServiceProvider services = await BuildAsync();
        var stepTypes = services.GetRequiredService<GrindingStepTypeRegistry>();
        MachineCapability capability = services.GetRequiredService<MachineCapability>();
        GrindingJobStep[] steps = stepTypes.All
            .Where(capability.Supports)
            .Select((type, i) => new GrindingJobStep(i + 1, type.Key, type.Schema.CreateDefaults()))
            .ToArray();
        GrindingJob job = GrindingJob.Create(
            "J-ALL", "R-ALL", RollGeometry.FromDiameter(2000.0, 600.0),
            ProfileTypeKeys.Cylindrical, new CylindricalProfileType().Schema.CreateDefaults(), steps);

        ParameterValidationResult result = services.GetRequiredService<GrindingJobValidator>().Validate(job, capability);

        result.Violations.Should().BeEmpty();
        services.GetRequiredService<GrindingJobValidator>()
            .ValidateSteps(steps, job.Geometry, capability).IsValid.Should().BeTrue("存程序时跑的是工序那一半");
    }

    [Fact]
    public async Task A_valid_job_is_handed_over_and_archived()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);

        JobDownloadResult result = await services.GetRequiredService<IJobDownloadService>()
            .DownloadAsync(CreateJob(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Violations.Should().BeEmpty();
        result.MissingTags.Should().BeEmpty();
        result.RecordId.Should().NotBeNullOrEmpty();
        result.WriteCount.Should().BeGreaterThan(20);

        (GrindingJob Job, JobState State)? stored = await services.GetRequiredService<IJobRepository>()
            .GetAsync("J-1", CancellationToken.None);
        stored!.Value.State.Should().Be(JobState.Handed);

        GrindingRecord? record = await services.GetRequiredService<IGrindingRecordRepository>()
            .GetAsync(result.RecordId!, CancellationToken.None);
        record!.JobId.Should().Be("J-1");
        record.FinishedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Handover_puts_the_simulated_machine_into_running_state()
    {
        await using ServiceProvider services = await BuildAsync();
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();
        await gateway.ConnectAsync(CancellationToken.None);

        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(CreateJob(), CancellationToken.None);

        MachineStateSnapshot snapshot = await gateway.ReadStateAsync(
            MachineTagKeys.MonitoringKeys(services.GetRequiredService<MachineDescription>()),
            CancellationToken.None);

        snapshot.GetNumberOrNull(MachineTagKeys.ChannelState).Should().Be((double)(int)NcChannelState.Running);
    }

    [Fact]
    public async Task An_out_of_range_job_writes_nothing()
    {
        await using ServiceProvider services = await BuildAsync();
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();
        await gateway.ConnectAsync(CancellationToken.None);

        var rough = new RoughGrindingStepType();
        GrindingJob job = GrindingJob.Create(
            "J-bad",
            "R-1",
            RollGeometry.FromDiameter(2000.0, 650.0),
            ProfileTypeKeys.Cylindrical,
            ParameterSet.Empty,
            new[]
            {
                // 把周期分量顶到 200 µm（直径量），单刀切深限幅才轮得上。
                new GrindingJobStep(
                    1,
                    StepTypeKeys.Rough,
                    rough.Schema.CreateDefaults()
                        .With(
                            StepParameterKeys.InfeedPerPassDiameterMicrometer,
                            ParameterValue.FromNumber(200.0))),
            });

        JobDownloadResult result = await services.GetRequiredService<IJobDownloadService>()
            .DownloadAsync(job, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Violations.Should().Contain(violation =>
            violation.Kind == ParameterViolationKind.ExceedsMachineLimit);
        result.WriteCount.Should().Be(0);

        MachineStateSnapshot snapshot = await gateway.ReadStateAsync(
            MachineTagKeys.MonitoringKeys(services.GetRequiredService<MachineDescription>()),
            CancellationToken.None);
        snapshot.GetNumberOrNull(MachineTagKeys.ChannelState)
            .Should().Be((double)(int)NcChannelState.Reset, "校验不过就不该有任何写入");

        (await services.GetRequiredService<IJobRepository>().GetAsync("J-bad", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_step_the_machine_is_not_equipped_for_is_refused_and_writes_nothing()
    {
        // 造一台没装探伤仪的机床：实机是装了的，但这条规则本身与哪台机床无关。
        this.patchConfiguration = options =>
            RemoveMachineOptionAsync(options, MachineOptionKeys.EddyCurrentTester);

        await using ServiceProvider services = await BuildAsync();
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();
        await gateway.ConnectAsync(CancellationToken.None);

        GrindingJob job = GrindingJob.Create(
            "J-nodevice",
            "R-1",
            RollGeometry.FromDiameter(2000.0, 650.0),
            ProfileTypeKeys.Cylindrical,
            ParameterSet.Empty,
            new[]
            {
                new GrindingJobStep(
                    1, StepTypeKeys.EddyCurrent, new EddyCurrentStepType().Schema.CreateDefaults()),
            });

        JobDownloadResult result = await services.GetRequiredService<IJobDownloadService>()
            .DownloadAsync(job, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Violations.Should().Contain(violation =>
            violation.ParameterKey == StepTypeKeys.EddyCurrent
            && violation.Kind == ParameterViolationKind.MachineOptionMissing);
        result.WriteCount.Should().Be(0, "机床装不了的工序，一个字节也不该写下去");

        MachineStateSnapshot snapshot = await gateway.ReadStateAsync(
            MachineTagKeys.MonitoringKeys(services.GetRequiredService<MachineDescription>()),
            CancellationToken.None);
        snapshot.GetNumberOrNull(MachineTagKeys.ChannelState)
            .Should().Be((double)(int)NcChannelState.Reset);
    }

    [Fact]
    public async Task A_step_the_machine_is_equipped_for_goes_through()
    {
        // 同一份样例里 hasWheelDresser = true，所以砂轮修整能编能下发。
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);

        GrindingJob job = GrindingJob.Create(
            "J-dress",
            "R-1",
            RollGeometry.FromDiameter(2000.0, 650.0),
            ProfileTypeKeys.Cylindrical,
            ParameterSet.Empty,
            new[]
            {
                new GrindingJobStep(
                    1, StepTypeKeys.WheelDress, new WheelDressStepType().Schema.CreateDefaults()),
            });

        JobDownloadResult result = await services.GetRequiredService<IJobDownloadService>()
            .DownloadAsync(job, CancellationToken.None);

        result.Violations.Should().NotContain(violation =>
            violation.Kind == ParameterViolationKind.MachineOptionMissing);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task The_machine_capability_mirrors_the_sample_configuration()
    {
        await using ServiceProvider services = await BuildAsync();
        MachineCapability capability = services.GetRequiredService<MachineCapability>();

        // 样例配置按 MK84160 实机写：原理图确认探伤仪 -E44 实装。
        capability.InstalledOptions.Should().Contain(MachineOptionKeys.WheelDresser);
        capability.InstalledOptions.Should().Contain(MachineOptionKeys.EddyCurrentTester);
        capability.CanMeasureDiameter.Should().BeTrue("样例机床装了测径仪");
        capability.Supports(new WheelDressStepType()).Should().BeTrue();
        capability.Supports(new EddyCurrentStepType()).Should().BeTrue();

        // 实机 6 个进给轴 + 2 个主轴；砂轮摆角实机没有。
        capability.AvailableAxisRoles.Should().Contain(new[]
        {
            MachineAxisRoleNames.RollProfile, MachineAxisRoles.MeasuringCarriage,
            MachineAxisRoles.InfeedRadius, MachineAxisRoles.Carriage,
        });
        capability.AvailableAxisRoles.Should().NotContain(MachineAxisRoles.WheelSwivel);
    }

    [Fact]
    public async Task A_stored_compensation_is_folded_into_the_handed_over_profile()
    {
        await using ServiceProvider services = await BuildAsync();
        await services.GetRequiredService<IMachineGateway>().ConnectAsync(CancellationToken.None);
        IJobDownloadService download = services.GetRequiredService<IJobDownloadService>();

        // 先下发一次，作业与辊件才存在，补偿才能挂上去。
        await download.DownloadAsync(CreateJob(), CancellationToken.None);

        await services.GetRequiredService<ICompensationRepository>().AddAsync(
            new CompensationRecord("C-1", "J-1", DateTimeOffset.UnixEpoch, null, new[]
            {
                new ProfilePoint(0.0, 0.005),
                new ProfilePoint(2000.0, 0.005),
            }),
            CancellationToken.None);

        JobDownloadResult result = await download.DownloadAsync(CreateJob(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        // 补偿是整体抬高 5 µm 半径量，端点处的下发值应当由 0 变成 0.005。
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();
        TagValue endPoint = await gateway.ReadTagAsync(
            TagKeySyntax.Indexed(MachineTagKeys.JobProfileRadiusOffsetMm, 0), CancellationToken.None);
        Convert.ToDouble(endPoint.Raw, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeApproximately(0.005, 1e-9);
    }

    [Fact]
    public async Task The_handover_flag_is_written_last()
    {
        await using ServiceProvider services = await BuildAsync();
        IMachineGateway gateway = services.GetRequiredService<IMachineGateway>();
        await gateway.ConnectAsync(CancellationToken.None);

        await services.GetRequiredService<IJobDownloadService>().DownloadAsync(CreateJob(), CancellationToken.None);

        // 参数有效标志置真时，辊形与工序参数必须都已经在 NC 里。
        TagValue valid = await gateway.ReadTagAsync(MachineTagKeys.JobParametersValid, CancellationToken.None);
        valid.Raw.Should().Be(true);

        TagValue stepCount = await gateway.ReadTagAsync(MachineTagKeys.JobStepCount, CancellationToken.None);
        Convert.ToInt32(stepCount.Raw, System.Globalization.CultureInfo.InvariantCulture).Should().Be(3);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        this.workspace.Dispose();
    }
}
