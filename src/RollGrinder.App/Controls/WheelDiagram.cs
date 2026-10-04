using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RollGrinder.Core.Calibration;

namespace RollGrinder.App.Controls;

/// <summary>砂轮简图画哪一幅。</summary>
public enum WheelDiagramMode
{
    /// <summary>砂轮本身：直径 D、新砂轮直径 D0、宽度 B、磨损 ΔD。</summary>
    Wheel = 0,

    /// <summary>对刀与试磨：砂轮贴上轧辊，量辊径 d 反推砂轮直径。</summary>
    Trial = 1,
}

/// <summary>
/// 砂轮页与换砂轮向导的简图（修改稿 5.7、5②）：标出要量、要填的是哪个尺寸，当前那一项加粗换色。
/// 和工序简图一样只是示意、只标符号；数值和说明在旁边的参数格与提示里。
/// </summary>
public sealed class WheelDiagram : FrameworkElement
{
    /// <summary>对刀那一下（砂轮贴上轧辊）。</summary>
    public const string TouchKey = "touch";

    /// <summary>试磨后量的辊径。</summary>
    public const string TrialRollKey = "trialRoll";

    /// <summary>砂轮磨损（新砂轮直径减当前直径）。</summary>
    public const string WearKey = "wear";

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(WheelDiagramMode), typeof(WheelDiagram),
        new FrameworkPropertyMetadata(WheelDiagramMode.Wheel, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightKeyProperty = DependencyProperty.Register(
        nameof(HighlightKey), typeof(string), typeof(WheelDiagram),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double DesignWidth = 420.0;
    private const double DesignHeight = 260.0;

    private Pen normalPen = new(Brushes.Gray, 2.0);
    private Pen accentPen = new(Brushes.OrangeRed, 4.0);
    private Pen thinPen = new(Brushes.Gray, 1.0);
    private Brush textBrush = Brushes.Black;
    private Brush accentBrush = Brushes.OrangeRed;
    private Brush fillBrush = Brushes.LightGray;
    private Brush wheelBrush = Brushes.Gainsboro;
    private Typeface typeface = new("Consolas");
    private double pixelsPerDip = 1.0;

    public WheelDiagramMode Mode
    {
        get => (WheelDiagramMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>要亮的量：标定值的键（wheelDiameterMm、newWheelDiameterMm、wheelWidthMm）或 touch / trialRoll / wear。</summary>
    public string? HighlightKey
    {
        get => (string?)GetValue(HighlightKeyProperty);
        set => SetValue(HighlightKeyProperty, value);
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
        double scale = Math.Min(ActualWidth / DesignWidth, ActualHeight / DesignHeight);
        if (scale <= 0.0)
        {
            return;
        }

        dc.PushTransform(new TranslateTransform((ActualWidth - (DesignWidth * scale)) / 2.0, (ActualHeight - (DesignHeight * scale)) / 2.0));
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (Mode == WheelDiagramMode.Trial)
        {
            DrawTrial(dc);
        }
        else
        {
            DrawWheel(dc);
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

    private bool IsLit(string key) => string.Equals(HighlightKey, key, StringComparison.Ordinal);

    private Pen PenFor(string key) => IsLit(key) ? this.accentPen : this.normalPen;

    private void Label(DrawingContext dc, string key, string symbol, double x, double y)
    {
        bool lit = IsLit(key);
        var text = new FormattedText(
            symbol, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, this.typeface,
            lit ? 22.0 : 17.0, lit ? this.accentBrush : this.textBrush, this.pixelsPerDip);
        dc.DrawText(text, new Point(x - (text.Width / 2.0), y - (text.Height / 2.0)));
    }

    private static void DoubleArrow(DrawingContext dc, Pen pen, Point a, Point b)
    {
        dc.DrawLine(pen, a, b);
        Vector direction = b - a;
        if (direction.Length < 1.0)
        {
            return;
        }

        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        foreach ((Point tip, Vector back) in new[] { (b, -direction), (a, direction) })
        {
            dc.DrawLine(pen, tip, tip + (back * 10.0) + (normal * 5.0));
            dc.DrawLine(pen, tip, tip + (back * 10.0) - (normal * 5.0));
        }
    }

    /// <summary>砂轮正视（左）与侧视（右）：直径 D、新砂轮 D0（虚线）、磨损 ΔD、宽度 B。</summary>
    private void DrawWheel(DrawingContext dc)
    {
        var center = new Point(140, 130);
        dc.DrawEllipse(null, IsLit(CalibrationKeys.NewWheelDiameterMm) ? this.accentPen : this.thinPen, center, 110, 110);
        dc.DrawEllipse(this.wheelBrush, this.normalPen, center, 94, 94);
        dc.DrawEllipse(this.fillBrush, this.normalPen, center, 22, 22);

        DoubleArrow(dc, PenFor(CalibrationKeys.WheelDiameterMm), new Point(center.X - 94, center.Y), new Point(center.X + 94, center.Y));
        Label(dc, CalibrationKeys.WheelDiameterMm, "D", center.X + 50, center.Y - 14);
        DoubleArrow(dc, PenFor(CalibrationKeys.NewWheelDiameterMm), new Point(center.X - 110, 252), new Point(center.X + 110, 252));
        Label(dc, CalibrationKeys.NewWheelDiameterMm, "D0", center.X, 238);

        // 磨损：新砂轮外圆到现在外圆之间那一圈。
        Pen wear = PenFor(WearKey);
        dc.DrawLine(wear, new Point(center.X, center.Y - 110), new Point(center.X, center.Y - 94));
        Label(dc, WearKey, "ΔD", center.X + 26, center.Y - 118);

        // 侧视：宽度 B。
        var side = new Rect(320, 36, 44, 188);
        dc.DrawRectangle(this.wheelBrush, this.normalPen, side);
        DoubleArrow(dc, PenFor(CalibrationKeys.WheelWidthMm), new Point(side.Left, 16), new Point(side.Right, 16));
        Label(dc, CalibrationKeys.WheelWidthMm, "B", side.Left + (side.Width / 2), 244);
        dc.DrawLine(this.thinPen, new Point(side.Left, 20), new Point(side.Left, side.Top));
        dc.DrawLine(this.thinPen, new Point(side.Right, 20), new Point(side.Right, side.Top));
    }

    /// <summary>对刀与试磨：砂轮贴上轧辊（对刀），磨一刀后量辊径 d，反推砂轮直径 D。</summary>
    private void DrawTrial(DrawingContext dc)
    {
        var wheelCenter = new Point(120, 130);
        dc.DrawEllipse(this.wheelBrush, this.normalPen, wheelCenter, 90, 90);
        Label(dc, CalibrationKeys.WheelDiameterMm, "D", wheelCenter.X, wheelCenter.Y);

        var rollCenter = new Point(300, 130);
        dc.DrawEllipse(this.fillBrush, this.normalPen, rollCenter, 80, 80);
        DoubleArrow(dc, PenFor(TrialRollKey), new Point(rollCenter.X, rollCenter.Y - 80), new Point(rollCenter.X, rollCenter.Y + 80));
        Label(dc, TrialRollKey, "d", rollCenter.X + 20, rollCenter.Y);

        // 接触点：对刀时砂轮外圆碰到辊面。
        Pen touch = PenFor(TouchKey);
        dc.DrawEllipse(IsLit(TouchKey) ? this.accentBrush : null, touch, new Point(215, 130), 7, 7);
        dc.DrawLine(touch, new Point(215, 150), new Point(215, 190));
        Label(dc, TouchKey, "T", 215, 206);
    }
}
