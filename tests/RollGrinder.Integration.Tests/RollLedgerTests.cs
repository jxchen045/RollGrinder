using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Core.Geometry;
using RollGrinder.Data;
using RollGrinder.Data.Model;
using RollGrinder.Services;
using RollGrinder.Services.Records;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 轧辊台账（阶段 1）：尺寸属于轧辊本身，在台账里登记和修改，作业只是选一支台账里的辊。
/// </summary>
public sealed class RollLedgerTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    public void Dispose() => this.workspace.Dispose();

    private async Task<ServiceProvider> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);
        var configProvider = new JsonMachineConfigProvider(options);
        MachineDescription machine = await configProvider.GetMachineAsync(CancellationToken.None);
        ITagMap tagMap = await configProvider.GetTagMapAsync(CancellationToken.None);
        HmiSettings settings = await JsonHmiSettingsProvider.LoadAsync(options, CancellationToken.None);
        await new SqliteDatabase(Path.Combine(options.DataDirectory, SqliteDatabase.FileName)).MigrateAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddMachineAccess(options, machine, tagMap);
        services.AddDomainRegistries();
        services.AddDataStore(options);
        services.AddApplicationServices(settings);
        return services.BuildServiceProvider();
    }

    private static RollRecord WorkRoll(string id = "WR-1001") =>
        new RollRecord(id, id, RollGeometry.FromDiameter(2000.0, 650.0), "Cr5", DateTimeOffset.UnixEpoch)
        {
            Kind = RollKind.WorkRoll,
            CurrentDiameterMm = 642.5,
            Data = new RollDataSheet(0.0, 1900.0, 10.0, 5200.0, 800.0, 790.0),
        };

    [Fact]
    public async Task A_new_roll_is_registered_with_its_kind_and_current_diameter()
    {
        await using ServiceProvider services = await BuildAsync();
        var ledger = services.GetRequiredService<IRollLedgerService>();

        RollLedgerSaveResult result = await ledger.SaveAsync(WorkRoll(), isNew: true, CancellationToken.None);

        result.Saved.Should().BeTrue();
        RollRecord? stored = await ledger.GetAsync("WR-1001", CancellationToken.None);
        stored!.Kind.Should().Be(RollKind.WorkRoll);
        stored.CurrentDiameterMm.Should().Be(642.5);
        stored.Data.TotalWeightKg.Should().Be(6790.0);

        RollLedgerRow row = (await services.GetRequiredService<IRecordService>().LoadLedgerAsync(10, CancellationToken.None)).Single();
        row.Kind.Should().Be(RollKind.WorkRoll);
        row.CurrentDiameterMm.Should().Be(642.5);
    }

    [Fact]
    public async Task A_roll_number_is_registered_only_once_but_can_be_edited()
    {
        await using ServiceProvider services = await BuildAsync();
        var ledger = services.GetRequiredService<IRollLedgerService>();
        await ledger.SaveAsync(WorkRoll(), isNew: true, CancellationToken.None);

        (await ledger.SaveAsync(WorkRoll(), isNew: true, CancellationToken.None))
            .Problems.Should().Equal(RollLedgerProblem.RollIdTaken);

        RollLedgerSaveResult edited = await ledger.SaveAsync(WorkRoll() with { CurrentDiameterMm = 640.0 }, isNew: false, CancellationToken.None);
        edited.Saved.Should().BeTrue("改已有的辊不算重号");
        (await ledger.GetAsync("WR-1001", CancellationToken.None))!.CurrentDiameterMm.Should().Be(640.0);
    }

    [Fact]
    public async Task Sizes_outside_what_this_machine_can_grind_are_refused_with_every_reason()
    {
        await using ServiceProvider services = await BuildAsync();
        var ledger = services.GetRequiredService<IRollLedgerService>();
        RollRecord bad = new RollRecord(" ", "x", RollGeometry.FromDiameter(9000.0, 5000.0), null, DateTimeOffset.UnixEpoch)
        {
            CurrentDiameterMm = 10.0,
            Data = RollDataSheet.Empty with { NetWeightKg = -1.0 },
        };

        RollLedgerSaveResult result = await ledger.SaveAsync(bad, isNew: true, CancellationToken.None);

        result.Saved.Should().BeFalse();
        result.Problems.Should().BeEquivalentTo(new[]
        {
            RollLedgerProblem.MissingRollId, RollLedgerProblem.BodyLengthOutOfRange, RollLedgerProblem.DiameterOutOfRange,
            RollLedgerProblem.CurrentDiameterOutOfRange, RollLedgerProblem.NegativeWeight,
        });
    }

    [Fact]
    public async Task Rolls_registered_before_the_ledger_existed_read_back_as_unspecified()
    {
        await using ServiceProvider services = await BuildAsync();
        var repository = services.GetRequiredService<IRollRepository>();
        await repository.UpsertAsync(
            new RollRecord("OLD-1", "OLD-1", RollGeometry.FromDiameter(2000.0, 650.0), null, DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        RollRecord old = (await repository.GetAsync("OLD-1", CancellationToken.None))!;

        old.Kind.Should().Be(RollKind.Unspecified);
        old.CurrentDiameterMm.Should().BeNull("没登记就是没登记，不是 0");
    }
}
