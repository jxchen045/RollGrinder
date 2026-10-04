using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RollGrinder.App.Controls;

/// <summary>
/// 沿辊身的位置图（最终稿 5.1）：头架、尾架、辊身、往复行程（橙色）、拖板当前位置。
/// 横轴是机床 Z（头架 → 尾架），辊身从 Z 0 起（辊形零点，Q6）。点图上一处 = 在"定位 ▸ 拖板到 Z"里填上目标。
/// 自己画（OnRender），不用第三方控件；颜色取 Palette.Light.xaml 的曲线色位。
/// </summary>
public sealed class RollBodyStrip : FrameworkElement
{
    public static readonly DependencyProperty TravelMmProperty = Register(nameof(TravelMm), 6000.0);

    public static readonly DependencyProperty BodyLengthMmProperty = Register(nameof(BodyLengthMm), 0.0);

    public static readonly DependencyProperty CarriageZProperty = DependencyProperty.Register(
        nameof(CarriageZ), typeof(double?), typeof(RollBodyStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeStartProperty = DependencyProperty.Register(
        nameof(StrokeStart), typeof(double?), typeof(RollBodyStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeEndProperty = DependencyProperty.Register(
        nameof(StrokeEnd), typeof(double?), typeof(RollBodyStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>点了图上一处（参数是机床 Z，mm）。</summary>
    public event EventHandler<double>? TargetPicked;

    public RollBodyStrip()
    {
        // 画法按实际高度走；最小高度不能大于格子（标准档只给 75、紧凑档 45），否则下半截被裁掉。
        MinHeight = 40;
        Cursor = Cursors.Hand;
    }

    /// <summary>拖板行程（横轴全长，mm）。</summary>
    public double TravelMm
    {
        get => (double)GetValue(TravelMmProperty);
        set => SetValue(TravelMmProperty, value);
    }

    /// <summary>辊身长度（mm）；0 表示没有装辊的数据，只画机床行程。</summary>
    public double BodyLengthMm
    {
        get => (double)GetValue(BodyLengthMmProperty);
        set => SetValue(BodyLengthMmProperty, value);
    }

    public double? CarriageZ
    {
        get => (double?)GetValue(CarriageZProperty);
        set => SetValue(CarriageZProperty, value);
    }

    public double? StrokeStart
    {
        get => (double?)GetValue(StrokeStartProperty);
        set => SetValue(StrokeStartProperty, value);
    }

    public double? StrokeEnd
    {
        get => (double?)GetValue(StrokeEndProperty);
        set => SetValue(StrokeEndProperty, value);
    }

    private const double Edge = 60.0;

    private double Span => Math.Max(1.0, TravelMm);

    private double ToX(double z) => Edge + Math.Clamp(z / Span, 0.0, 1.0) * Math.Max(1.0, ActualWidth - 2 * Edge);

    private double ToZ(double x) => Math.Clamp((x - Edge) / Math.Max(1.0, ActualWidth - 2 * Edge), 0.0, 1.0) * Span;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        TargetPicked?.Invoke(this, ToZ(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 2 * Edge || height < 20)
        {
            return;
        }

        Brush ink = Brush("Brush.TextPrimary", Brushes.Black);
        Brush body = Brush("Brush.Key", Brushes.LightGray);
        Brush stock = Brush("Brush.Tile", Brushes.SlateGray);
        var orange = new Pen(Brush("Color.CurveWheel", Brushes.Orange), 6);
        var outline = new Pen(Brush("Brush.Divider", Brushes.Gray), 1);
        double mid = height / 2;

        // 行程基线。
        dc.DrawLine(outline, new Point(Edge, mid + 26), new Point(width - Edge, mid + 26));

        // 头架（左）、尾架（右）。
        dc.DrawRectangle(stock, null, new Rect(4, mid - 24, Edge - 10, 48));
        dc.DrawRectangle(stock, null, new Rect(width - Edge + 6, mid - 18, Edge - 10, 36));

        // 辊身。
        if (BodyLengthMm > 0)
        {
            dc.DrawRectangle(body, outline, new Rect(new Point(ToX(0), mid - 16), new Point(ToX(BodyLengthMm), mid + 16)));
        }

        // 往复行程：橙色。
        if (StrokeStart is { } from && StrokeEnd is { } to && to > from)
        {
            dc.DrawLine(orange, new Point(ToX(from), mid + 26), new Point(ToX(to), mid + 26));
        }

        // 拖板（砂轮）当前位置：三角 + 竖线 + 读数。
        if (CarriageZ is { } z)
        {
            double x = ToX(z);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                g.BeginFigure(new Point(x, mid - 20), isFilled: true, isClosed: true);
                g.LineTo(new Point(x - 10, mid - 36), isStroked: true, isSmoothJoin: false);
                g.LineTo(new Point(x + 10, mid - 36), isStroked: true, isSmoothJoin: false);
            }

            dc.DrawGeometry(orange.Brush, null, geometry);
            dc.DrawLine(new Pen(orange.Brush, 2), new Point(x, mid - 20), new Point(x, mid + 30));
            var text = new FormattedText(
                z.ToString("F1", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                15,
                ink,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(Math.Min(x + 6, width - text.Width - 2), mid + 30));
        }
    }

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) switch
    {
        Brush brush => brush,
        Color color => new SolidColorBrush(color),
        _ => fallback,
    };

    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(
        name, typeof(double), typeof(RollBodyStrip), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
}
