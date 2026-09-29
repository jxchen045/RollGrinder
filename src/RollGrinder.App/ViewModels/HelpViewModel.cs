using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RollGrinder.App.Localization;

namespace RollGrinder.App.ViewModels;

/// <summary>帮助条目列表是按什么排的。</summary>
public enum HelpListMode
{
    /// <summary>不显示列表，只显示当前条目。</summary>
    None = 0,

    /// <summary>目录：按章节顺序。</summary>
    Contents = 1,

    /// <summary>索引：按标题字母 / 拼音顺序。</summary>
    Index = 2,

    /// <summary>搜索：标题或正文里有这个词。</summary>
    Search = 3,
}

/// <summary>帮助目录里的一条。</summary>
public sealed record HelpTopicItem(string Key, string Title, IRelayCommand Command);

/// <summary>
/// 黄色帮助（最终稿 F9、5.14）：按功能键块的"i 帮助"（或键盘 i），简图的位置换成黄色帮助，
/// 竖键换成目录、索引、搜索、全屏、退出帮助；表单仍可填。
/// 内容全在 resx（中、英两份），键名 Help_{条目}_Title / Help_{条目}_Body；先覆盖工序参数、辊形段参数、手动磨削三处。
/// </summary>
public sealed partial class HelpViewModel : ObservableObject
{
    /// <summary>全部帮助条目，按目录顺序。</summary>
    public static IReadOnlyList<string> TopicKeys { get; } = new[]
    {
        "Help_ManualGrinding",
        "Help_StepParameters",
        "Help_ProfileSegment",
        "Help_SoftKeys",
        "Help_Confirmation",
        "Help_Keypad",
    };

    private readonly IStringLocalizer localizer;

    public HelpViewModel(IStringLocalizer localizer)
    {
        this.localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Keys = new ObservableCollection<FunctionKeyViewModel>(new[]
        {
            Key("Help_Contents", () => ShowList(HelpListMode.Contents)),
            Key("Help_Index", () => ShowList(HelpListMode.Index)),
            Key("Help_Search", () => ShowList(HelpListMode.Search)),
            Key("Help_FullScreen", () => IsFullScreen = !IsFullScreen),
            FunctionKeyViewModel.Empty(localizer),
            FunctionKeyViewModel.Empty(localizer),
            FunctionKeyViewModel.Empty(localizer),
            Key("Help_Exit", Close),
        });

        int index = 0;
        foreach (FunctionKeyViewModel key in Keys)
        {
            index++;
            key.ShortcutText = key.IsPlaceholder ? null : localizer.Format("Vk_ShortcutFormat", index);
        }
    }

    /// <summary>帮助模式下的 8 个竖键（黄底）。</summary>
    public ObservableCollection<FunctionKeyViewModel> Keys { get; }

    /// <summary>当前列表里的条目。</summary>
    public ObservableCollection<HelpTopicItem> VisibleTopics { get; } = new();

    [ObservableProperty]
    private bool isOpen;

    /// <summary>全屏：帮助盖满工作区（标题行、软键不挡）。</summary>
    [ObservableProperty]
    private bool isFullScreen;

    [ObservableProperty]
    private HelpListMode listMode;

    [ObservableProperty]
    private string topicTitle = string.Empty;

    [ObservableProperty]
    private string topicBody = string.Empty;

    /// <summary>搜索词。</summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    /// <summary>显示列表（目录 / 索引 / 搜索）。</summary>
    public bool IsListShown => ListMode != HelpListMode.None;

    /// <summary>显示搜索框。</summary>
    public bool IsSearchShown => ListMode == HelpListMode.Search;

    partial void OnListModeChanged(HelpListMode value)
    {
        OnPropertyChanged(nameof(IsListShown));
        OnPropertyChanged(nameof(IsSearchShown));
        RefreshList();
    }

    partial void OnSearchTextChanged(string value) => RefreshList();

    /// <summary>进帮助：给了本画面的条目就直接显示它，否则先给目录。</summary>
    public void Open(string? topicKey)
    {
        IsOpen = true;
        if (topicKey is not null && TopicKeys.Contains(topicKey))
        {
            ShowTopic(topicKey);
        }
        else
        {
            TopicTitle = this.localizer["Help_Title"];
            TopicBody = this.localizer["Help_Intro"];
            ShowList(HelpListMode.Contents);
        }
    }

    /// <summary>退出帮助。</summary>
    public void Close()
    {
        IsOpen = false;
        IsFullScreen = false;
        ListMode = HelpListMode.None;
        SearchText = string.Empty;
    }

    private void ShowTopic(string key)
    {
        TopicTitle = this.localizer[key + "_Title"];
        TopicBody = this.localizer[key + "_Body"];
        ListMode = HelpListMode.None;
    }

    private void ShowList(HelpListMode mode) => ListMode = ListMode == mode ? HelpListMode.None : mode;

    private void RefreshList()
    {
        VisibleTopics.Clear();
        IEnumerable<HelpTopicItem> items = TopicKeys.Select(key =>
            new HelpTopicItem(key, this.localizer[key + "_Title"], new RelayCommand(() => ShowTopic(key))));

        items = ListMode switch
        {
            HelpListMode.Index => items.OrderBy(item => item.Title, StringComparer.CurrentCulture),
            HelpListMode.Search => items.Where(item => SearchText.Length == 0
                || item.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
                || this.localizer[item.Key + "_Body"].Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)),
            HelpListMode.Contents => items,
            _ => Enumerable.Empty<HelpTopicItem>(),
        };

        foreach (HelpTopicItem item in items)
        {
            VisibleTopics.Add(item);
        }
    }

    private FunctionKeyViewModel Key(string labelKey, Action action) =>
        new(labelKey, new RelayCommand(action), this.localizer, FunctionKeyKind.Help);
}
