using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RollGrinder.App.Controls;

/// <summary>
/// 工序简图（修改稿原则 2"图形辅助输入"、5.3）：每种工序一张示意图，光标所在参数对应的量在图上加粗、换强调色。
///
/// 只是示意，不按比例：画的是"这个参数指的是哪个量"，不是这支辊的真实尺寸。
/// 图上只标工程符号（ae、f、n、vc……），说明、单位、范围在图下面那一行。
/// 画法写在代码里而不是图片里：颜色跟着主题走，新加一种量只改这一处。
/// </summary>
public sealed class StepDiagram : FrameworkElement
{
    public static readonly DependencyProperty StepTypeKeyProperty = DependencyProperty.Register(
        nameof(StepTypeKey), typeof(string), typeof(StepDiagram),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightedParameterKeyProperty = DependencyProperty.Register(
        nameof(HighlightedParameterKey), typeof(string), typeof(StepDiagram),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>设计坐标系：先在 520×260 里画，再等比缩放到控件大小。</summary>
    private const double DesignWidth = 520.0;
    private const double DesignHeight = 260.0;

    private DiagramElement? highlighted;
    private Pen normalPen = new(Brushes.Gray, 2.0);
    private Pen accentPen = new(Brushes.Blue, 4.0);
    private Pen thinPen = new(Brushes.Gray, 1.0);
    private Brush textBrush = Brushes.Black;
    private Brush accentBrush = Brushes.Blue;
    private Brush fillBrush = Brushes.LightGray;
    private Brush wheelBrush = Brushes.Gainsboro;
    private Typeface typeface = new("Consolas");
    private double pixelsPerDip = 1.0;

    public string StepTypeKey
    {
        get => (string)GetValue(StepTypeKeyProperty);
        set => SetValue(StepTypeKeyProperty, value);
    }

    public string? HighlightedParameterKey
    {
        get => (string?)GetValue(HighlightedParameterKeyProperty);
        set => SetValue(HighlightedParameterKeyProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 宽、高两头都得守住：只按宽算的话，外面给了固定高度时排出来比格子高，下半截被裁掉。
        double scale = Math.Min(
            1.0,
            Math.Min(
                double.IsInfinity(availableSize.Width) ? 1.0 : availableSize.Width / DesignWidth,
                double.IsInfinity(availableSize.Height) ? 1.0 : availableSize.Height / DesignHeight));
        return new Size(DesignWidth * scale, DesignHeight * scale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        LoadTheme();
        this.highlighted = HighlightedParameterKey is { Length: > 0 } key ? StepDiagramMap.ElementOf(key) : null;

        double scale = Math.Min(ActualWidth / DesignWidth, ActualHeight / DesignHeight);
        if (scale <= 0.0)
        {
            return;
        }

        dc.PushTransform(new TranslateTransform((ActualWidth - (DesignWidth * scale)) / 2.0, (ActualHeight - (DesignHeight * scale)) / 2.0));
        dc.PushTransform(new ScaleTransform(scale, scale));

        switch (StepDiagramMap.KindOf(StepTypeKey ?? string.Empty))
        {
            case StepDiagramKind.Traverse:
                DrawTraverse(dc);
                break;
            case StepDiagramKind.Measure:
                DrawMeasure(dc);
                break;
            case StepDiagramKind.Roundness:
                DrawRoundness(dc);
                break;
            case StepDiagramKind.Chamfer:
                DrawChamfer(dc);
                break;
            case StepDiagramKind.WheelDress:
                DrawWheelDress(dc);
                break;
            case StepDiagramKind.EddyCurrent:
                DrawEddyCurrent(dc);
                break;
            case StepDiagramKind.Pause:
                DrawPause(dc);
                break;
            case StepDiagramKind.Auxiliary:
                DrawAuxiliary(dc);
                break;
            default:
                DrawMarker(dc);
                break;
        }

        dc.Pop();
        dc.Pop();
    }

    private void LoadTheme()
    {
        Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        Brush line = Find("Brush.TextSecondary", Brushes.Gray);
        this.accentBrush = Find("Brush.CurrentStep", Brushes.OrangeRed);
        this.textBrush = Find("Brush.TextPrimary", Brushes.Black);
        this.fillBrush = Find("Brush.HeaderFill", Brushes.LightGray);
        this.wheelBrush = Find("Brush.GridLine", Brushes.Gainsboro);
        this.normalPen = new Pen(line, 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        this.accentPen = new Pen(this.accentBrush, 4.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        this.thinPen = new Pen(line, 1.0) { DashStyle = DashStyles.Dash };
        FontFamily family = TryFindResource("Font.Mono") as FontFamily ?? new FontFamily("Consolas");
        this.typeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        this.pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
    }

    // ── 基本件 ────────────────────────────────────────────────────────────────

    private bool IsLit(DiagramElement element) => this.highlighted == element;

    private Pen PenFor(DiagramElement element) => IsLit(element) ? this.accentPen : this.normalPen;

    /// <summary>一个量的符号。亮起时加大、换强调色。</summary>
    private void Label(DrawingContext dc, DiagramElement element, double x, double y)
    {
        bool lit = IsLit(element);
        var text = new FormattedText(
            StepDiagramMap.SymbolOf(element),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            this.typeface,
            lit ? 22.0 : 17.0,
            lit ? this.accentBrush : this.textBrush,
            this.pixelsPerDip);
        dc.DrawText(text, new Point(x - (text.Width / 2.0), y - (text.Height / 2.0)));
    }

    private static void Arrow(DrawingContext dc, Pen pen, Point from, Point to)
    {
        dc.DrawLine(pen, from, to);
        Vector direction = to - from;
        if (direction.Length < 1.0)
        {
            return;
        }

        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        dc.DrawLine(pen, to, to - (direction * 10.0) + (normal * 5.0));
        dc.DrawLine(pen, to, to - (direction * 10.0) - (normal * 5.0));
    }

    private static void DoubleArrow(DrawingContext dc, Pen pen, Point a, Point b)
    {
        Arrow(dc, pen, a, b);
        Arrow(dc, pen, b, a);
    }

    /// <summary>绕一个中心的旋转箭头（大半圈）。</summary>
    private static void Rotation(DrawingContext dc, Pen pen, Point center, double radius)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            Point start = new(center.X + radius, center.Y);
            Point end = new(center.X, center.Y - radius);
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0.0, true, SweepDirection.Clockwise, true, true);
        }

        dc.DrawGeometry(null, pen, geometry);
        Point tip = new(center.X, center.Y - radius);
        dc.DrawLine(pen, tip, tip + new Vector(9.0, -5.0));
        dc.DrawLine(pen, tip, tip + new Vector(9.0, 5.0));
    }

    /// <summary>轧辊侧视：辊身加两端辊颈。</summary>
    private void Roll(DrawingContext dc, Rect body)
    {
        dc.DrawRectangle(this.fillBrush, this.normalPen, body);
        double neck = body.Height * 0.45;
        double top = body.Top + ((body.Height - neck) / 2.0);
        dc.DrawRectangle(this.fillBrush, this.normalPen, new Rect(body.Left - 34.0, top, 34.0, neck));
        dc.DrawRectangle(this.fillBrush, this.normalPen, new Rect(body.Right, top, 34.0, neck));
    }

    private void Wheel(DrawingContext dc, Point center, double radius) =>
        dc.DrawEllipse(this.wheelBrush, this.normalPen, center, radius, radius);

    // ── 各张图 ────────────────────────────────────────────────────────────────

    /// <summary>往复磨削：砂轮在辊身上方沿辊身往复（f），换向时进给（ae），走 i 道、光磨 i0 道。</summary>
    private void DrawTraverse(DrawingContext dc)
    {
        var body = new Rect(90, 165, 340, 55);
        Roll(dc, body);
        Rotation(dc, PenFor(DiagramElement.WorkpieceSpeed), new Point(52, 192), 22);
        Label(dc, DiagramElement.WorkpieceSpeed, 22, 232);

        // 转速变化：辊的转速上叠一条波形。
        var wave = new StreamGeometry();
        using (StreamGeometryContext context = wave.Open())
        {
            context.BeginFigure(new Point(12, 150), false, false);
            for (int i = 1; i <= 8; i++)
            {
                context.LineTo(new Point(12 + (i * 8), 150 + (i % 2 == 0 ? 0 : -10)), true, false);
            }
        }

        dc.DrawGeometry(null, PenFor(DiagramElement.SpeedVariation), wave);
        Label(dc, DiagramElement.SpeedVariation, 40, 124);

        var wheelCenter = new Point(200, 110);
        Wheel(dc, wheelCenter, 50);
        Rotation(dc, PenFor(DiagramElement.WheelSurfaceSpeed), wheelCenter, 30);
        Label(dc, DiagramElement.WheelSurfaceSpeed, 200, 110);

        DoubleArrow(dc, PenFor(DiagramElement.Feed), new Point(110, 30), new Point(410, 30));
        Label(dc, DiagramElement.Feed, 260, 16);

        // 往复轨迹：每道走一个来回，换向处进给一点。
        Pen passes = PenFor(DiagramElement.Passes);
        dc.DrawLine(passes, new Point(110, 146), new Point(410, 146));
        dc.DrawLine(passes, new Point(410, 152), new Point(110, 152));
        dc.DrawLine(passes, new Point(110, 158), new Point(410, 158));
        Label(dc, DiagramElement.Passes, 470, 138);

        // 光磨：不进给的几道，虚线画在最后一道上。
        Pen sparkOut = IsLit(DiagramElement.SparkOutPasses) ? this.accentPen : this.thinPen;
        dc.DrawLine(sparkOut, new Point(110, 162), new Point(410, 162));
        Label(dc, DiagramElement.SparkOutPasses, 470, 162);

        Arrow(dc, PenFor(DiagramElement.InfeedPerPass), new Point(420, 132), new Point(420, 160));
        Label(dc, DiagramElement.InfeedPerPass, 448, 110);

        Arrow(dc, PenFor(DiagramElement.ContinuousInfeed), new Point(96, 132), new Point(122, 160));
        Label(dc, DiagramElement.ContinuousInfeed, 88, 108);

        // 换向停留：换向点上一个小钟。
        dc.DrawEllipse(null, PenFor(DiagramElement.ReversalDwell), new Point(440, 80), 12, 12);
        dc.DrawLine(PenFor(DiagramElement.ReversalDwell), new Point(440, 80), new Point(440, 71));
        dc.DrawLine(PenFor(DiagramElement.ReversalDwell), new Point(440, 80), new Point(447, 80));
        Label(dc, DiagramElement.ReversalDwell, 470, 80);

        // 余量：辊身右端，现表面到目标表面的直径差。
        Pen stock = PenFor(DiagramElement.Stock);
        dc.DrawLine(this.thinPen, new Point(340, 170), new Point(430, 170));
        DoubleArrow(dc, stock, new Point(385, 165), new Point(385, 182));
        Label(dc, DiagramElement.Stock, 360, 196);

        // 在线测量：辊身下方的测头。
        Pen gauge = PenFor(DiagramElement.InProcessMeasurement);
        dc.DrawLine(gauge, new Point(260, 220), new Point(260, 246));
        dc.DrawRectangle(null, gauge, new Rect(248, 246, 24, 10));
        Label(dc, DiagramElement.InProcessMeasurement, 292, 246);
    }

    /// <summary>测量：测量臂沿辊身走（f），取 k 个点；辊在转（n）。</summary>
    private void DrawMeasure(DrawingContext dc)
    {
        var body = new Rect(90, 130, 340, 70);
        Roll(dc, body);
        Rotation(dc, PenFor(DiagramElement.WorkpieceSpeed), new Point(52, 165), 22);
        Label(dc, DiagramElement.WorkpieceSpeed, 22, 208);

        Pen points = PenFor(DiagramElement.MeasurePoints);
        for (int i = 0; i < 7; i++)
        {
            double x = 110 + (i * 50);
            dc.DrawEllipse(IsLit(DiagramElement.MeasurePoints) ? this.accentBrush : this.textBrush, null, new Point(x, 124), 4, 4);
        }

        dc.DrawLine(points, new Point(110, 112), new Point(410, 112));
        Label(dc, DiagramElement.MeasurePoints, 440, 112);

        // 测量臂：从上方伸下来的测头。
        dc.DrawLine(this.normalPen, new Point(260, 40), new Point(260, 108));
        dc.DrawRectangle(this.wheelBrush, this.normalPen, new Rect(240, 26, 40, 16));

        DoubleArrow(dc, PenFor(DiagramElement.Feed), new Point(110, 72), new Point(410, 72));
        Label(dc, DiagramElement.Feed, 330, 58);
    }

    /// <summary>圆度：辊身上取 s 个截面，每个截面每转取 p 个点。</summary>
    private void DrawRoundness(DrawingContext dc)
    {
        var body = new Rect(60, 110, 240, 70);
        Roll(dc, body);
        Rotation(dc, PenFor(DiagramElement.WorkpieceSpeed), new Point(24, 145), 18);
        Label(dc, DiagramElement.WorkpieceSpeed, 24, 190);

        Pen sections = PenFor(DiagramElement.RoundnessSections);
        foreach (double x in new[] { 100.0, 180.0, 260.0 })
        {
            dc.DrawLine(sections, new Point(x, 96), new Point(x, 194));
        }

        Label(dc, DiagramElement.RoundnessSections, 180, 214);

        // 截面放大：一圈上的采样点。
        var center = new Point(420, 140);
        dc.DrawEllipse(this.fillBrush, this.normalPen, center, 70, 70);
        Brush dot = IsLit(DiagramElement.RoundnessPoints) ? this.accentBrush : this.textBrush;
        for (int i = 0; i < 16; i++)
        {
            double angle = i * Math.PI * 2.0 / 16.0;
            dc.DrawEllipse(dot, null, new Point(center.X + (70 * Math.Cos(angle)), center.Y + (70 * Math.Sin(angle))), 4, 4);
        }

        Label(dc, DiagramElement.RoundnessPoints, 420, 140);
        dc.DrawLine(this.thinPen, new Point(260, 110), new Point(352, 110));
    }

    /// <summary>倒角：辊身右端先 L1×H1、再 L2×H2；斜坡或圆弧（R）。</summary>
    private void DrawChamfer(DrawingContext dc)
    {
        // 辊身上沿（放大的端部）：从左往右到端面。
        var p0 = new Point(40, 90);
        var p1 = new Point(300, 90);
        var p2 = new Point(390, 120);
        var p3 = new Point(450, 170);
        dc.DrawLine(this.normalPen, p0, p1);
        dc.DrawLine(this.normalPen, p3, new Point(450, 230));
        dc.DrawLine(this.normalPen, new Point(40, 230), new Point(450, 230));

        Pen profile = PenFor(DiagramElement.ChamferKind);
        var curve = new StreamGeometry();
        using (StreamGeometryContext context = curve.Open())
        {
            context.BeginFigure(p1, false, false);
            context.QuadraticBezierTo(new Point(350, 92), p2, true, true);
            context.QuadraticBezierTo(new Point(430, 138), p3, true, true);
        }

        dc.DrawGeometry(null, profile, curve);
        Label(dc, DiagramElement.ChamferKind, 350, 150);

        DoubleArrow(dc, PenFor(DiagramElement.ChamferLength1), new Point(300, 64), new Point(390, 64));
        Label(dc, DiagramElement.ChamferLength1, 345, 48);
        DoubleArrow(dc, PenFor(DiagramElement.ChamferLength2), new Point(390, 64), new Point(450, 64));
        Label(dc, DiagramElement.ChamferLength2, 420, 48);
        DoubleArrow(dc, PenFor(DiagramElement.ChamferHeight1), new Point(475, 90), new Point(475, 120));
        Label(dc, DiagramElement.ChamferHeight1, 500, 105);
        DoubleArrow(dc, PenFor(DiagramElement.ChamferHeight2), new Point(475, 120), new Point(475, 170));
        Label(dc, DiagramElement.ChamferHeight2, 500, 145);
        dc.DrawLine(this.thinPen, new Point(390, 120), new Point(480, 120));
        dc.DrawLine(this.thinPen, new Point(450, 90), new Point(480, 90));
        dc.DrawLine(this.thinPen, new Point(450, 170), new Point(480, 170));

        var wheelCenter = new Point(150, 50);
        Wheel(dc, wheelCenter, 38);
        Rotation(dc, PenFor(DiagramElement.WheelSurfaceSpeed), wheelCenter, 22);
        Label(dc, DiagramElement.WheelSurfaceSpeed, 150, 50);
        Arrow(dc, PenFor(DiagramElement.Feed), new Point(200, 22), new Point(280, 22));
        Label(dc, DiagramElement.Feed, 240, 10);
        Label(dc, DiagramElement.Passes, 240, 110);
        dc.DrawLine(PenFor(DiagramElement.Passes), new Point(200, 100), new Point(280, 100));
        Rotation(dc, PenFor(DiagramElement.WorkpieceSpeed), new Point(80, 170), 20);
        Label(dc, DiagramElement.WorkpieceSpeed, 80, 208);
    }

    /// <summary>砂轮修整：金刚笔横走砂轮宽度（fd），每道切深 ad，走 id 道。</summary>
    private void DrawWheelDress(DrawingContext dc)
    {
        // 砂轮正视：宽 × 径。
        var wheel = new Rect(150, 40, 170, 180);
        dc.DrawRoundedRectangle(this.wheelBrush, this.normalPen, wheel, 6, 6);
        Rotation(dc, PenFor(DiagramElement.WheelSurfaceSpeed), new Point(235, 130), 34);
        Label(dc, DiagramElement.WheelSurfaceSpeed, 235, 130);

        // 金刚笔：从下方顶在砂轮外圆上。
        var tip = new Point(200, 226);
        var diamond = new StreamGeometry();
        using (StreamGeometryContext context = diamond.Open())
        {
            context.BeginFigure(tip, true, true);
            context.LineTo(new Point(190, 246), true, false);
            context.LineTo(new Point(210, 246), true, false);
        }

        dc.DrawGeometry(this.textBrush, this.normalPen, diamond);

        Arrow(dc, PenFor(DiagramElement.DressFeed), new Point(160, 252), new Point(310, 252));
        Label(dc, DiagramElement.DressFeed, 340, 252);
        Arrow(dc, PenFor(DiagramElement.DressInfeed), new Point(120, 250), new Point(120, 222));
        Label(dc, DiagramElement.DressInfeed, 96, 236);

        Pen passes = PenFor(DiagramElement.DressPasses);
        dc.DrawLine(passes, new Point(360, 60), new Point(360, 200));
        dc.DrawLine(passes, new Point(375, 60), new Point(375, 200));
        Label(dc, DiagramElement.DressPasses, 405, 130);
    }

    /// <summary>涡流探伤：探头沿辊身螺旋扫，螺距 P；辊在转（n）。</summary>
    private void DrawEddyCurrent(DrawingContext dc)
    {
        var body = new Rect(90, 110, 340, 80);
        Roll(dc, body);
        Rotation(dc, PenFor(DiagramElement.WorkpieceSpeed), new Point(52, 150), 22);
        Label(dc, DiagramElement.WorkpieceSpeed, 22, 196);

        for (int i = 0; i < 6; i++)
        {
            double x = 110 + (i * 56);
            dc.DrawLine(this.thinPen, new Point(x, 110), new Point(x + 28, 190));
        }

        DoubleArrow(dc, PenFor(DiagramElement.ScanPitch), new Point(166, 90), new Point(222, 90));
        Label(dc, DiagramElement.ScanPitch, 194, 72);
        dc.DrawLine(this.thinPen, new Point(166, 94), new Point(166, 110));
        dc.DrawLine(this.thinPen, new Point(222, 94), new Point(222, 110));
    }

    /// <summary>暂停：两道竖杠。</summary>
    private void DrawPause(DrawingContext dc)
    {
        Brush fill = IsLit(DiagramElement.PauseReason) ? this.accentBrush : this.fillBrush;
        dc.DrawRoundedRectangle(fill, this.normalPen, new Rect(215, 70, 30, 120), 4, 4);
        dc.DrawRoundedRectangle(fill, this.normalPen, new Rect(275, 70, 30, 120), 4, 4);
    }

    /// <summary>辅助动作：一个机构（A）和它的开关（I/O）。</summary>
    private void DrawAuxiliary(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(this.fillBrush, PenFor(DiagramElement.AuxAction), new Rect(120, 80, 130, 100), 8, 8);
        Label(dc, DiagramElement.AuxAction, 185, 130);

        Pen toggle = PenFor(DiagramElement.AuxState);
        dc.DrawRoundedRectangle(null, toggle, new Rect(310, 108, 90, 44), 22, 22);
        dc.DrawEllipse(IsLit(DiagramElement.AuxState) ? this.accentBrush : this.textBrush, null, new Point(378, 130), 16, 16);
        Label(dc, DiagramElement.AuxState, 355, 186);
        Arrow(dc, this.normalPen, new Point(255, 130), new Point(305, 130));
    }

    /// <summary>开始 / 结束：一面旗。</summary>
    private void DrawMarker(DrawingContext dc)
    {
        dc.DrawLine(this.normalPen, new Point(240, 50), new Point(240, 220));
        var flag = new StreamGeometry();
        using (StreamGeometryContext context = flag.Open())
        {
            context.BeginFigure(new Point(240, 50), true, true);
            context.LineTo(new Point(320, 75), true, false);
            context.LineTo(new Point(240, 100), true, false);
        }

        dc.DrawGeometry(this.fillBrush, this.normalPen, flag);
    }
}
