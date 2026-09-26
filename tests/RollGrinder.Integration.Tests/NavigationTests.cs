using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using RollGrinder.App.Navigation;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>
/// 页面切换的规则守卫。这些断言对应 docs/design/导航规范.md 里的五条硬规则：
/// 1. 主页是唯一的枢纽，区域之间是平的，不叠历史栈；
/// 2. 导航槽只退一级，且在任何位置都有明确含义（没有死键）；
/// 3. 最深三层，回主页永远一步；
/// 4. 任务跳转只记一个返回点，办完即清；
/// 5. 平切会清掉子视图与任务返回点，状态不会残留。
/// </summary>
public sealed class NavigationTests
{
    private static readonly PageKey[] AllAreas = Enum.GetValues<PageKey>();

    [Fact]
    public void Starts_on_the_home_page_with_the_menu_key()
    {
        var model = new NavigationModel();

        model.CurrentArea.Should().Be(PageKey.AutoGrinding);
        model.Depth.Should().Be(1);
        model.CurrentSubViewKey.Should().BeNull();
        model.TaskReturnArea.Should().BeNull();

        NavigationKeyDescriptor key = model.DescribeNavigationKey();
        key.Role.Should().Be(NavigationKeyRole.OpenAreaMenu, "主页没有上一级，第 8 键改成打开菜单而不是发呆");
        key.TargetArea.Should().BeNull();
    }

    [Theory]
    [InlineData(PageKey.Profile)]
    [InlineData(PageKey.Steps)]
    [InlineData(PageKey.Records)]
    [InlineData(PageKey.Manual)]
    [InlineData(PageKey.Diagnostics)]
    public void Any_sub_page_offers_a_single_step_back_to_home(PageKey area)
    {
        var model = new NavigationModel();

        model.GoToArea(area).Should().BeTrue();
        model.Depth.Should().Be(2);

        NavigationKeyDescriptor key = model.DescribeNavigationKey();
        key.Role.Should().Be(NavigationKeyRole.BackToHome);
        key.TargetArea.Should().Be(PageKey.AutoGrinding);

        model.GoToArea(key.TargetArea!.Value);
        model.CurrentArea.Should().Be(PageKey.AutoGrinding);
    }

    [Fact]
    public void Switching_between_areas_never_accumulates_history()
    {
        var model = new NavigationModel();

        // 来回切十几次：没有栈，状态只取决于"现在在哪儿"。
        for (int i = 0; i < 12; i++)
        {
            model.GoToArea(PageKey.Records);
            model.GoToArea(PageKey.Manual);
        }

        model.GoToArea(PageKey.Profile);
        model.TaskReturnArea.Should().BeNull();
        model.DescribeNavigationKey().Role.Should().Be(NavigationKeyRole.BackToHome,
            "不管绕了多少圈，返回键始终只是回主页");
    }

    [Fact]
    public void Task_jump_remembers_exactly_one_return_point()
    {
        var model = new NavigationModel();
        model.GoToArea(PageKey.Steps);

        // 工序编程 → 选择辊形。
        model.StartTask(PageKey.Profile, PageKey.Steps).Should().BeTrue();
        model.TaskReturnArea.Should().Be(PageKey.Steps);

        NavigationKeyDescriptor key = model.DescribeNavigationKey();
        key.Role.Should().Be(NavigationKeyRole.BackToTask);
        key.TargetArea.Should().Be(PageKey.Steps);
        key.LabelResourceKey.Should().Be("Nav_BackToPageFormat", "标签要写明退回哪一页");

        model.CompleteTask();
        model.CurrentArea.Should().Be(PageKey.Steps);
        model.TaskReturnArea.Should().BeNull("返回点用完即清，不会第二次生效");
        model.DescribeNavigationKey().Role.Should().Be(NavigationKeyRole.BackToHome);
    }

    [Fact]
    public void A_task_issued_from_a_task_page_gives_that_page_its_return_point_back()
    {
        var model = new NavigationModel();
        model.GoToArea(PageKey.Steps);

        // 工艺程序 →「用于作业」→ 作业页 →「新登记轧辊」→ 台账（记录页的子视图）。
        model.StartTask(PageKey.Job, PageKey.Steps);
        model.StartTask(PageKey.Records, PageKey.Job);
        model.OpenSubView("SubView_RollLedger");
        model.DescribeNavigationKey().Role.Should().Be(NavigationKeyRole.CloseSubView);

        model.CloseSubView();
        model.DescribeNavigationKey().TargetArea.Should().Be(PageKey.Job);

        // 登记完回作业页：作业页的"返回 工艺程序"还在。
        model.CompleteTask();
        model.CurrentArea.Should().Be(PageKey.Job);
        model.TaskReturnArea.Should().Be(PageKey.Steps);
        model.DescribeNavigationKey().Should().Be(
            new NavigationKeyDescriptor(NavigationKeyRole.BackToTask, "Nav_BackToPageFormat", PageKey.Steps));

        model.CompleteTask();
        model.CurrentArea.Should().Be(PageKey.Steps);
        model.TaskReturnArea.Should().BeNull("最外层的返回点用完即清");
    }

