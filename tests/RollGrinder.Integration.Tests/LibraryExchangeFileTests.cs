using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;
using RollGrinder.Core.Steps;
using RollGrinder.Data;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>库区"导出到 U 盘 / 从 U 盘导入"的交换文件（界面最终稿 5.9）。</summary>
public sealed class LibraryExchangeFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rollgrinder-exchange", Guid.NewGuid().ToString("N"));

    public LibraryExchangeFileTests()
    {
        Directory.CreateDirectory(this.directory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(this.directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows 上连接池偶尔晚一拍放手：临时目录留着无妨。
        }
    }

    private static RollProfileDefinition Profile(string id, string name) => RollProfileDefinition.Create(
        id,
        name,
        2000.0,
        CompositeRollProfile.Sequential(0.0, new[]
        {
            new SequentialSegment(
                ProfileTypeKeys.Crown,
                2000.0,
                new CrownProfileType().Schema.CreateDefaults()
                    .With(CrownProfileType.CrownDiameterMicrometerKey, ParameterValue.FromNumber(250.0))),
        }),
        DateTimeOffset.UnixEpoch);

    private static GrindingProgram Program(string id, string name) => GrindingProgram.Create(
        id,
        name,
        new[]
        {
            new GrindingJobStep(1, StepTypeKeys.Rough, new RoughGrindingStepType().Schema.CreateDefaults()),
            new GrindingJobStep(2, StepTypeKeys.Finish, new FinishGrindingStepType().Schema.CreateDefaults()),
        },
        DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task What_is_exported_reads_back_the_same_and_leaves_a_single_file()
    {
        string file = Path.Combine(this.directory, "usb" + LibraryExchangeFile.Extension);

        await LibraryExchangeFile.ExportAsync(
            file,
            new[] { Profile("P-1", "凸度 250"), Profile("P-2", "凸度 250 副本") },
            new[] { Program("G-1", "粗到精") },
            CancellationToken.None);

        Directory.GetFiles(this.directory).Should().Equal(new[] { file }, "WAL 要并回主文件，U 盘上只留这一个文件");

        LibraryExchangeContent content = await LibraryExchangeFile.ReadAsync(file, CancellationToken.None);
        content.Profiles.Select(p => p.Name).Should().BeEquivalentTo("凸度 250", "凸度 250 副本");
        content.Profiles.First(p => p.ProfileId == "P-1").SegmentCount.Should().Be(1);
        content.Programs.Should().ContainSingle().Which.Steps.Should().HaveCount(2);
    }

    [Fact]
    public async Task Exporting_over_an_old_file_replaces_it()
    {
        string file = Path.Combine(this.directory, "again" + LibraryExchangeFile.Extension);
        await LibraryExchangeFile.ExportAsync(file, new[] { Profile("P-1", "旧") }, Array.Empty<GrindingProgram>(), CancellationToken.None);
        await LibraryExchangeFile.ExportAsync(file, new[] { Profile("P-9", "新") }, Array.Empty<GrindingProgram>(), CancellationToken.None);

        LibraryExchangeContent content = await LibraryExchangeFile.ReadAsync(file, CancellationToken.None);
        content.Profiles.Select(p => p.Name).Should().Equal("新");
    }

    [Fact]
    public async Task A_file_that_is_not_an_exchange_file_is_refused_with_a_data_error()
    {
        string junk = Path.Combine(this.directory, "junk" + LibraryExchangeFile.Extension);
        await File.WriteAllTextAsync(junk, "this is not sqlite");

        Func<Task> read = () => LibraryExchangeFile.ReadAsync(junk, CancellationToken.None);
        await read.Should().ThrowAsync<DataStoreException>();

        Func<Task> missing = () => LibraryExchangeFile.ReadAsync(Path.Combine(this.directory, "none.rgxlib"), CancellationToken.None);
        await missing.Should().ThrowAsync<DataStoreException>();
    }

    [Fact]
    public void A_clashing_name_gets_the_next_free_suffix()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "凸度 250", "凸度 250 (2)" };

        LibraryExchangeFile.UniqueName("新辊形", taken).Should().Be("新辊形");
        LibraryExchangeFile.UniqueName("凸度 250", taken).Should().Be("凸度 250 (3)");
    }
}
