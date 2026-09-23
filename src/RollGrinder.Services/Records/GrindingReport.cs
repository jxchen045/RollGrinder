using System;
using System.Collections.Generic;

namespace RollGrinder.Services.Records;

/// <summary>报表的种类。</summary>
public enum ReportKind
{
    /// <summary>
    /// 磨前：这支辊准备按什么磨。交班、工艺确认时打一张贴在机床上。
    /// </summary>
    PreGrind = 0,

    /// <summary>
    /// 磨后：这支辊实际磨成了什么样。随辊交付，质量追溯用。
    /// </summary>
    PostGrind = 1,
}

/// <summary>报表里的一行：一个量与它的值。</summary>
/// <param name="LabelResourceKey">名目的资源键；界面层按当前语言取字。</param>
/// <param name="Value">值，已经按显示单位格式化好。</param>
public sealed record ReportField(string LabelResourceKey, string Value, bool ValueIsResourceKey = false);

/// <summary>报表里的一张表。</summary>
/// <param name="TitleResourceKey">表头的资源键。</param>
/// <param name="ColumnHeaderResourceKeys">列头的资源键；为空表示这是"名目 + 值"的两列表。</param>
/// <param name="Rows">各行；每行的单元格数与列头数一致。</param>
public sealed record ReportTable(
    string TitleResourceKey,
    IReadOnlyList<string> ColumnHeaderResourceKeys,
    IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// 一张报表的全部内容。
///
/// **这里没有排版。** 纸上怎么摆、字多大、页眉页脚，都是界面层的事；
/// 这里只负责"报表上有哪些数、数是多少"——所以它可以被测试直接核对，
/// 也能换个渲染方式（预览、打印、以后导出 PDF）而内容不变。
///
/// 字面全是资源键，不是中文：报表跟着界面语言走（架构约束 ⑪）。
/// </summary>
/// <param name="Kind">磨前还是磨后。</param>
/// <param name="TitleResourceKey">报表标题的资源键。</param>
/// <param name="GeneratedAtUtc">出表时刻。</param>
/// <param name="Header">表头字段（辊号、作业号、时间……）。</param>
/// <param name="Tables">正文里的各张表。</param>
/// <param name="CurvePoints">
/// 要画在报表上的曲线（辊身坐标 mm，直径量 µm）。磨前是目标辊形，磨后是实测偏差；
/// 没有可画的就是空表，界面照实留白，不画一条编出来的线。
/// </param>
/// <param name="CurveTitleResourceKey">曲线标题的资源键。</param>
public sealed record GrindingReport(
    ReportKind Kind,
    string TitleResourceKey,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ReportField> Header,
    IReadOnlyList<ReportTable> Tables,
    IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> CurvePoints,
    string CurveTitleResourceKey);
