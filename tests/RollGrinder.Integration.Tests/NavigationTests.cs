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
/// 画面切换的规则守卫（界面最终稿 4.1、4.4）：
/// 1. 区域之间是平的，不叠历史栈；
/// 2. 任务跳转只记一个返回点，办完即清；发起页自己的返回点在回来时恢复；
/// 3. "返回"只退一级：菜单 &gt; 子功能 &gt; 任务返回点；区域根部没有可退的；
/// 4. 平切会清掉子功能与任务返回点，状态不会残留。
/// </summary>
public sealed class NavigationTests
{
    [Fact]
    public void Starts_on_manual_grinding_with_nothing_to_go_back_to()
    {
        var model = new NavigationModel();

        model.CurrentPage.Should().Be(PageKey.ManualGrinding);
        model.CurrentArea.Should().Be(AreaKey.Machine);
        model.CurrentSubViewKey.Should().BeNull();
        model.TaskReturnPage.Should().BeNull();
        model.DescribeBack().Should().Be(new BackDescriptor(BackRole.None, null),
            "区域根部不设'回主页'：换区域是左栏一点");
    }

    [Fact]
    public void A_custom_home_page_is_where_the_session_starts()
    {
        var model = new NavigationModel(PageKey.Steps);

        model.HomePage.Should().Be(PageKey.Steps);
        model.CurrentPage.Should().Be(PageKey.Steps);
        model.CurrentArea.Should().Be(AreaKey.Steps);
    }

    [Fact]
    public void Switching_between_pages_never_accumulates_history()
    {
        var model = new NavigationModel();

        for (int i = 0; i < 12; i++)
        {
            model.GoTo(PageKey.Records);
            model.GoTo(PageKey.Manual);
        }

        model.GoTo(PageKey.Profile);
        model.TaskReturnPage.Should().BeNull();
        model.DescribeBack().Role.Should().Be(BackRole.None, "不管绕了多少圈，平切之后都没有可退的");
    }

    [Fact]
    public void Going_to_the_current_page_reports_no_change_but_still_clears_state()
    {
        var model = new NavigationModel();
        model.OpenSubView("SubView_Compensation");
        model.OpenAreaMenu();

        model.GoTo(PageKey.ManualGrinding).Should().BeFalse();

        model.CurrentSubViewKey.Should().BeNull();
        model.IsAreaMenuOpen.Should().BeFalse();
    }

    [Fact]
    public void Task_jump_remembers_exactly_one_return_point()
    {
        var model = new NavigationModel();
        model.GoTo(PageKey.Steps);

        // 工艺程序 →「用于作业」→ 作业向导。
        model.StartTask(PageKey.Job, PageKey.Steps).Should().BeTrue();
        model.TaskReturnPage.Should().Be(PageKey.Steps);
        model.DescribeBack().Should().Be(new BackDescriptor(BackRole.BackToTask, PageKey.Steps));

        model.CompleteTask();
        model.CurrentPage.Should().Be(PageKey.Steps);
        model.TaskReturnPage.Should().BeNull("返回点用完即清，不会第二次生效");
        model.DescribeBack().Role.Should().Be(BackRole.None);
    }

    [Fact]
    public void A_task_issued_from_a_task_page_gives_that_page_its_return_point_back()
    {
        var model = new NavigationModel();
        model.GoTo(PageKey.Steps);

        // 工艺程序 → 作业 →「新登记轧辊」→ 轧辊 › 新登记。
        model.StartTask(PageKey.Job, PageKey.Steps);
        model.StartTask(PageKey.Rolls, PageKey.Job);
        model.OpenSubView("SubView_RollLedger");
        model.DescribeBack().Role.Should().Be(BackRole.CloseSubView);

        model.CloseSubView();
        model.DescribeBack().Should().Be(new BackDescriptor(BackRole.BackToTask, PageKey.Job));

        model.CompleteTask();
        model.CurrentPage.Should().Be(PageKey.Job);
        model.TaskReturnPage.Should().Be(PageKey.Steps, "作业向导原来的'返回 工艺程序'还在");
    }

    [Fact]
    public void A_plain_switch_in_the_middle_of_a_nested_task_drops_every_return_point()
    {
        var model = new NavigationModel();
        model.GoTo(PageKey.Steps);
        model.StartTask(PageKey.Job, PageKey.Steps);
        model.StartTask(PageKey.Profile, PageKey.Job);

        model.GoTo(PageKey.Records);
        model.CompleteTask();

        model.CurrentPage.Should().Be(model.HomePage, "平切过一次，所有返回点都作废；没返回点的任务完成回开机画面");
        model.TaskReturnPage.Should().BeNull();
    }

