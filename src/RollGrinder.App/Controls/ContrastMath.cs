using System;
using System.Globalization;

namespace RollGrinder.App.Controls;

/// <summary>
/// WCAG 2.x 对比度计算。界面里所有"字看不看得清"的判断都用它：
/// 正文字 ≥ 4.5:1，大字（≥ 24 px，或 ≥ 18.66 px 粗体）≥ 3:1，
/// 禁用态的字与按钮描边这类非正文元素 ≥ 3:1。
///
/// 纯逻辑，不引用 WPF，单测与界面自检共用。
/// </summary>
public static class ContrastMath
{
    /// <summary>正文字的最低对比度。</summary>
    public const double NormalText = 4.5;

    /// <summary>大字、禁用字、界面元素边界的最低对比度。</summary>
    public const double LargeTextOrGraphics = 3.0;

    /// <summary>两种不透明颜色的对比度（1–21）。</summary>
    public static double Ratio(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        double l1 = RelativeLuminance(r1, g1, b1);
        double l2 = RelativeLuminance(r2, g2, b2);
        double lighter = Math.Max(l1, l2);
        double darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>两个 #RRGGBB 或 #AARRGGBB 颜色的对比度（透明度忽略）。</summary>
    public static double Ratio(string colorA, string colorB)
    {
        (byte r1, byte g1, byte b1) = Parse(colorA);
        (byte r2, byte g2, byte b2) = Parse(colorB);
        return Ratio(r1, g1, b1, r2, g2, b2);
    }

    /// <summary>这段字要求的最低对比度。</summary>
    public static double RequiredFor(double fontSizeDip, bool bold) =>
        fontSizeDip >= 24.0 || (bold && fontSizeDip >= 18.66) ? LargeTextOrGraphics : NormalText;

    /// <summary>
    /// 半透明颜色叠在不透明底色上之后的实际颜色（alpha 0–255）。
    /// 半透明的浮层、半透明的字都要先合成再算对比度。
    /// </summary>
    public static (byte R, byte G, byte B) Blend(byte a, byte r, byte g, byte b, byte baseR, byte baseG, byte baseB)
    {
        double alpha = a / 255.0;
        return (Mix(r, baseR), Mix(g, baseG), Mix(b, baseB));

        byte Mix(byte top, byte bottom) => (byte)Math.Round((top * alpha) + (bottom * (1.0 - alpha)));
    }

    private static double RelativeLuminance(byte r, byte g, byte b) =>
        (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));

    private static double Channel(byte value)
    {
        double c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static (byte R, byte G, byte B) Parse(string color)
    {
        string hex = color.Trim().TrimStart('#');
        if (hex.Length == 8)
        {
            hex = hex[2..];
        }

        if (hex.Length != 6)
        {
            throw new FormatException("Expected #RRGGBB or #AARRGGBB, got '" + color + "'.");
        }

        return (
            byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
