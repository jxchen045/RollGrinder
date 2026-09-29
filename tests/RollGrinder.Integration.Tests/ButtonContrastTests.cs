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
/// 每一种按钮样式在常态、按下、禁用三种状态下字都看得清（触摸屏没有悬停，界面最终稿去掉了悬停态）。
///
/// 曾经共用模板把所有按钮按下改浅蓝：白字的主按钮 / 启动 / 危险一按，对比度只剩约 1.1:1。
/// 这里把 Controls.xaml 里每个按钮样式沿 BasedOn 链展开，颜色经 Palette.Light.xaml 解析成色值，逐对核算。
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
        IReadOnlyDictionary<string, XElement> keyed = LoadButtonStyles();
        var problems = new List<string>();
        int checkedVariants = 0;

        foreach ((string name, IReadOnlyDictionary<string, string> setters) in Variants(keyed))
        {
            checkedVariants++;
            string Color(string property) =>
                setters.TryGetValue(property, out string? key) && brushes.TryGetValue(key, out string? hex)
                    ? hex
                    : throw new InvalidOperationException($"{name}: {property} is not resolvable ({(setters.TryGetValue(property, out string? k) ? k : "unset")})");

            string fg = Color("Foreground");
            Check(name, "normal", fg, Color("Background"), ContrastMath.NormalText, problems);
            Check(name, "pressed", fg, Color("ctl:ButtonStates.PressedBackground"), ContrastMath.NormalText, problems);
        }

        keyed.Should().ContainKeys(
            "SecondaryButton", "PrimaryButton", "StartButton", "DangerButton", "NavigationButton",
            "SoftKeyButton", "CancelKeyButton", "ConfirmKeyButton", "HelpKeyButton", "AreaMenuKeyButton", "AreaMenuKeyCurrentButton");
        checkedVariants.Should().BeGreaterThan(keyed.Count, "样式触发器（选中、保持型动作点亮、待确认）的变体也要核算");
        problems.Should().BeEmpty();
    }

    [Fact]
    public void The_shared_disabled_look_is_readable()
    {
        // 禁用态所有按钮共用一层中性灰（模板里的 DisabledLayer），核一次即可。
        IReadOnlyDictionary<string, string> brushes = LoadBrushColors();
        ContrastMath.Ratio(brushes["Brush.DisabledText"], brushes["Brush.DisabledFill"])
            .Should().BeGreaterThanOrEqualTo(ContrastMath.LargeTextOrGraphics);
    }

    [Fact]
    public void Title_row_alarm_and_emergency_stop_are_readable()
    {
        // 界面最终稿 4.2：报警号与文字红字写在标题行上；急停时整条标题行变红、白字"急停"。
        IReadOnlyDictionary<string, string> brushes = LoadBrushColors();
        ContrastMath.Ratio(brushes["Brush.Alarm"], brushes["Brush.TitleRow"]).Should().BeGreaterThanOrEqualTo(ContrastMath.NormalText);
        ContrastMath.Ratio(brushes["Brush.OnAccent"], brushes["Brush.Alarm"]).Should().BeGreaterThanOrEqualTo(ContrastMath.NormalText);

        XDocument shell = XDocument.Load(Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "Views", "ShellWindow.xaml"));
        XElement alarm = shell.Descendants(Wpf + "Button").Single(b => (string?)b.Attribute(X + "Name") == "AlarmButton");
        alarm.Descendants(Wpf + "TextBlock")
            .Select(t => (string?)t.Attribute("Foreground"))
            .Should().OnlyContain(f => f == "{StaticResource Brush.Alarm}", "alarm text on the title row is red");
    }

    [Fact]
    public void Soft_key_states_are_readable()
    {
        // 选中（青底 Active）、待写入（红字）、焦点格（FocusField）都要看得清。
        IReadOnlyDictionary<string, string> brushes = LoadBrushColors();
        ContrastMath.Ratio(brushes["Brush.OnAccent"], brushes["Brush.Active"]).Should().BeGreaterThanOrEqualTo(ContrastMath.NormalText);
        ContrastMath.Ratio(brushes["Brush.PendingValue"], brushes["Brush.Surface"]).Should().BeGreaterThanOrEqualTo(ContrastMath.NormalText);
        ContrastMath.Ratio(brushes["Brush.TextPrimary"], brushes["Brush.FocusField"]).Should().BeGreaterThanOrEqualTo(ContrastMath.NormalText);
        ContrastMath.Ratio(brushes["Brush.OnAccent"], brushes["Brush.ChannelRow"]).Should().BeGreaterThanOrEqualTo(ContrastMath.NormalText);
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

    /// <summary>
    /// 要核算的全部外观：每个带键的按钮样式、写在模板里的内联按钮样式，
    /// 以及它们每一个样式触发器生效时的样子（基础 Setter 再叠上触发器的 Setter）。
    /// </summary>
    private static IEnumerable<(string Name, IReadOnlyDictionary<string, string> Setters)> Variants(
        IReadOnlyDictionary<string, XElement> keyed)
    {
        XDocument controls = XDocument.Load(Path.Combine(Themes, "Controls.xaml"));
        var all = controls.Descendants(Wpf + "Style")
            .Where(s => (string?)s.Attribute("TargetType") == "Button")
            .Select((s, i) => (Name: (string?)s.Attribute(X + "Key") ?? "inline#" + i, Style: s));

        foreach ((string name, XElement style) in all)
        {
            IReadOnlyDictionary<string, string> baseSetters = Resolve(style, keyed);
            yield return (name, baseSetters);

            IEnumerable<XElement> triggers = Chain(style, keyed)
                .SelectMany(s => s.Elements(Wpf + "Style.Triggers").Elements());
            int t = 0;
            foreach (XElement trigger in triggers)
            {
                var merged = new Dictionary<string, string>(baseSetters, StringComparer.Ordinal);
                Apply(trigger.Elements(Wpf + "Setter"), merged);
                yield return ($"{name} trigger#{t++}", merged);
            }
        }
    }

    /// <summary>样式本身及其 BasedOn 祖先，祖先在前。</summary>
    private static IEnumerable<XElement> Chain(XElement style, IReadOnlyDictionary<string, XElement> keyed)
    {
        var chain = new List<XElement>();
        for (XElement? s = style; s is not null;)
        {
            chain.Insert(0, s);
            s = (string?)s.Attribute("BasedOn") is { } basedOn ? keyed[basedOn["{StaticResource ".Length..^1].Trim()] : null;
        }

        return chain;
    }

    private static IReadOnlyDictionary<string, string> Resolve(XElement style, IReadOnlyDictionary<string, XElement> keyed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement s in Chain(style, keyed))
        {
            Apply(s.Elements(Wpf + "Setter"), result);
        }

        return result;
    }

    private static void Apply(IEnumerable<XElement> setters, Dictionary<string, string> into)
    {
        foreach (XElement setter in setters)
        {
            string value = (string?)setter.Attribute("Value") ?? string.Empty;
            if (value.StartsWith("{StaticResource ", StringComparison.Ordinal))
            {
                into[(string)setter.Attribute("Property")!] = value["{StaticResource ".Length..^1].Trim();
            }
        }
    }
}
