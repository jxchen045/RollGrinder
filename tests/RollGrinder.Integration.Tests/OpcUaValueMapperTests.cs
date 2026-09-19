using System;
using FluentAssertions;
using Opc.Ua;
using RollGrinder.Contracts;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Device;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// OPC UA 取值换算：物理量 = 原始值 × Scale，写入做逆运算。
/// 这些用例不需要机床，专盯换算与错误处理。
/// </summary>
public sealed class OpcUaValueMapperTests
{
    private static readonly DateTimeOffset Fallback = DateTimeOffset.UnixEpoch;

    private static TagDescriptor Tag(
        TagDataType dataType,
        double scale = 1.0,
        string address = "ns=2;s=/Channel/Parameter/R[100]") =>
        new("job.rollRadiusMm", address, dataType, TagAccess.ReadWrite, Scale: scale);

    [Fact]
    public void Node_ids_come_from_the_tag_map()
    {
        NodeId nodeId = OpcUaValueMapper.ParseNodeId(Tag(TagDataType.Double));

        nodeId.NamespaceIndex.Should().Be(2);
        nodeId.Identifier.Should().Be("/Channel/Parameter/R[100]");
    }

    [Theory]
    [InlineData("not a node id")]
    [InlineData("")]
    [InlineData("ns=2;")]
    [InlineData("/Channel/Parameter/R[100]")]
    public void An_unusable_node_id_is_a_gateway_error(string address)
    {
        FluentActions.Invoking(() => OpcUaValueMapper.ParseNodeId(Tag(TagDataType.Double, address: address)))
            .Should().Throw<GatewayException>();
    }

    [Theory]
    [InlineData("ns=2;s=/Channel/Parameter/R[100]")]
    [InlineData("i=2258")]
    [InlineData("ns=3;i=1001")]
    [InlineData("ns=4;g=09087e75-8e5e-499b-954f-f2a9603db28a")]
    public void Well_formed_node_ids_are_accepted(string address)
    {
        FluentActions.Invoking(() => OpcUaValueMapper.ParseNodeId(Tag(TagDataType.Double, address: address)))
            .Should().NotThrow();
    }

    [Fact]
    public void Reading_applies_the_configured_scale()
    {
        TagValue value = OpcUaValueMapper.ToTagValue(
            Tag(TagDataType.Double, scale: 0.001),
            new DataValue(new Variant(325000.0)) { StatusCode = StatusCodes.Good },
            Fallback);

        value.IsGood.Should().BeTrue();
        value.Raw.Should().Be(325.0);
    }

    [Fact]
    public void Writing_undoes_the_scale()
    {
        object raw = OpcUaValueMapper.ToOpcValue(
            Tag(TagDataType.Double, scale: 0.001),
            new TagValue("job.rollRadiusMm", TagDataType.Double, 325.0, Fallback));

        raw.Should().Be(325000.0);
    }

    [Fact]
    public void A_bad_status_code_marks_the_value_as_untrustworthy()
    {
        TagValue value = OpcUaValueMapper.ToTagValue(
            Tag(TagDataType.Double),
            new DataValue(new Variant(1.0)) { StatusCode = StatusCodes.BadDeviceFailure },
            Fallback);

        value.IsGood.Should().BeFalse();
        value.Raw.Should().BeNull();
    }

    [Fact]
    public void A_missing_result_is_untrustworthy_rather_than_zero()
    {
        TagValue value = OpcUaValueMapper.ToTagValue(Tag(TagDataType.Double), null, Fallback);

        value.IsGood.Should().BeFalse();
        value.Raw.Should().BeNull();
        value.SampledAtUtc.Should().Be(Fallback);
    }

    [Fact]
    public void The_source_timestamp_is_kept_when_the_server_supplies_one()
    {
        var sourceTimestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        TagValue value = OpcUaValueMapper.ToTagValue(
            Tag(TagDataType.Double),
            new DataValue(new Variant(1.0)) { StatusCode = StatusCodes.Good, SourceTimestamp = sourceTimestamp },
            Fallback);

        value.SampledAtUtc.Should().Be(new DateTimeOffset(sourceTimestamp));
    }

    [Fact]
    public void Integers_round_rather_than_truncate()
    {
        TagValue value = OpcUaValueMapper.ToTagValue(
            Tag(TagDataType.Int32),
            new DataValue(new Variant(2.6)) { StatusCode = StatusCodes.Good },
            Fallback);

        value.Raw.Should().Be(3);
    }

    [Fact]
    public void Booleans_and_strings_round_trip()
    {
        OpcUaValueMapper.ToTagValue(
                Tag(TagDataType.Boolean),
                new DataValue(new Variant(true)) { StatusCode = StatusCodes.Good },
                Fallback)
            .Raw.Should().Be(true);

        OpcUaValueMapper.ToOpcValue(
                Tag(TagDataType.String),
                new TagValue("machine.programName", TagDataType.String, "SIM.MPF", Fallback))
            .Should().Be("SIM.MPF");
    }

    [Fact]
    public void A_type_mismatch_on_read_is_reported_as_untrustworthy()
    {
        TagValue value = OpcUaValueMapper.ToTagValue(
            Tag(TagDataType.Double),
            new DataValue(new Variant("not a number")) { StatusCode = StatusCodes.Good },
            Fallback);

        value.IsGood.Should().BeFalse();
    }

    [Fact]
    public void A_type_mismatch_on_write_is_refused_loudly()
    {
        FluentActions.Invoking(() => OpcUaValueMapper.ToOpcValue(
                Tag(TagDataType.Double),
                new TagValue("job.rollRadiusMm", TagDataType.Double, "not a number", Fallback)))
            .Should().Throw<GatewayException>();
    }

    [Fact]
    public void Writing_null_is_refused()
    {
        FluentActions.Invoking(() => OpcUaValueMapper.ToOpcValue(
                Tag(TagDataType.Double),
                new TagValue("job.rollRadiusMm", TagDataType.Double, null, Fallback)))
            .Should().Throw<GatewayException>();
    }

    [Fact]
    public void A_zero_scale_is_refused_instead_of_dividing_by_zero()
    {
        FluentActions.Invoking(() => OpcUaValueMapper.ToOpcValue(
                Tag(TagDataType.Double, scale: 0.0),
                new TagValue("job.rollRadiusMm", TagDataType.Double, 1.0, Fallback)))
            .Should().Throw<GatewayException>();
    }
}