    [Fact]
    public void A_plain_switch_in_the_middle_of_a_nested_task_drops_every_return_point()
    {
        var model = new NavigationModel();
        model.StartTask(PageKey.Job, PageKey.Steps);
        model.StartTask(PageKey.Records, PageKey.Job);

        model.GoToArea(PageKey.Settings);
        model.GoToArea(PageKey.Job);
        model.TaskReturnArea.Should().BeNull("从菜单切走再回来，旧的任务链全部作废");
    }

    [Fact]
    public void Task_jump_to_the_page_that_issued_it_is_just_a_plain_switch()
    {
        var model = new NavigationModel();
        model.GoToArea(PageKey.Steps);

        model.StartTask(PageKey.Steps, PageKey.Steps).Should().BeFalse();
        model.TaskReturnArea.Should().BeNull("原地跳转不该留下一个指向自己的返回点");
    }

    [Fact]
    public void Leaving_an_area_by_a_plain_switch_drops_the_task_return_point()
    {
        var model = new NavigationModel();
        model.StartTask(PageKey.Profile, PageKey.Steps);

        // 中途从菜单切去别处：任务作废，不能把旧返回点带到新页面上。
        model.GoToArea(PageKey.Records);
        model.TaskReturnArea.Should().BeNull();
        model.DescribeNavigationKey().Role.Should().Be(NavigationKeyRole.BackToHome);
    }

    [Fact]
    public void Sub_view_adds_one_level_and_the_key_points_back_to_its_own_page()
    {
        var model = new NavigationModel();
        model.GoToArea(PageKey.Diagnostics);
        model.OpenSubView("SubView_TagMonitor");

        model.Depth.Should().Be(3);
        NavigationKeyDescriptor key = model.DescribeNavigationKey();
        key.Role.Should().Be(NavigationKeyRole.CloseSubView);
        key.TargetArea.Should().Be(PageKey.Diagnostics);

        model.CloseSubView().Should().BeTrue();
        model.Depth.Should().Be(2);
        model.DescribeNavigationKey().Role.Should().Be(NavigationKeyRole.BackToHome);
    }

    [Fact]
    public void Sub_view_takes_priority_over_a_task_return_point()
    {
        var model = new NavigationModel();
        model.StartTask(PageKey.Diagnostics, PageKey.Manual);
        model.OpenSubView("SubView_TagMonitor");

        // 先退出子视图，再退回发起页——一次只退一级。
        model.DescribeNavigationKey().Role.Should().Be(NavigationKeyRole.CloseSubView);
        model.CloseSubView();
        model.DescribeNavigationKey().Role.Should().Be(NavigationKeyRole.BackToTask);
        model.DescribeNavigationKey().TargetArea.Should().Be(PageKey.Manual);
    }

    [Fact]
    public void Closing_a_sub_view_that_is_not_open_changes_nothing()
    {
        var model = new NavigationModel();
        model.GoToArea(PageKey.Records);

        model.CloseSubView().Should().BeFalse();
        model.CurrentArea.Should().Be(PageKey.Records);
    }

    [Fact]
    public void Switching_areas_closes_the_menu_and_any_sub_view()
    {
        var model = new NavigationModel();
        model.GoToArea(PageKey.Diagnostics);

        model.OpenAreaMenu();
        model.OpenSubView("SubView_TagMonitor");
        model.IsAreaMenuOpen.Should().BeFalse("打开子视图会把菜单收掉");

        model.OpenAreaMenu();
        model.GoToArea(PageKey.Records);
        model.IsAreaMenuOpen.Should().BeFalse();
        model.CurrentSubViewKey.Should().BeNull();
    }

    [Fact]
    public void Home_is_always_one_step_away_from_anywhere()
    {
        foreach (PageKey area in AllAreas)
        {
            var model = new NavigationModel();
            model.GoToArea(area);
            model.OpenSubView("SubView_TagMonitor");

            model.GoToArea(model.HomeArea);

            model.CurrentArea.Should().Be(PageKey.AutoGrinding);
            model.Depth.Should().Be(1);
            model.CurrentSubViewKey.Should().BeNull();
        }
    }

