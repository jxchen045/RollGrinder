using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RollGrinder.App.Controls;

/// <summary>把窗口画成一张图（功能键块"◉ 截屏"、Ctrl+P、自检截图共用）。</summary>
public static class WindowCapture
{
    /// <summary>
    /// 截一张图存到 <paramref name="path"/>（扩展名 .png 存 PNG，其他存 JPEG）。
    /// 先铺窗口底色再画内容：RenderTargetBitmap 只画元素本身，透明的缝隙存成 JPEG 会变成黑块。
    /// </summary>
    /// <returns>存下了返回 true；窗口还没布局好返回 false。</returns>
    public static bool Save(Window window, string path)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (window.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1)
        {
            return false;
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);

        var backdrop = new DrawingVisual();
        using (DrawingContext dc = backdrop.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, bitmap.Width, bitmap.Height));
        }

        bitmap.Render(backdrop);
        bitmap.Render(root);

        BitmapEncoder encoder = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = 75 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return true;
    }
}
