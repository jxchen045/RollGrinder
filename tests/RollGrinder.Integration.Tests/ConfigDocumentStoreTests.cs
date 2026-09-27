using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RollGrinder.Composition;
using RollGrinder.Contracts;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 机床配置与标签映射的编辑存取（修改稿 5.8）：不成立不写；写前备份；能取回上一版；
/// 界面不认识的字段原样保留；中文说明写回去还是中文。
/// </summary>
public sealed class ConfigDocumentStoreTests : IDisposable
{
    private readonly TempWorkspace workspace = new();

    private async Task<(ConfigDocumentStore Store, AppOptions Options)> BuildAsync()
    {
        AppOptions options = AppOptions.Parse(new[] { "--gateway", "sim" }, this.workspace.Root);
        await ConfigBootstrapper.EnsureConfigurationAsync(options, this.workspace.CreateSampleDirectory(), CancellationToken.None);
        return (new ConfigDocumentStore(options, TimeProvider.System), options);
    }

    [Fact]
    public async Task The_sample_files_are_valid()
    {
        (ConfigDocumentStore store, _) = await BuildAsync();
        ConfigDocumentStore.Validate(ConfigFileKind.Machine, await store.LoadAsync(ConfigFileKind.Machine, CancellationToken.None))
            .Should().BeEmpty();
        ConfigDocumentStore.Validate(ConfigFileKind.TagMap, await store.LoadAsync(ConfigFileKind.TagMap, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Cross_field_mistakes_are_reported_on_their_field()
    {
        (ConfigDocumentStore store, _) = await BuildAsync();
        JsonObject machine = await store.LoadAsync(ConfigFileKind.Machine, CancellationToken.None);
        machine["workpiece"]!["maxBodyLengthMm"] = 100.0;
        machine["axes"]![0]!["maxPositionMm"] = -10.0;
        machine["thresholds"]!["maxCompensationRadiusMm"] = 0.0;

        ConfigDocumentStore.Validate(ConfigFileKind.Machine, machine).Select(issue => issue.Path).Should().Contain(new[]
        {
            "workpiece.maxBodyLengthMm", "axes[0].maxPositionMm", "thresholds.maxCompensationRadiusMm",
        });
    }

    [Fact]
    public async Task A_missing_required_field_is_a_structural_issue_and_is_not_saved()
    {
        (ConfigDocumentStore store, AppOptions options) = await BuildAsync();
        string before = await File.ReadAllTextAsync(options.MachineConfigFilePath);
        JsonObject machine = await store.LoadAsync(ConfigFileKind.Machine, CancellationToken.None);
        machine.Remove("controller");

        ConfigDocumentStore.Validate(ConfigFileKind.Machine, machine).Should().Contain(issue => issue.ReasonResourceKey == "Cfg_Issue_Structure");
        await store.Invoking(s => s.SaveAsync(ConfigFileKind.Machine, machine, CancellationToken.None))
            .Should().ThrowAsync<GatewayException>();
        (await File.ReadAllTextAsync(options.MachineConfigFilePath)).Should().Be(before);
    }

    [Fact]
    public async Task Saving_backs_up_the_old_file_keeps_unknown_fields_and_can_be_undone()
    {
        (ConfigDocumentStore store, AppOptions options) = await BuildAsync();
        JsonObject machine = await store.LoadAsync(ConfigFileKind.Machine, CancellationToken.None);
        machine["siteNote"] = "界面不认识的字段";
        machine["workpiece"]!["maxWeightKg"] = 40000.0;

        string backup = await store.SaveAsync(ConfigFileKind.Machine, machine, CancellationToken.None);

        File.Exists(backup).Should().BeTrue();
        JsonObject saved = await store.LoadAsync(ConfigFileKind.Machine, CancellationToken.None);
        saved["siteNote"]!.GetValue<string>().Should().Be("界面不认识的字段");
        (await File.ReadAllTextAsync(options.MachineConfigFilePath)).Should().Contain("界面不认识的字段", "中文不转义");

        JsonObject? previous = await store.LoadLatestBackupAsync(ConfigFileKind.Machine, CancellationToken.None);
        previous!["workpiece"]!["maxWeightKg"]!.GetValue<double>().Should().Be(42000.0);
        var diffs = ConfigDocumentStore.Diff(previous, saved);
        diffs.Select(diff => diff.Path).Should().BeEquivalentTo(new[] { "siteNote", "workpiece.maxWeightKg" });
        diffs.Single(diff => diff.Path == "siteNote").Should().Be(new ConfigDiff("siteNote", null, "\"界面不认识的字段\""));
    }

    [Fact]
    public async Task Tag_map_rows_are_diffed_by_key_and_duplicates_or_empty_addresses_are_refused()
    {
        (ConfigDocumentStore store, _) = await BuildAsync();
        JsonObject before = await store.LoadAsync(ConfigFileKind.TagMap, CancellationToken.None);
        JsonObject after = (JsonObject)before.DeepClone();
        JsonArray tags = (JsonArray)after["tags"]!;
        tags.Insert(0, new JsonObject { ["key"] = "status.test", ["address"] = "ns=3;s=x", ["dataType"] = "Boolean", ["access"] = "Read" });

        ConfigDocumentStore.Diff(before, after).Should().OnlyContain(diff => diff.Path.StartsWith("tags[status.test]", StringComparison.Ordinal),
            "插一行不能让后面每一行都算改动");

        tags.Add(new JsonObject { ["key"] = "status.test", ["address"] = "", ["dataType"] = "Boolean", ["access"] = "Read" });
        ConfigDocumentStore.Validate(ConfigFileKind.TagMap, after).Select(issue => issue.ReasonResourceKey)
            .Should().Contain(new[] { "Cfg_Issue_Duplicate", "Cfg_Issue_Required" });
    }

    public void Dispose() => this.workspace.Dispose();
}
