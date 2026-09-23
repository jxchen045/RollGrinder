using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using RollGrinder.App.Controls;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 每一种按钮样式在常态、悬停、按下、禁用四种状态下字都看得清。
///
/// 曾经共用模板把所有按钮悬停改浅灰、按下改浅蓝：白字的主按钮 / 启动 / 危险 / 导航槽一悬停，
/// 对比度只剩约 1.1:1。这里把 Controls.xaml 里每个按钮样式沿 BasedOn 链展开，
/// 颜色经 Palette.Light.xaml 解析成色值，逐对核算。
/// </summary>
public sealed class ButtonContrastTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string Themes = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "Themes");

    [Fact]
    public void Every_button_style_is_readable_in_every_state()
    {
        IReadOnlyDictionary<string, string> brushes = LoadBrushColors();
        IReadOnlyDictionary<string, XElement> styles = LoadButtonStyles();
        var problems = new List<string>();

        foreach (string name in styles.Keys)
        {
            IReadOnlyDictionary<string, string> setters = Resolve(name, styles);
            string Color(string property) =>
                setters.TryGetValue(property, out string? key) && brushes.TryGetValue(key, out string? hex)
                    ? hex
                    : throw new InvalidOperationException($"{name}: {property} is not resolvable ({(setters.TryGetValue(property, out string? k) ? k : "unset")})");

            string fg = Color("Foreground");
            Check(name, "normal", fg, Color("Background"), ContrastMath.NormalText, problems);
            Check(name, "hover", fg, Color("ctl:ButtonStates.HoverBackground"), ContrastMath.NormalText, problems);
            Check(name, "pressed", fg, Color("ctl:ButtonStates.PressedBackground"), ContrastMath.NormalText, problems);
            Check(name, "disabled", Color("ctl:ButtonStates.DisabledForeground"), Color("ctl:ButtonStates.DisabledBackground"),
                ContrastMath.LargeTextOrGraphics, problems);
        }

        styles.Should().ContainKeys("SecondaryButton", "PrimaryButton", "StartButton", "DangerButton", "NavigationButton");
        problems.Should().BeEmpty();
    }

    [Fact]
    public void White_buttons_have_a_visible_edge_on_white_cards()
    {
        // 按钮描边是界面元素边界，≥ 3:1——以前 1.9:1，白按钮放在白卡片上几乎看不出边。
        IReadOnlyDictionary<string, string> brushes = LoadBrushColors();
        ContrastMath.Ratio(brushes["Brush.ButtonBorder"], brushes["Brush.Surface"])
            .Should().BeGreaterThanOrEqualTo(ContrastMath.LargeTextOrGraphics);
    }

    [Theory]
    [InlineData("Brush.TextPrimary")]
    [InlineData("Brush.TextSecondary")]
    [InlineData("Brush.TextMuted")]
    public void Body_text_colours_are_readable_on_every_light_fill(string text)
    {
        IReadOnlyDictionary<string, string> brushes = LoadBrushColors();
        foreach (string fill in new[] { "Brush.Surface", "Brush.HeaderFill", "Brush.Background", "Brush.FunctionBar", "Brush.SelectionFill", "Brush.RowAlternate" })
        {
            ContrastMath.Ratio(brushes[text], brushes[fill]).Should().BeGreaterThanOrEqualTo(
                ContrastMath.NormalText, $"{text} on {fill}");
        }
    }

    [Fact]
    public void The_contrast_formula_matches_known_values()
    {
        ContrastMath.Ratio("#000000", "#FFFFFF").Should().BeApproximately(21.0, 0.01);
        ContrastMath.Ratio("#FFFFFF", "#FFFFFF").Should().BeApproximately(1.0, 0.001);
        ContrastMath.Ratio("#767676", "#FFFFFF").Should().BeApproximately(4.54, 0.01);
    }

    private static void Check(string style, string state, string fg, string bg, double required, List<string> problems)
    {
        double ratio = ContrastMath.Ratio(fg, bg);
        if (ratio < required)
        {
            problems.Add($"{style} {state}: {fg} on {bg} = {ratio:0.00}:1 < {required}:1");
        }
    }

    /// <summary>Brush.X → #RRGGBB（经 Color.Y 解析）。</summary>
    private static IReadOnlyDictionary<string, string> LoadBrushColors()
    {
        XDocument palette = XDocument.Load(Path.Combine(Themes, "Palette.Light.xaml"));
        var colors = palette.Root!.Elements(Wpf + "Color")
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim());
        var brushes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement brush in palette.Root!.Elements(Wpf + "SolidColorBrush"))
        {
            string color = (string)brush.Attribute("Color")!;
            brushes[(string)brush.Attribute(X + "Key")!] = color.StartsWith("{StaticResource ", StringComparison.Ordinal)
                ? colors[color["{StaticResource ".Length..^1].Trim()]
                : color;
        }

        return brushes;
    }

    private static IReadOnlyDictionary<string, XElement> LoadButtonStyles()
    {
        XDocument controls = XDocument.Load(Path.Combine(Themes, "Controls.xaml"));
        return controls.Root!.Elements(Wpf + "Style")
            .Where(s => (string?)s.Attribute("TargetType") == "Button" && s.Attribute(X + "Key") is not null)
            .ToDictionary(s => (string)s.Attribute(X + "Key")!);
    }

    /// <summary>沿 BasedOn 链合并 Setter：子样式覆盖父样式。值只保留 StaticResource 的键名。</summary>
    private static IReadOnlyDictionary<string, string> Resolve(string name, IReadOnlyDictionary<string, XElement> styles)
    {
        XElement style = styles[name];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if ((string?)style.Attribute("BasedOn") is { } basedOn)
        {
            string parent = basedOn["{StaticResource ".Length..^1].Trim();
            foreach (KeyValuePair<string, string> pair in Resolve(parent, styles))
            {
                result[pair.Key] = pair.Value;
            }
        }

        foreach (XElement setter in style.Elements(Wpf + "Setter"))
        {
            string value = (string?)setter.Attribute("Value") ?? string.Empty;
            if (value.StartsWith("{StaticResource ", StringComparison.Ordinal))
            {
                result[(string)setter.Attribute("Property")!] = value["{StaticResource ".Length..^1].Trim();
            }
        }

        return result;
    }
}
