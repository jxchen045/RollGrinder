using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using RollGrinder.Services.Alarms;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 报警号段守卫。MGK84160 操作说明书把机床的号段划死了：NC 报警 &lt; 500000，
/// PLC 报警 ≥ 500000，其中严重故障 &lt; 700040、一般故障 ≥ 700040。
/// 上位机的号段必须整段落在机床的一般故障号段之上，否则现场按号查手册
/// 查到的是另一本手册上的故障——这正是 720000 段当初踩的坑。
/// </summary>
public sealed class AlarmCodeRangeTests
{
    private static int[] RegisteredCodes() => typeof(AlarmCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(int))
        .Where(field => field.Name is not (nameof(AlarmCodes.Unspecified)
            or nameof(AlarmCodes.RangeStart)
            or nameof(AlarmCodes.RangeEnd)
            or nameof(AlarmCodes.MachineGeneralFaultRangeStart)))
        .Select(field => (int)field.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void The_hmi_range_sits_clear_of_the_machines_own_alarm_ranges()
    {
        AlarmCodes.RangeStart.Should().BeGreaterThan(
            AlarmCodes.MachineGeneralFaultRangeStart,
            "号段与机床的一般故障报警重叠，现场按号查到的会是机床自己的条目");
        AlarmCodes.RangeEnd.Should().BeGreaterThan(AlarmCodes.RangeStart);
    }

    [Fact]
    public void Every_registered_code_falls_inside_the_range()
    {
        int[] codes = RegisteredCodes();

        codes.Should().NotBeEmpty();
        foreach (int code in codes)
        {
            AlarmCodes.IsHmiCode(code).Should().BeTrue($"报警号 {code} 落在 {AlarmCodes.RangeStart} 号段之外");
        }
    }

    [Fact]
    public void Registered_codes_are_unique()
    {
        int[] codes = RegisteredCodes();

        codes.Should().OnlyHaveUniqueItems("两条报警共用一个号，现场按号查就分不出是哪一条");
    }

    [Fact]
    public void Codes_from_the_machines_ranges_are_not_mistaken_for_ours()
    {
        foreach (int machineCode in new[] { 12345, 510000, 700039, 700040, 720000, 720999 })
        {
            AlarmCodes.IsHmiCode(machineCode).Should().BeFalse($"{machineCode} 是机床侧的号");
        }
    }
}
