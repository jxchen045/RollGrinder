using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RollGrinder.App.Controls;

/// <summary>把窗口画成一张图（功能键块"◉ 截屏"、Ctrl+P、自检截图共用）。</summary>
public static class WindowCapture
{
    /// <summary>JPEG 质量。界面是大块平色加文字，70 看字不糊，体积约为 PNG 的十分之一。</summary>
    public const int DefaultJpegQuality = 70;

    /// <summary>
    /// 截一张图存到 <paramref name="path"/>（扩展名 .png 存 PNG，其他存 JPEG）。
    /// </summary>
    /// <returns>存下了返回 true；窗口还没布局好返回 false。</returns>
    public static bool Save(Window window, string path)
    {
        BitmapSource? bitmap = Render(window);
        if (bitmap is null)
        {
            return false;
        }

        Write(bitmap, path, DefaultJpegQuality);
        return true;
    }

    /// <summary>
    /// 画出画布本身（外壳里那块固定 1920×1080 / 1366×768 的 Grid "Canvas"）：
    /// 不管屏幕多大、Viewbox 缩了多少，图都是画布的原尺寸，字一个像素不糊——
    /// 云端 Windows 构建机的屏幕只有 1024×768，照样截出 1:1 的图。没有画布时退回整个窗口内容。
    /// 先铺窗口底色再画内容：透明的缝隙存成 JPEG 会变成黑块。
    /// </summary>
    public static BitmapSource? Render(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        FrameworkElement? target = window.FindName("Canvas") as FrameworkElement ?? window.Content as FrameworkElement;
        if (target is null || target.ActualWidth < 1 || target.ActualHeight < 1)
        {
            return null;
        }

        double width = Math.Ceiling(target.ActualWidth);
        double height = Math.Ceiling(target.ActualHeight);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var area = new Rect(0, 0, width, height);
            dc.DrawRectangle(window.Background ?? Brushes.White, null, area);

            // VisualBrush 按元素自己的坐标画，祖先的缩放（Viewbox）不参与。
            dc.DrawRectangle(
                new VisualBrush(target)
                {
                    Stretch = Stretch.None,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = area,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                },
                null,
                area);
        }

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>像素指纹：两张图一模一样就同一个指纹（自检里画面没变的那一步不再另存一张）。</summary>
    public static string Fingerprint(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(SHA1.HashData(pixels));
    }

    /// <summary>存盘：.png 存 PNG，其他按 <paramref name="jpegQuality"/> 存 JPEG。</summary>
    public static void Write(BitmapSource bitmap, string path, int jpegQuality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentException.ThrowIfNullOrEmpty(path);

        BitmapEncoder encoder = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 30, 95) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
