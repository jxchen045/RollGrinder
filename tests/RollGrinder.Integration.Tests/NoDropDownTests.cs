using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 修改稿原则 1"按键优先，触摸兼容"：全系统去掉下拉菜单。下拉只对鼠标友好，
/// 按键面板上要先按开、再上下挑、再回车，还常常弹出在手指挡住的地方。
/// 选项少的用竖向软键或分段键，多的用可上下键移动的列表。
/// </summary>
public sealed class NoDropDownTests
{
    [Fact]
    public void No_view_uses_a_combo_box()
    {
        string appRoot = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App");
        string[] offenders = Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("<ComboBox", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(appRoot, file))
            .ToArray();

        offenders.Should().BeEmpty("下拉菜单换成竖向软键、分段键或列表");
    }
}