    [Fact]
    public void Task_jump_to_the_page_that_issued_it_is_just_a_plain_switch()
    {
        var model = new NavigationModel();
        model.GoTo(PageKey.Profile);

        model.StartTask(PageKey.Profile, PageKey.Profile).Should().BeFalse();
        model.TaskReturnPage.Should().BeNull();
    }

    [Fact]
    public void Sub_view_takes_priority_over_a_task_return_point_and_menu_over_both()
    {
        var model = new NavigationModel();
        model.GoTo(PageKey.Steps);
        model.StartTask(PageKey.Job, PageKey.Steps);
        model.OpenSubView("SubView_Check");

        model.DescribeBack().Should().Be(new BackDescriptor(BackRole.CloseSubView, PageKey.Job));

        model.OpenAreaMenu();
        model.DescribeBack().Should().Be(new BackDescriptor(BackRole.CloseAreaMenu, null));

        model.ToggleAreaMenu();
        model.IsAreaMenuOpen.Should().BeFalse("区域方块、F10 再按一次就收起");
        model.DescribeBack().Role.Should().Be(BackRole.CloseSubView);
    }

    [Fact]
    public void Closing_a_sub_view_that_is_not_open_changes_nothing()
    {
        var model = new NavigationModel();

        model.CloseSubView().Should().BeFalse();
        model.CurrentPage.Should().Be(PageKey.ManualGrinding);
    }

    [Fact]
    public void Opening_a_sub_view_needs_a_key_and_closes_the_menu()
    {
        var model = new NavigationModel();
        FluentActions.Invoking(() => model.OpenSubView(string.Empty)).Should().Throw<ArgumentException>();

        model.OpenAreaMenu();
        model.OpenSubView("SubView_Compensation");
        model.IsAreaMenuOpen.Should().BeFalse();
    }

    [Fact]
    public void Back_always_has_a_defined_meaning_everywhere()
    {
        foreach (PageKey page in Enum.GetValues<PageKey>())
        {
            var model = new NavigationModel();
            model.GoTo(page);
            model.DescribeBack().Role.Should().Be(BackRole.None);

            model.OpenSubView("SubView_X");
            model.DescribeBack().Should().Be(new BackDescriptor(BackRole.CloseSubView, page));
        }
    }

    /// <summary>帮助条目（HelpViewModel.TopicKeys）：视图模型不在测试程序集里，从源码里读出来。</summary>
    private static IEnumerable<string> HelpTopics()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "ViewModels", "HelpViewModel.cs"));
        int start = source.IndexOf("TopicKeys", StringComparison.Ordinal);
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        string[] topics = System.Text.RegularExpressions.Regex.Matches(source[start..end], "\"(Help_[A-Za-z]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        topics.Should().NotBeEmpty();
        return topics;
    }

    [Fact]
    public void Every_navigation_string_exists_in_both_languages()
    {
        string[] keys = Enum.GetValues<AreaKey>().Select(AreaCatalog.TitleKey)
            .Concat(QuickBarCatalog.Known.Select(entry => entry.LabelResourceKey))
            .Concat(new[] { "Nav_BackToPageFormat", "Nav_PathSeparator", "Nav_OfflineUnavailable", "Nav_NeedsManufacturer" })
            .Concat(Enum.GetValues<MachineMode>().Select(mode => "Mode_" + mode))
            .Concat(Enum.GetValues<RollGrinder.App.Interaction.NumericEntryError>()
                .Where(error => error != RollGrinder.App.Interaction.NumericEntryError.None)
                .Select(error => "Keypad_Error_" + error))
            .Concat(Enumerable.Range(1, 5).Select(axis => "Pendant_Axis" + axis))
            .Concat(new[] { "Language_zhCN", "Language_enUS" })
            .Concat(HelpTopics().SelectMany(topic => new[] { topic + "_Title", topic + "_Body" }))
            .ToArray();

        foreach (string fileName in new[] { "Strings.resx", "Strings.en-US.resx" })
        {
            string path = Path.Combine(RepositoryLayout.Root, "src", "RollGrinder.App", "Resources", fileName);
            HashSet<string> defined = XDocument.Load(path).Root!.Elements("data")
                .Select(e => (string)e.Attribute("name")!)
                .ToHashSet(StringComparer.Ordinal);

            keys.Where(key => !defined.Contains(key)).Should().BeEmpty("{0} 里要有全部导航文字", fileName);
        }
    }
}
