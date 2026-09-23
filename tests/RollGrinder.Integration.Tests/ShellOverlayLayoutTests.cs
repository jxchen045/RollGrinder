using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 外壳的三个浮层（离开确认、登录、用户管理）必须是同一层的兄弟，各管各的显隐。
///
/// 曾经登录与用户管理被错嵌进"离开确认"里面：离开确认平时是隐藏的，于是登录框永远出不来，
/// 启动后整页变灰、什么也按不了——而命令层的自检全过，因为它不看屏幕。
/// </summary>
public sealed class ShellOverlayLayoutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_three_overlays_are_siblings()
    {
        string path = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "Views", "ShellWindow.xaml");
        XDocument document = XDocument.Load(path);

        XElement[] overlays = new[] { "LeaveConfirmOverlay", "SignInOverlay", "UserAdminOverlay" }
            .Select(name => document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == name))
            .Select((element, i) => element ?? throw new Xunit.Sdk.XunitException("overlay #" + i + " is missing"))
            .ToArray();

        overlays.Select(o => o.Parent).Distinct().Should().ContainSingle("三个浮层必须挂在同一个容器下，谁也不能套在谁里面");
        foreach (XElement overlay in overlays)
        {
            overlay.Ancestors().Should().NotContain(overlays, "浮层不能嵌在另一个浮层里");
        }
    }
}
