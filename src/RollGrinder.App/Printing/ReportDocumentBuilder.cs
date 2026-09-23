using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using RollGrinder.App.Localization;
using RollGrinder.Services.Records;

namespace RollGrinder.App.Printing;

/// <summary>
/// 把一张报表排到纸上。
///
/// 内容来自 <see cref="GrindingReport"/>，这里只管排版：页边距、字号、
/// 表格线、曲线怎么画。报表里的字面全是资源键，取字在这里做——
/// 于是同一份内容能按当前界面语言打出中文或英文两张（架构约束 ⑪）。
/// </summary>
public static class ReportDocumentBuilder
{
    /// <summary>A4 纵向，按 96 dpi 折算（210 × 297 mm）。</summary>
    private const double PageWidth = 793.7;
    private const double PageHeight = 1122.5;
    private const double Margin = 56.0;

    /// <summary>曲线图在纸上的大小。</summary>
    private const double CurveWidth = PageWidth - (Margin * 2.0);
    private const double CurveHeight = 200.0;

    public static FlowDocument Build(GrindingReport report, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);

        var document = new FlowDocument
        {
            PageWidth = PageWidth,
            PageHeight = PageHeight,
            PagePadding = new Thickness(Margin),
            ColumnWidth = PageWidth,
            FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
            FontSize = 11.0,
            Background = Brushes.White,
            Foreground = Brushes.Black,
        };

        document.Blocks.Add(Title(localizer[report.TitleResourceKey]));
        document.Blocks.Add(Subtitle(localizer.Format(
            "Report_GeneratedAtFormat",
            report.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))));

        document.Blocks.Add(HeaderTable(report.Header, localizer));

        foreach (ReportTable table in report.Tables)
        {
            document.Blocks.Add(Title(localizer[table.TitleResourceKey], size: 13.0));
            document.Blocks.Add(BodyTable(table, localizer));
        }

        if (report.CurvePoints.Count >= 2)
        {
            document.Blocks.Add(Title(localizer[report.CurveTitleResourceKey], size: 13.0));
            document.Blocks.Add(new BlockUIContainer(Curve(report.CurvePoints)));
        }

        return document;
    }

    private static Paragraph Title(string text, double size = 18.0) => new(new Run(text))
    {
        FontSize = size,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0.0, size == 18.0 ? 0.0 : 14.0, 0.0, 6.0),
    };

    private static Paragraph Subtitle(string text) => new(new Run(text))
    {
        FontSize = 10.0,
        Foreground = Brushes.DimGray,
        Margin = new Thickness(0.0, 0.0, 0.0, 12.0),
    };

    /// <summary>表头字段排成两列（名目 + 值）的双栏，省纸也好读。</summary>
    private static Table HeaderTable(IReadOnlyList<ReportField> fields, IStringLocalizer localizer)
    {
        Table table = NewTable(4);
        var group = new TableRowGroup();

        for (int i = 0; i < fields.Count; i += 2)
        {
            var row = new TableRow();
            AppendField(row, fields[i], localizer);

            if (i + 1 < fields.Count)
            {
                AppendField(row, fields[i + 1], localizer);
            }
            else
            {
                row.Cells.Add(Cell(string.Empty));
                row.Cells.Add(Cell(string.Empty));
            }

            group.Rows.Add(row);
        }

        table.RowGroups.Add(group);
        return table;
    }

    private static void AppendField(TableRow row, ReportField field, IStringLocalizer localizer)
    {
        row.Cells.Add(Cell(localizer[field.LabelResourceKey], bold: true));
        row.Cells.Add(Cell(field.ValueIsResourceKey ? localizer[field.Value] : field.Value));
    }

    /// <summary>
    /// 正文表。列头为空的表是"名目 + 值"两列（结果指标那种），
    /// 这时第一格仍然是资源键，得取字。
    /// </summary>
    private static Table BodyTable(ReportTable source, IStringLocalizer localizer)
    {
        bool isLabelValue = source.ColumnHeaderResourceKeys.Count == 0;
        int columns = isLabelValue ? 2 : source.ColumnHeaderResourceKeys.Count;

        Table table = NewTable(columns);
        var group = new TableRowGroup();

        if (!isLabelValue)
        {
            var header = new TableRow { Background = Brushes.WhiteSmoke };
            foreach (string key in source.ColumnHeaderResourceKeys)
            {
                header.Cells.Add(Cell(localizer[key], bold: true));
            }

            group.Rows.Add(header);
        }

        foreach (IReadOnlyList<string> cells in source.Rows)
        {
            var row = new TableRow();
            for (int i = 0; i < cells.Count; i++)
            {
                // 约定：名目列放资源键，值列放已经格式化好的字面。
                bool isLabelColumn = isLabelValue ? i == 0 : i == 1;
                row.Cells.Add(Cell(isLabelColumn ? Localize(localizer, cells[i]) : cells[i]));
            }

            group.Rows.Add(row);
        }

        table.RowGroups.Add(group);
        return table;
    }

    /// <summary>取不到字就照原样打：报表上宁可出现一个键，也不要空一格让人以为漏了数。</summary>
    private static string Localize(IStringLocalizer localizer, string key)
    {
        string text = localizer[key];
        return text.StartsWith('!') ? key : text;
    }

    private static Table NewTable(int columns)
    {
        var table = new Table
        {
            CellSpacing = 0.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
        };

        for (int i = 0; i < columns; i++)
        {
            table.Columns.Add(new TableColumn());
        }

        return table;
    }

    private static TableCell Cell(string text, bool bold = false) =>
        new(new Paragraph(new Run(text)) { Margin = new Thickness(0.0), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal })
        {
            Padding = new Thickness(5.0, 3.0, 5.0, 3.0),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0.5),
        };

    /// <summary>
    /// 曲线画成一张矢量图：打印出来是线条而不是一张糊的位图。
    ///
    /// 纵轴按数据自身的范围拉满并对称到零，让零线落在正中——
    /// 报表上的曲线是给人看趋势的，不是量尺寸的。
    /// </summary>
    private static FrameworkElement Curve(IReadOnlyList<(double BodyPositionMm, double DiameterMicrometer)> points)
    {
        double minX = points.Min(point => point.BodyPositionMm);
        double maxX = points.Max(point => point.BodyPositionMm);
        double span = points.Max(point => Math.Abs(point.DiameterMicrometer));
        if (span <= 0.0)
        {
            span = 1.0;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            bool first = true;
            foreach ((double positionMm, double micrometer) in points)
            {
                double x = maxX > minX ? (positionMm - minX) / (maxX - minX) * CurveWidth : 0.0;
                double y = (CurveHeight / 2.0) - (micrometer / span * (CurveHeight / 2.0));

                if (first)
                {
                    context.BeginFigure(new Point(x, y), isFilled: false, isClosed: false);
                    first = false;
                }
                else
                {
                    context.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
                }
            }
        }

        geometry.Freeze();

        var canvas = new System.Windows.Controls.Canvas
        {
            Width = CurveWidth,
            Height = CurveHeight,
            Background = Brushes.White,
        };

        canvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 0.0,
            Y1 = CurveHeight / 2.0,
            X2 = CurveWidth,
            Y2 = CurveHeight / 2.0,
            Stroke = Brushes.Gray,
            StrokeThickness = 0.5,
            StrokeDashArray = new DoubleCollection { 3.0, 3.0 },
        });

        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stroke = Brushes.Black,
            StrokeThickness = 1.2,
        });

        return new System.Windows.Controls.Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0.5),
            Child = canvas,
        };
    }
}