    [Fact]
    public void Depth_never_exceeds_three()
    {
        foreach (PageKey area in AllAreas)
        {
            var model = new NavigationModel();
            model.StartTask(area, PageKey.Steps);
            model.OpenSubView("SubView_TagMonitor");
            model.OpenSubView("SubView_TagMonitor");

            model.Depth.Should().BeLessThanOrEqualTo(3, "子视图不叠层：再打开一个只是换一个，不会越钻越深");
        }
    }

    [Fact]
    public void The_navigation_key_always_has_a_meaning()
    {
        // 没有任何状态组合会让第 8 键变成按了没反应的死键。
        foreach (PageKey area in AllAreas)
        {
            foreach (bool withTask in new[] { false, true })
            {
                foreach (bool withSubView in new[] { false, true })
                {
                    foreach (bool withMenu in new[] { false, true })
                    {
                    var model = new NavigationModel();
                    if (withTask)
                    {
                        model.StartTask(area, PageKey.Manual);
                    }
                    else
                    {
                        model.GoToArea(area);
                    }

                    if (withSubView)
                    {
                        model.OpenSubView("SubView_TagMonitor");
                    }

                    if (withMenu)
                    {
                        model.OpenAreaMenu();
                    }

                    NavigationKeyDescriptor key = model.DescribeNavigationKey();
                    key.LabelResourceKey.Should().NotBeNullOrWhiteSpace();
                    if (key.Role is not (NavigationKeyRole.OpenAreaMenu or NavigationKeyRole.CloseAreaMenu))
                    {
                        key.TargetArea.Should().NotBeNull("除了开/收菜单，其余角色都必须说明退到哪一页");
                    }
                    }
                }
            }
        }
    }

    [Fact]
    public void While_the_menu_is_open_the_navigation_key_cancels_it_without_moving()
    {
        // 页面菜单是软键条原地变身：第 8 键此时就是"取消"，按下去软键条变回来，人还在原页。
        var model = new NavigationModel();
        model.GoToArea(PageKey.Records);
        model.OpenSubView("SubView_TagMonitor");
        model.OpenAreaMenu();

        NavigationKeyDescriptor key = model.DescribeNavigationKey();

        key.Role.Should().Be(NavigationKeyRole.CloseAreaMenu, "菜单态优先于子视图与返回主页");
        key.LabelResourceKey.Should().Be("Menu_Cancel");
        key.TargetArea.Should().BeNull("取消不换页");

        model.CloseAreaMenu();
        model.CurrentArea.Should().Be(PageKey.Records);
        model.DescribeNavigationKey().Role.Should().Be(NavigationKeyRole.CloseSubView, "收起菜单后第 8 键回到原来的角色");
    }

    [Fact]
    public void Opening_a_sub_view_needs_a_key()
    {
        var model = new NavigationModel();

        Action act = () => model.OpenSubView(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Every_navigation_string_exists_in_both_languages()
    {
        string[] required =
        {
            "Nav_AreaMenu", "Nav_BackToHome", "Nav_BackToPageFormat", "Nav_BreadcrumbSeparator",
            "Menu_Cancel", "Menu_Current", "Fn_Empty", "Nav_ShortcutFormat", "Nav_ShortcutBadgeHint",
            "Menu_AutoGrindingHint", "Menu_StepsHint", "Menu_ProfileHint",
            "Menu_ManualHint", "Menu_RecordsHint", "Menu_DiagnosticsHint",
            "Leave_TitleFormat", "Leave_Message", "Leave_Save", "Leave_Discard", "Leave_Cancel",
            "Shell_OpenAreaMenu", "Shell_Modified", "Shell_ReadOnlyRunning",
            "SubView_TagMonitor", "Alarm_SaveFailedStayingOnPage",
        };

        foreach (string fileName in new[] { "Strings.resx", "Strings.en-US.resx" })
        {
            IReadOnlySet<string> keys = LoadKeys(fileName);
            foreach (string key in required)
            {
                keys.Should().Contain(key, $"{fileName} 缺少导航文案 {key}");
            }
        }
    }

    private static IReadOnlySet<string> LoadKeys(string fileName)
    {
        string path = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "Resources", fileName);
        XDocument document = XDocument.Load(path);
        return document.Root!
            .Elements("data")
            .Select(element => element.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
